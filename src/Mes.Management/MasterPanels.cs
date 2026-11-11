using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Shared.UI;
using Tool = Mes.Core.Tool;

namespace Mes.Management;

/// <summary>R3.2a: numeracja wyrobow. Istniejacych wyrobow nie mozna nadpisac – wybor z listy kopiuje dane do formularza, zapis tworzy nowy wyrob.</summary>
public class ProductsPanel : Panel
{
    readonly Ctx _c;
    readonly GridView _grid;
    readonly TextBox _prefix = new() { MaxLength = 50 }, _erp = new() { MaxLength = 50 }, _suffix = new() { MaxLength = 50 };
    readonly NumericStepper _len = new() { MinValue = 3, MaxValue = 6, Value = 4 };
    readonly TextArea _notes = new() { Height = 80 };
    readonly Label _preview = Ui.Lbl("", 13, true, Ui.Blue);
    readonly AttachmentsBox _att;

    public ProductsPanel(Ctx c, bool write)
    {
        _c = c;
        _grid = Ui.Grid<Product>(
            Ui.Col<Product>(L.T("Wzorzec numeru", "Number pattern"), p => p.Pattern, 260),
            Ui.Col<Product>(L.T("Przykład", "Example"), p => p.Example, 240),
            Ui.Col<Product>("Num++", p => p.NumLength.ToString(), 60),
            Ui.Col<Product>(L.T("Ostatni numer", "Last number"), p => $"{p.LastValue} / {SerialNumber.MaxValue(p.NumLength)}", 110),
            Ui.Col<Product>(L.T("Marszruta", "Routing"), p => p.HasRouting ? L.T("tak", "yes") : "—", 80),
            Ui.Col<Product>(L.T("Utworzono", "Created"), p => Ui.Local(p.CreatedAt, "yyyy-MM-dd HH:mm"), 120),
            Ui.Col<Product>(L.T("Notatki", "Notes"), p => p.Notes.Replace('\n', ' '), 300));
        _att = new AttachmentsBox(c, "PRODUCT", write);
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not Product p) return;
            _prefix.Text = p.Prefix; _erp.Text = p.ErpValue; _suffix.Text = p.Suffix; _len.Value = p.NumLength; _notes.Text = p.Notes;
            _att.Load(p.Id);
        };
        foreach (var t in new[] { _prefix, _erp, _suffix }) t.TextChanged += (_, _) => Preview();
        _len.ValueChanged += (_, _) => Preview();

        var save = Ui.Btn(L.T("Zapisz jako NOWY wyrób", "Save as NEW product"), Save); save.Enabled = write;
        var form = new GroupBox
        {
            Text = L.T("Definicja numeru seryjnego: [PREFIX][WARTOŚĆ ERP][Num++][SUFIX]", "Serial number definition: [PREFIX][ERP VALUE][Num++][SUFFIX]"), Padding = 10,
            Content = new TableLayout
            {
                Spacing = new Size(6, 8),
                Rows =
                {
                    new TableRow(Ui.Form(
                        (L.T("PREFIX (obowiązkowy)", "PREFIX (required)"), _prefix), (L.T("WARTOŚĆ ERP (opcjonalna)", "ERP VALUE (optional)"), _erp),
                        (L.T("Długość Num++ (3–6 cyfr)", "Num++ length (3–6 digits)"), _len), (L.T("SUFIX (opcjonalny, spacje dozwolone)", "SUFFIX (optional, spaces allowed)"), _suffix),
                        (L.T("Pierwszy numer", "First number"), _preview), (L.T("Notatki (do 1000 znaków)", "Notes (up to 1000 chars)"), _notes))),
                    new TableRow(Ui.Row(save, Ui.Btn(L.T("Wyczyść formularz", "Clear form"), Clear), Ui.Btn(L.T("Odśwież", "Refresh"), Reload))),
                    new TableRow(Ui.Lbl(L.T("Niedozwolone znaki: $ % ^ && ; #   •   Wybranie wyrobu z listy kopiuje jego dane – po zmianie zapisz jako nowy wyrób.",
                        "Forbidden characters: $ % ^ && ; #   •   Selecting a product copies its data – change it and save as a new product."), color: Ui.Gray)),
                }
            }
        };
        Content = new Splitter
        {
            Orientation = Orientation.Vertical, Position = 300,
            Panel1 = _grid,
            Panel2 = new Scrollable { Border = BorderType.None, Content = new TableLayout { Spacing = new Size(10, 8), Rows = { new TableRow(new TableCell(form, true), new TableCell(_att)), null } } }
        };
        Preview();
        Reload();
    }

    void Preview() => _preview.Text = "„" + SerialNumber.Build(_prefix.Text, _erp.Text, _suffix.Text, (int)_len.Value, 1) + "”";
    void Clear() { _prefix.Text = _erp.Text = _suffix.Text = _notes.Text = ""; _len.Value = 4; _att.Load(0); }
    void Reload() => Ui.Try(this, () => _grid.DataStore = _c.Master.Products(), _c.Log);

    void Save() => Ui.Try(this, () =>
    {
        var p = new Product { Prefix = _prefix.Text, ErpValue = _erp.Text, Suffix = _suffix.Text, NumLength = (int)_len.Value, Notes = _notes.Text ?? "" };
        long id = _c.Master.AddProduct(p, _c.UserId);
        Reload();
        _att.Load(id);
        Ui.Info(this, L.T($"Utworzono wyrób. Pierwszy numer: „{p.Example}”.\nMożesz teraz dodać załączniki i zdefiniować marszrutę.", $"Product created. First number: \"{p.Example}\".\nYou can now add attachments and define the routing."));
    }, _c.Log);
}

/// <summary>R3.2g: materialy – nazwa, jednostka, ilosc, wzorzec walidacji partii.</summary>
public class MaterialsPanel : Panel
{
    readonly Ctx _c;
    readonly GridView _grid;
    readonly TextBox _name = new() { MaxLength = 100 }, _unit = new() { MaxLength = 10 }, _qty = new() { Text = "1" }, _pattern = new() { MaxLength = 100 };
    long _id;

    public MaterialsPanel(Ctx c, bool write)
    {
        _c = c;
        _grid = Ui.Grid<Material>(
            Ui.Col<Material>(L.T("Nazwa", "Name"), m => m.Name, 280), Ui.Col<Material>(L.T("Jednostka", "Unit"), m => m.Unit, 90),
            Ui.Col<Material>(L.T("Ilość domyślna", "Default qty"), m => Ui.Dec(m.DefaultQty), 110), Ui.Col<Material>(L.T("Wzorzec partii", "Batch pattern"), m => m.BatchPattern, 200));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not Material m) return;
            _id = m.Id; _name.Text = m.Name; _unit.Text = m.Unit; _qty.Text = Ui.Dec(m.DefaultQty); _pattern.Text = m.BatchPattern;
        };
        var save = Ui.Btn(L.T("Zapisz", "Save"), Save); save.Enabled = write;
        var form = new GroupBox
        {
            Text = L.T("Materiał", "Material"), Padding = 10, Width = 460,
            Content = new TableLayout
            {
                Spacing = new Size(6, 8),
                Rows =
                {
                    new TableRow(Ui.Form((L.T("Nazwa", "Name"), _name), (L.T("Jednostka miary", "Unit of measure"), _unit), (L.T("Ilość domyślna", "Default quantity"), _qty),
                        (L.T("Wzorzec partii (np. KLEJ*, puste = dowolny)", "Batch pattern (e.g. KLEJ*, empty = any)"), _pattern))),
                    new TableRow(Ui.Row(save, Ui.Btn(L.T("Nowy", "New"), New))), null
                }
            }
        };
        Content = new TableLayout { Spacing = new Size(10, 8), Rows = { new TableRow(new TableCell(_grid, true), new TableCell(form)) } };
        Reload();
    }

    void New() { _id = 0; _name.Text = _unit.Text = _pattern.Text = ""; _qty.Text = "1"; _grid.UnselectAll(); _name.Focus(); }
    void Reload() => Ui.Try(this, () => _grid.DataStore = _c.Master.Materials(), _c.Log);
    void Save() => Ui.Try(this, () =>
    {
        var q = Ui.Dec(_qty.Text) ?? throw new MesException("Ilość musi być liczbą.", "Quantity must be a number.");
        _c.Master.SaveMaterial(new Material { Id = _id, Name = _name.Text.Trim(), Unit = _unit.Text.Trim(), DefaultQty = q, BatchPattern = _pattern.Text.Trim() });
        Reload(); New();
    }, _c.Log);
}

/// <summary>R3.2h: narzedzia – numery seryjne, daty waznosci (czasowki), przelacznik aktywny, swiadectwa wzorcowania.</summary>
public class ToolsPanel : Panel
{
    readonly Ctx _c;
    readonly GridView _grid;
    readonly TextBox _sn = new() { MaxLength = 100 }, _name = new() { MaxLength = 100 };
    readonly DateTimePicker _from = new() { Mode = DateTimePickerMode.Date }, _to = new() { Mode = DateTimePickerMode.Date };
    readonly CheckBox _noFrom = new(), _noTo = new(), _active = new() { Checked = true };
    readonly AttachmentsBox _att;
    long _id;

    public ToolsPanel(Ctx c, bool write)
    {
        _c = c;
        _noFrom.Text = _noTo.Text = L.T("bez daty", "no date");
        _active.Text = L.T("Aktywne", "Active");
        _grid = Ui.Grid<Tool>(
            Ui.Col<Tool>(L.T("Numer seryjny (ToolID)", "Serial number (ToolID)"), t => t.ToolSn, 220), Ui.Col<Tool>(L.T("Nazwa", "Name"), t => t.Name, 220),
            Ui.Col<Tool>(L.T("Ważne od", "Valid from"), t => t.ValidFrom?.ToString("yyyy-MM-dd") ?? "—", 100), Ui.Col<Tool>(L.T("Ważne do", "Valid to"), t => t.ValidTo?.ToString("yyyy-MM-dd") ?? "—", 100),
            Ui.Col<Tool>(L.T("Aktywne", "Active"), t => t.IsActive ? L.T("tak", "yes") : L.T("NIE", "NO"), 80),
            Ui.Col<Tool>(L.T("Dziś", "Today"), t => t.IsValidOn(DateTime.Today) ? L.T("ważne", "valid") : L.T("NIEWAŻNE", "INVALID"), 100));
        _att = new AttachmentsBox(c, "TOOL", write);
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not Tool t) return;
            _id = t.Id; _sn.Text = t.ToolSn; _name.Text = t.Name; _active.Checked = t.IsActive;
            _noFrom.Checked = t.ValidFrom == null; _noTo.Checked = t.ValidTo == null;
            _from.Value = t.ValidFrom ?? DateTime.Today; _to.Value = t.ValidTo ?? DateTime.Today.AddYears(1);
            _att.Load(t.Id);
        };
        var save = Ui.Btn(L.T("Zapisz", "Save"), Save); save.Enabled = write;
        var form = new GroupBox
        {
            Text = L.T("Narzędzie / urządzenie pomiarowe", "Tool / measuring device"), Padding = 10, Width = 480,
            Content = new TableLayout
            {
                Spacing = new Size(6, 8),
                Rows =
                {
                    new TableRow(Ui.Form((L.T("Numer seryjny", "Serial number"), _sn), (L.T("Nazwa", "Name"), _name),
                        (L.T("Ważne od", "Valid from"), Ui.Row(_from, _noFrom)), (L.T("Ważne do", "Valid to"), Ui.Row(_to, _noTo)), ("", _active))),
                    new TableRow(Ui.Row(save, Ui.Btn(L.T("Nowe", "New"), New))),
                    new TableRow(_att), null
                }
            }
        };
        Content = new TableLayout { Spacing = new Size(10, 8), Rows = { new TableRow(new TableCell(_grid, true), new TableCell(form)) } };
        New();
        Reload();
    }

    void New()
    {
        _id = 0; _sn.Text = _name.Text = ""; _active.Checked = true; _noFrom.Checked = true; _noTo.Checked = true;
        _from.Value = DateTime.Today; _to.Value = DateTime.Today.AddYears(1); _grid.UnselectAll(); _att.Load(0);
    }
    void Reload() => Ui.Try(this, () => _grid.DataStore = _c.Master.Tools(), _c.Log);
    void Save() => Ui.Try(this, () =>
    {
        var t = new Tool
        {
            Id = _id, ToolSn = _sn.Text.Trim(), Name = _name.Text.Trim(), IsActive = _active.Checked == true,
            ValidFrom = _noFrom.Checked == true ? null : _from.Value, ValidTo = _noTo.Checked == true ? null : _to.Value
        };
        _c.Master.SaveTool(t);
        _id = t.Id;
        Reload();
        _att.Load(t.Id);
    }, _c.Log);
}

/// <summary>R3.2i: uzytkownicy i role.</summary>
public class UsersPanel : Panel
{
    readonly Ctx _c;
    readonly GridView _grid;
    readonly TextBox _login = new() { MaxLength = 50 }, _name = new() { MaxLength = 100 };
    readonly PasswordBox _pwd = new();
    readonly DropDown _role;
    readonly CheckBox _active = new() { Checked = true };
    long _id;

    public UsersPanel(Ctx c, bool write)
    {
        _c = c;
        _active.Text = L.T("Aktywny", "Active");
        _role = Mx.Drop(Roles.All.Select(r => (r, Roles.Name(r))));
        _grid = Ui.Grid<AppUser>(
            Ui.Col<AppUser>("Login", u => u.Login, 180), Ui.Col<AppUser>(L.T("Nazwa", "Name"), u => u.FullName, 240),
            Ui.Col<AppUser>(L.T("Rola", "Role"), u => Roles.Name(u.Role), 140), Ui.Col<AppUser>(L.T("Aktywny", "Active"), u => u.IsActive ? L.T("tak", "yes") : L.T("NIE", "NO"), 80),
            Ui.Col<AppUser>(L.T("Zmiana hasła", "Pwd change"), u => u.MustChangePwd ? L.T("wymagana", "required") : "", 110),
            Ui.Col<AppUser>(L.T("Blokada do", "Locked until"), u => u.LockedUntil > DateTime.UtcNow ? Ui.Local(u.LockedUntil, "HH:mm:ss") : "", 100));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not AppUser u) return;
            _id = u.Id; _login.Text = u.Login; _login.ReadOnly = true; _name.Text = u.FullName; _role.SelectedKey = u.Role; _active.Checked = u.IsActive; _pwd.Text = "";
        };
        var save = Ui.Btn(L.T("Zapisz", "Save"), Save); save.Enabled = write;
        var form = new GroupBox
        {
            Text = L.T("Użytkownik", "User"), Padding = 10, Width = 480,
            Content = new TableLayout
            {
                Spacing = new Size(6, 8),
                Rows =
                {
                    new TableRow(Ui.Form(("Login (UserID)", _login), (L.T("Imię i nazwisko", "Full name"), _name), (L.T("Rola", "Role"), _role),
                        (L.T("Hasło (puste = bez zmiany)", "Password (empty = unchanged)"), _pwd), ("", _active))),
                    new TableRow(Ui.Row(save, Ui.Btn(L.T("Nowy", "New"), New))),
                    new TableRow(Ui.Lbl(L.T("Nowe lub zresetowane hasło wymusza zmianę przy pierwszym logowaniu.\nZapis zdejmuje blokadę konta po nieudanych logowaniach.\n\nProdukcja – tylko SpecAPP.  Planista – wyroby i zlecenia.\nTechnolog – marszruty.  Jakość – moduł jakości i eksport.\nMasterDATA – pełny dostęp.",
                        "A new or reset password forces a change at first login.\nSaving clears the lock after failed logins.\n\nProduction – SpecAPP only.  Planner – products and orders.\nTechnologist – routings.  Quality – quality module and export.\nMasterDATA – full access."), color: Ui.Gray)),
                    null
                }
            }
        };
        Content = new TableLayout { Spacing = new Size(10, 8), Rows = { new TableRow(new TableCell(_grid, true), new TableCell(form)) } };
        Reload();
    }

    void New() { _id = 0; _login.Text = _name.Text = _pwd.Text = ""; _login.ReadOnly = false; _active.Checked = true; _role.SelectedKey = Roles.Production; _grid.UnselectAll(); _login.Focus(); }
    void Reload() => Ui.Try(this, () => _grid.DataStore = _c.Master.Users(), _c.Log);
    void Save() => Ui.Try(this, () =>
    {
        var u = new AppUser { Id = _id, Login = _login.Text.Trim(), FullName = _name.Text.Trim(), Role = _role.SelectedKey, IsActive = _active.Checked == true };
        if (_id == 0) _c.Master.AddUser(u, _pwd.Text, _c.UserId);
        else _c.Master.UpdateUser(u, _pwd.Text, _c.UserId);
        Reload(); New();
    }, _c.Log);
}
