using System.Security.Cryptography;
using Dapper;
using Mes.Core;
using Npgsql;

namespace Mes.Data;

/// <summary>Logowanie uzytkownikow aplikacji (wspolne dla ProdAPP i Zarzadzania).</summary>
public class AuthRepo
{
    readonly Db _db;
    public AuthRepo(Db db) { _db = db; }

    static string DeriveFor(NpgsqlConnection c, string login, string password)
    {
        var salt = c.ExecuteScalar<string?>("select mes_login_salt(@login)", new { login });
        if (string.IsNullOrEmpty(salt)) return "-";
        var p = salt.Split('$');
        return PasswordHasher.Derive(password, int.Parse(p[0]), p[1]);
    }

    public LoginResult Login(string login, string password)
    {
        using var c = _db.Open();
        var r = c.QuerySingle<LoginResult>("select * from mes_login(@login, @hash)", new { login, hash = DeriveFor(c, login, password) });
        r.Login = login;
        return r;
    }

    public void ChangePassword(string login, string oldPassword, string newPassword)
    {
        ValidatePassword(newPassword);
        using var c = _db.Open();
        bool ok = c.ExecuteScalar<bool>("select mes_change_password(@login, @old, @full)",
            new { login, old = DeriveFor(c, login, oldPassword), full = PasswordHasher.Hash(newPassword) });
        if (!ok) throw new MesException("Nie udało się zmienić hasła (błędne stare hasło).", "Password change failed (wrong old password).");
    }

    public static void ValidatePassword(string pwd)
    {
        if (pwd.Length < 4) throw new MesException("Hasło musi mieć co najmniej 4 znaki.", "Password must have at least 4 characters.");
    }
}

/// <summary>Dane podstawowe: uzytkownicy, wyroby, materialy, narzedzia, zalaczniki.</summary>
public class MasterRepo
{
    readonly Db _db;
    public MasterRepo(Db db) { _db = db; }

    // ---- uzytkownicy
    public List<AppUser> Users()
    {
        using var c = _db.Open();
        return c.Query<AppUser>("select id, login, full_name, role, is_active, must_change_pwd, locked_until from app_user order by login").ToList();
    }

    public void AddUser(AppUser u, string password, long byUser)
    {
        TextRules.Require(u.Login, "Login", "Login", 50);
        TextRules.Require(u.FullName, "Nazwa", "Name", 100, false);
        AuthRepo.ValidatePassword(password);
        if (!Roles.All.Contains(u.Role)) throw new MesException("Nieznana rola.", "Unknown role.");
        using var c = _db.Open();
        try
        {
            long id = c.ExecuteScalar<long>("insert into app_user(login, full_name, password_hash, role, is_active, must_change_pwd) values (@Login,@FullName,@h,@Role,@IsActive,true) returning id",
                new { u.Login, u.FullName, h = PasswordHasher.Hash(password), u.Role, u.IsActive });
            Db.Audit(c, null, byUser, null, "USER_ADD", "app_user", id, new { u.Login, u.Role });
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        { throw new MesException($"Login „{u.Login}” już istnieje.", $"Login \"{u.Login}\" already exists."); }
    }

    public void UpdateUser(AppUser u, string? newPassword, long byUser)
    {
        TextRules.Require(u.FullName, "Nazwa", "Name", 100, false);
        if (!Roles.All.Contains(u.Role)) throw new MesException("Nieznana rola.", "Unknown role.");
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        if (u.Role != Roles.MasterData || !u.IsActive)
        {
            int admins = c.ExecuteScalar<int>("select count(*) from app_user where role='MASTERDATA' and is_active and id<>@Id", new { u.Id }, tx);
            if (admins == 0) throw new MesException("W systemie musi pozostać co najmniej jeden aktywny użytkownik MasterDATA.", "At least one active MasterDATA user must remain.");
        }
        c.Execute("update app_user set full_name=@FullName, role=@Role, is_active=@IsActive, failed_logins=0, locked_until=null where id=@Id", u, tx);
        if (!string.IsNullOrEmpty(newPassword))
        {
            AuthRepo.ValidatePassword(newPassword);
            c.Execute("update app_user set password_hash=@h, must_change_pwd=true where id=@Id", new { h = PasswordHasher.Hash(newPassword), u.Id }, tx);
        }
        Db.Audit(c, tx, byUser, null, "USER_UPDATE", "app_user", u.Id, new { u.Role, u.IsActive, passwordReset = !string.IsNullOrEmpty(newPassword) });
        tx.Commit();
    }

    // ---- wyroby (tylko dodawanie, Z1)
    public List<Product> Products()
    {
        using var c = _db.Open();
        return c.Query<Product>(@"select p.*, coalesce(sc.last_value,0) last_value, exists(select 1 from routing r join routing_step rs on rs.routing_id=r.id where r.product_id=p.id) has_routing
            from product p left join serial_counter sc on sc.product_id=p.id order by p.prefix, p.erp_value, p.suffix, p.num_length").ToList();
    }

    public long AddProduct(Product p, long byUser)
    {
        SerialNumber.ValidateDefinition(p.Prefix, p.ErpValue, p.Suffix, p.NumLength);
        if ((p.Notes ?? "").Length > 1000) throw new MesException("Notatki mogą mieć najwyżej 1000 znaków.", "Notes may have at most 1000 characters.");
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        try
        {
            long id = c.ExecuteScalar<long>("insert into product(prefix, erp_value, suffix, num_length, notes, created_by) values (@Prefix,@ErpValue,@Suffix,@NumLength,@Notes,@byUser) returning id",
                new { p.Prefix, ErpValue = p.ErpValue ?? "", Suffix = p.Suffix ?? "", p.NumLength, Notes = p.Notes ?? "", byUser }, tx);
            c.Execute("insert into serial_counter(product_id, last_value) values (@id, 0)", new { id }, tx);
            Db.Audit(c, tx, byUser, null, "PRODUCT_ADD", "product", id, new { p.Prefix, p.ErpValue, p.Suffix, p.NumLength });
            tx.Commit();
            return id;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new MesException("Wyrób o takiej kombinacji PREFIX + WARTOŚĆ ERP + SUFIX + długość Num++ już istnieje. Istniejących wyrobów nie można nadpisywać.",
                "A product with this PREFIX + ERP VALUE + SUFFIX + Num++ length already exists. Existing products cannot be overwritten.");
        }
    }

    // ---- materialy
    public List<Material> Materials()
    {
        using var c = _db.Open();
        return c.Query<Material>("select * from material order by name").ToList();
    }

    public void SaveMaterial(Material m)
    {
        TextRules.Require(m.Name, "Nazwa materiału", "Material name", 100);
        TextRules.Require(m.Unit, "Jednostka", "Unit", 10, false);
        if ((m.BatchPattern ?? "").Length > 100) throw new MesException("Wzorzec partii jest za długi.", "Batch pattern is too long.");
        if (m.DefaultQty < 0) throw new MesException("Ilość nie może być ujemna.", "Quantity cannot be negative.");
        using var c = _db.Open();
        try
        {
            if (m.Id == 0)
                m.Id = c.ExecuteScalar<long>("insert into material(name, unit, default_qty, batch_pattern) values (@Name,@Unit,@DefaultQty,@BatchPattern) returning id", m);
            else
                c.Execute("update material set name=@Name, unit=@Unit, default_qty=@DefaultQty, batch_pattern=@BatchPattern where id=@Id", m);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        { throw new MesException($"Materiał „{m.Name}” już istnieje.", $"Material \"{m.Name}\" already exists."); }
    }

    // ---- narzedzia
    public List<Tool> Tools()
    {
        using var c = _db.Open();
        return c.Query<Tool>("select * from tool order by tool_sn").ToList();
    }

    public void SaveTool(Tool t)
    {
        TextRules.Require(t.ToolSn, "Numer seryjny narzędzia", "Tool serial number", 100);
        TextRules.Require(t.Name, "Nazwa", "Name", 100, false);
        if (t.ValidFrom != null && t.ValidTo != null && t.ValidFrom > t.ValidTo)
            throw new MesException("Data „ważne od” jest późniejsza niż „ważne do”.", "\"Valid from\" is later than \"valid to\".");
        using var c = _db.Open();
        try
        {
            var p = new { t.Id, t.ToolSn, t.Name, f = t.ValidFrom?.ToString("yyyy-MM-dd"), to = t.ValidTo?.ToString("yyyy-MM-dd"), t.IsActive };
            if (t.Id == 0)
                t.Id = c.ExecuteScalar<long>("insert into tool(tool_sn, name, valid_from, valid_to, is_active) values (@ToolSn,@Name,@f::date,@to::date,@IsActive) returning id", p);
            else
                c.Execute("update tool set tool_sn=@ToolSn, name=@Name, valid_from=@f::date, valid_to=@to::date, is_active=@IsActive where id=@Id", p);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        { throw new MesException($"Narzędzie „{t.ToolSn}” już istnieje.", $"Tool \"{t.ToolSn}\" already exists."); }
    }

    // ---- zalaczniki (Z38: do 10 MB, bytea, SHA-256)
    public const int MaxAttachment = 10 * 1024 * 1024;

    public List<AttachmentInfo> Attachments(string ownerType, long ownerId)
    {
        using var c = _db.Open();
        return c.Query<AttachmentInfo>("select id, file_name, mime, sha256, octet_length(content) as size, created_at from attachment where owner_type=@ownerType and owner_id=@ownerId order by id",
            new { ownerType, ownerId }).ToList();
    }

    public void AddAttachment(string ownerType, long ownerId, string fileName, byte[] content, long byUser)
    {
        if (content.Length > MaxAttachment)
            throw new MesException("Załącznik przekracza limit 10 MB.", "The attachment exceeds the 10 MB limit.");
        if (content.Length == 0) throw new MesException("Plik jest pusty.", "The file is empty.");
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        string mime = ext switch { ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".txt" => "text/plain", _ => "application/octet-stream" };
        using var c = _db.Open();
        c.Execute("insert into attachment(owner_type, owner_id, file_name, mime, sha256, content, created_by) values (@ownerType,@ownerId,@fn,@mime,@sha,@content,@byUser)",
            new { ownerType, ownerId, fn = Path.GetFileName(fileName), mime, sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), content, byUser });
    }

    public (string FileName, byte[] Content) GetAttachment(long id)
    {
        using var c = _db.Open();
        var r = c.QuerySingle<(string, byte[])>("select file_name, content from attachment where id=@id", new { id });
        return r;
    }

    public void DeleteAttachment(long id)
    {
        using var c = _db.Open();
        c.Execute("delete from attachment where id=@id", new { id });
    }
}
