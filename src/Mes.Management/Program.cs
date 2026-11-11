using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Data;
using Mes.Shared.UI;

namespace Mes.Management;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var log = new FileLogger(Path.Combine(Ui.AppDir, "logs"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled exception", e.ExceptionObject as Exception);
        Ui.LoadLanguage();
        var app = new Application(Eto.Platform.Detect);
        app.UnhandledException += (_, e) => log.Error("Unhandled UI exception", e.ExceptionObject as Exception);
        var cfg = Ui.LoadSetup();
        if (cfg == null) return;
        app.Run(new MainForm(new Db(cfg, 4, "SpecManagment"), log));
    }
}

/// <summary>Wspolny kontekst modulow.</summary>
public class Ctx
{
    public Db Db; public FileLogger Log; public LoginResult User;
    public AuthRepo Auth; public MasterRepo Master; public RoutingRepo Routing; public OrderRepo Orders; public QualityRepo Quality; public ReportRepo Reports;
    public long UserId => User.UserId!.Value;
    public Ctx(Db db, FileLogger log)
    {
        Db = db; Log = log;
        Auth = new AuthRepo(db); Master = new MasterRepo(db); Routing = new RoutingRepo(db);
        Orders = new OrderRepo(db); Quality = new QualityRepo(db); Reports = new ReportRepo(db);
    }
}

/// <summary>Pomocniki list rozwijanych i pol liczbowych.</summary>
public static class Mx
{
    public static DropDown Drop(IEnumerable<(string Key, string Text)> items, string? selected = null)
    {
        var d = new DropDown();
        foreach (var (k, t) in items) d.Items.Add(new ListItem { Key = k, Text = t });
        if (selected != null) d.SelectedKey = selected;
        else if (d.Items.Count > 0) d.SelectedIndex = 0;
        return d;
    }

    public static long? KeyId(this DropDown d) => long.TryParse(d.SelectedKey, out var v) ? v : null;

    public static int Int(string? s, string pl, string en) =>
        int.TryParse((s ?? "").Trim(), out var v) ? v : throw new MesException($"Pole „{pl}” musi być liczbą całkowitą.", $"Field \"{en}\" must be an integer.");

    public static string SaveDialog(Control parent, string fileName, string ext)
    {
        var dlg = new SaveFileDialog { FileName = fileName };
        dlg.Filters.Add(new FileFilter(ext.ToUpperInvariant(), "." + ext));
        if (dlg.ShowDialog(parent) != DialogResult.Ok) return "";
        return dlg.FileName.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) ? dlg.FileName : dlg.FileName + "." + ext;
    }
}

public class MainForm : Form
{
    readonly Ctx _ctx;

    public MainForm(Db db, FileLogger log)
    {
        _ctx = new Ctx(db, log);
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(900, 600);
        L.Changed += () => Application.Instance.AsyncInvoke(() => { if (_ctx.User != null) Build(); });
        Shown += (_, _) => Login();
        Ui.Brand(this);
        Title = "SpecManagment – " + L.T("Zarządzanie produkcją", "Production Management");
    }

    void Login()
    {
        Content = null;
        _ctx.User = null!;
        var r = new LoginDialog(_ctx.Auth, "SpecManagment", Permissions.CanUseManagement).ShowModal(this);
        if (r == null) { Application.Instance.Quit(); return; }
        _ctx.User = r;
        _ctx.Log.Info($"Login: {r.Login} ({r.Role})");
        Build();
    }

    void Build()
    {
        Title = "SpecManagment – " + L.T("Zarządzanie produkcją", "Production Management");
        string role = _ctx.User.Role!;
        var tabs = new TabControl();
        void Add(Module m, string title, Func<bool, Control> make)
        {
            var a = Permissions.Get(role, m);
            if (a == Access.None) return;
            Control content;
            try { content = make(a == Access.Write); }
            catch (Exception ex) { _ctx.Log.Error("Module load failed: " + m, ex); content = Ui.Lbl(Ui.Describe(ex), color: Ui.Red); }
            tabs.Pages.Add(new TabPage { Text = title + (a == Access.Read ? L.T(" (podgląd)", " (read-only)") : ""), Content = content, Padding = 10 });
        }
        Add(Module.Products, L.T("Wyroby", "Products"), w => new ProductsPanel(_ctx, w));
        Add(Module.Routing, L.T("Marszruty", "Routings"), w => new RoutingPanel(_ctx, w));
        Add(Module.Orders, L.T("Zlecenia", "Orders"), w => new OrdersPanel(_ctx, w));
        Add(Module.Kpi, L.T("Dashboard KPI", "KPI Dashboard"), w => new KpiPanel(_ctx));
        Add(Module.Quality, L.T("Jakość", "Quality"), w => new QualityPanel(_ctx, w));
        Add(Module.Export, L.T("Eksport", "Export"), w => new ExportPanel(_ctx));
        Add(Module.Materials, L.T("Materiały", "Materials"), w => new MaterialsPanel(_ctx, w));
        Add(Module.Tools, L.T("Narzędzia", "Tools"), w => new ToolsPanel(_ctx, w));
        Add(Module.Users, L.T("Użytkownicy", "Users"), w => new UsersPanel(_ctx, w));

        var top = new TableLayout
        {
            Spacing = new Size(12, 4),
            Rows = { new TableRow(
                new TableCell(Ui.Lbl("SpecManagment – " + L.T("Zarządzanie produkcją", "Production Management"), 14, true)),
                new TableCell(Ui.Lbl($"{L.T("Użytkownik", "User")}: {_ctx.User.Login}  •  {Roles.Name(role)}  •  {L.T("baza", "database")} {_ctx.Db.Host}/{_ctx.Db.Schema}", color: Ui.Gray), true),
                new TableCell(Ui.LanguageButton()),
                new TableCell(Ui.Btn(L.T("Wyloguj", "Log out"), Login)),
                new TableCell(Ui.AboutButton(this, "SpecManagment"))) }
        };
        Content = new TableLayout { Padding = 10, Spacing = new Size(6, 8), Rows = { new TableRow(top), new TableRow(tabs) { ScaleHeight = true } } };
    }
}

/// <summary>Lista zalacznikow obiektu (wyrob / narzedzie) – pliki do 10 MB w bazie.</summary>
public class AttachmentsBox : Panel
{
    readonly Ctx _c; readonly string _type; readonly bool _write;
    long _ownerId;
    readonly GridView _grid;

    public AttachmentsBox(Ctx c, string ownerType, bool write)
    {
        _c = c; _type = ownerType; _write = write;
        _grid = Ui.Grid<AttachmentInfo>(
            Ui.Col<AttachmentInfo>(L.T("Plik", "File"), a => a.FileName, 220),
            Ui.Col<AttachmentInfo>(L.T("Rozmiar", "Size"), a => (a.Size / 1024.0).ToString("0.0") + " kB", 80),
            Ui.Col<AttachmentInfo>(L.T("Dodano", "Added"), a => Ui.Local(a.CreatedAt, "yyyy-MM-dd HH:mm"), 120));
        _grid.Height = 110;
        var add = Ui.Btn(L.T("Dodaj plik…", "Add file…"), AddFile); add.Enabled = write;
        var del = Ui.Btn(L.T("Usuń", "Delete"), Delete); del.Enabled = write;
        Content = new GroupBox
        {
            Text = L.T("Załączniki (do 10 MB)", "Attachments (up to 10 MB)"), Padding = 8,
            Content = new TableLayout { Spacing = new Size(6, 6), Rows = { new TableRow(_grid), new TableRow(Ui.Row(add, Ui.Btn(L.T("Zapisz na dysk…", "Save to disk…"), SaveFile), del)) } }
        };
    }

    public void Load(long ownerId)
    {
        _ownerId = ownerId;
        Ui.Try(this, () => _grid.DataStore = ownerId == 0 ? new List<AttachmentInfo>() : _c.Master.Attachments(_type, ownerId), _c.Log);
    }

    void AddFile()
    {
        if (_ownerId == 0) { Ui.Info(this, L.T("Najpierw wybierz lub zapisz pozycję.", "Select or save an item first.")); return; }
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog(this) != DialogResult.Ok) return;
        Ui.Try(this, () =>
        {
            var fi = new FileInfo(dlg.FileName);
            if (fi.Length > MasterRepo.MaxAttachment) throw new MesException("Załącznik przekracza limit 10 MB.", "The attachment exceeds the 10 MB limit.");
            _c.Master.AddAttachment(_type, _ownerId, fi.Name, File.ReadAllBytes(fi.FullName), _c.UserId);
            Load(_ownerId);
        }, _c.Log);
    }

    void SaveFile()
    {
        if (_grid.SelectedItem is not AttachmentInfo a) return;
        var dlg = new SaveFileDialog { FileName = a.FileName };
        if (dlg.ShowDialog(this) != DialogResult.Ok) return;
        Ui.Try(this, () => File.WriteAllBytes(dlg.FileName, _c.Master.GetAttachment(a.Id).Content), _c.Log);
    }

    void Delete()
    {
        if (_grid.SelectedItem is not AttachmentInfo a) return;
        if (!Ui.Confirm(this, L.T($"Usunąć załącznik „{a.FileName}”?", $"Delete attachment \"{a.FileName}\"?"))) return;
        Ui.Try(this, () => { _c.Master.DeleteAttachment(a.Id); Load(_ownerId); }, _c.Log);
    }
}
