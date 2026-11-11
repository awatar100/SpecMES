using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Mes.Core;
using Npgsql;

namespace Mes.Data;

/// <summary>Fabryka krotkich polaczen (pula ograniczona – 1000 stanowisk, limit polaczen PostgreSQL).</summary>
public class Db
{
    readonly string _cs;
    public string Schema { get; }
    public string Host { get; }

    static Db() { DefaultTypeMap.MatchNamesWithUnderscores = true; }

    public Db(SetupConfig c, int maxPool = 2, string app = "MES")
    {
        Schema = c.Schema; Host = c.Host;
        _cs = new NpgsqlConnectionStringBuilder
        {
            Host = c.Host, Port = c.Port, Database = c.Database, Username = c.User, Password = c.Password,
            SearchPath = c.Schema, MaxPoolSize = maxPool, MinPoolSize = 0, Timeout = 5, CommandTimeout = 20,
            ConnectionIdleLifetime = 20, ApplicationName = app, IncludeErrorDetail = true
        }.ConnectionString;
    }

    public NpgsqlConnection Open()
    {
        var c = new NpgsqlConnection(_cs);
        c.Open();
        return c;
    }

    public bool Ping()
    {
        try { using var c = Open(); c.ExecuteScalar<int>("select 1"); return true; }
        catch { return false; }
    }

    /// <summary>Czy blad oznacza utrate komunikacji (a nie blad danych) – decyduje o buforowaniu.</summary>
    public static bool IsConnectionError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is PostgresException pg)
                return pg.SqlState.StartsWith("08") || pg.SqlState.StartsWith("57P") || pg.SqlState == "53300";
            if (e is SocketException or TimeoutException or IOException) return true;
        }
        return ex is NpgsqlException;
    }

    public static void Audit(NpgsqlConnection c, NpgsqlTransaction? tx, long? userId, long? stationId, string action, string entity, long? entityId, object? details = null) =>
        c.Execute("insert into audit_log(user_id, station_id, action, entity, entity_id, details) values (@userId,@stationId,@action,@entity,@entityId,@d::jsonb)",
            new { userId, stationId, action, entity, entityId, d = details == null ? null : System.Text.Json.JsonSerializer.Serialize(details) }, tx);
}

/// <summary>Tworzenie struktury bazy: schemat, role, tabele, konto MasterDATA (R3.3, R2.13).</summary>
public static class SchemaInstaller
{
    public const string MasterLogin = "MasterDATA", MasterPassword = "DATA";

    public static string AdminConnString(string host, int port, string database, string user, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = database, Username = user, Password = password,
            Timeout = 8, CommandTimeout = 60, Pooling = false, ApplicationName = "MES Installer"
        }.ConnectionString;

    public static void ValidateSchemaName(string schema)
    {
        if (!Regex.IsMatch(schema, "^[a-z][a-z0-9]{4}$"))
            throw new MesException("Prefiks bazy musi mieć dokładnie 5 znaków: mała litera, potem litery lub cyfry (np. prod1).",
                "The database prefix must be exactly 5 characters: a lowercase letter followed by letters or digits (e.g. prod1).");
    }

    static void ValidateRoleName(string name)
    {
        if (!Regex.IsMatch(name, "^[a-z_][a-z0-9_]{2,40}$"))
            throw new MesException($"Nazwa użytkownika bazy „{name}” jest niepoprawna (małe litery, cyfry, _).", $"Database user name \"{name}\" is invalid (lowercase letters, digits, _).");
    }

    public static bool SchemaExists(string adminCs, string schema)
    {
        using var c = new NpgsqlConnection(adminCs); c.Open();
        return c.ExecuteScalar<bool>("select exists(select 1 from pg_namespace where nspname=@schema)", new { schema });
    }

    static string Lit(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Calosc w jednej transakcji: niepowodzenie nie zostawia zadnych obiektow.</summary>
    public static void Install(string adminCs, string schema, string prodUser, string prodPassword, string mgmtUser, string mgmtPassword, Action<string>? log = null)
    {
        ValidateSchemaName(schema); ValidateRoleName(prodUser); ValidateRoleName(mgmtUser);
        if (prodUser == mgmtUser) throw new MesException("Użytkownik produkcji i zarządzania muszą się różnić.", "Production and management users must differ.");
        if (prodPassword.Length < 8 || mgmtPassword.Length < 8)
            throw new MesException("Hasła użytkowników bazy muszą mieć co najmniej 8 znaków.", "Database user passwords must have at least 8 characters.");
        string owner = schema + "_owner";

        using var c = new NpgsqlConnection(adminCs); c.Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<bool>("select exists(select 1 from pg_namespace where nspname=@schema)", new { schema }, tx))
            throw new MesException($"Schemat „{schema}” już istnieje – instalator nie nadpisuje istniejącej bazy.", $"Schema \"{schema}\" already exists – the installer never overwrites an existing database.");
        foreach (var r in new[] { owner, prodUser, mgmtUser })
            if (c.ExecuteScalar<bool>("select exists(select 1 from pg_roles where rolname=@r)", new { r }, tx))
                throw new MesException($"Rola bazodanowa „{r}” już istnieje.", $"Database role \"{r}\" already exists.");

        log?.Invoke("Tworzenie ról / Creating roles…");
        c.Execute($"create role {owner} nologin", transaction: tx);
        c.Execute($"create role {prodUser} login password {Lit(prodPassword)}", transaction: tx);
        c.Execute($"create role {mgmtUser} login password {Lit(mgmtPassword)}", transaction: tx);
        c.Execute($"grant {owner} to current_user", transaction: tx);
        c.Execute($"create schema {schema} authorization {owner}", transaction: tx);

        log?.Invoke("Tworzenie tabel / Creating tables…");
        c.Execute($"set local role {owner}", transaction: tx);
        c.Execute($"set local search_path to {schema}", transaction: tx);
        c.Execute(LoadScript("001_init.sql").Replace("{S}", schema), transaction: tx);

        log?.Invoke("Nadawanie uprawnień / Granting privileges…");
        string[] appendOnly = { "step_result", "step_result_value", "material_usage", "consumed_part", "audit_log" };
        var sql = new List<string>
        {
            $"revoke all on schema {schema} from public",
            $"revoke all on all functions in schema {schema} from public",
            $"grant usage on schema {schema} to {prodUser}, {mgmtUser}",
            $"grant execute on all functions in schema {schema} to {prodUser}, {mgmtUser}",
            // Zarzadzanie: pelny dostep do danych podstawowych; wyrob bez UPDATE/DELETE (Z1); wyniki i audyt tylko dopisywane
            $"grant select on all tables in schema {schema} to {mgmtUser}",
            $"grant insert, update, delete on app_user, attachment, material, tool, routing, routing_step, routing_step_slot, routing_step_material to {mgmtUser}",
            $"grant insert on product, order_step, order_step_slot, order_step_material, unit, audit_log, station to {mgmtUser}",
            $"grant insert, update on serial_counter, work_order to {mgmtUser}",
            $"grant update on unit, station to {mgmtUser}",
            // Produkcja: minimalne uprawnienia, bez DELETE
            $"grant select on work_order, order_step, order_step_slot, order_step_material, material, tool, unit, station, step_result to {prodUser}",
            $"grant insert on {string.Join(", ", appendOnly)}, station to {prodUser}",
            $"grant update (current_step_no, status, version, locked_station_id, locked_until) on unit to {prodUser}",
            $"grant update (last_seen) on station to {prodUser}",
        };
        foreach (var s in sql) c.Execute(s, transaction: tx);

        c.Execute("insert into app_user(login, full_name, password_hash, role, must_change_pwd) values (@l, 'Master Data', @h, 'MASTERDATA', true)",
            new { l = MasterLogin, h = PasswordHasher.Hash(MasterPassword) }, tx);
        c.Execute("reset role", transaction: tx);
        tx.Commit();
        log?.Invoke("Gotowe / Done.");
    }

    /// <summary>Usuniecie schematu i rol – tylko do testow.</summary>
    public static void Drop(string adminCs, string schema, params string[] roles)
    {
        ValidateSchemaName(schema);
        using var c = new NpgsqlConnection(adminCs); c.Open();
        c.Execute($"drop schema if exists {schema} cascade");
        foreach (var r in roles.Append(schema + "_owner"))
        {
            ValidateRoleName(r);
            c.Execute($"drop role if exists {r}");
        }
    }

    static string LoadScript(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing embedded script " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
