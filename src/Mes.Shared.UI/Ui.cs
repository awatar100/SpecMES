using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Data;

namespace Mes.Shared.UI;

/// <summary>Wspolne pomocniki Eto.Forms dla wszystkich aplikacji.</summary>
public static class Ui
{
    public static readonly Color Green = Color.FromArgb(46, 160, 67), Red = Color.FromArgb(207, 34, 46),
        Amber = Color.FromArgb(191, 135, 0), Blue = Color.FromArgb(9, 105, 218), Gray = Color.FromArgb(110, 119, 129);

    public static string AppDir => AppContext.BaseDirectory;

    public static Label Lbl(string text, float size = 0, bool bold = false, Color? color = null)
    {
        var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (size > 0 || bold) l.Font = bold ? Fonts.Sans(size > 0 ? size : 10, FontStyle.Bold) : Fonts.Sans(size);
        if (color != null) l.TextColor = color.Value;
        return l;
    }

    public static GridColumn Col<T>(string header, Func<T, string?> get, int width = -1)
    {
        var col = new GridColumn
        {
            HeaderText = header, Sortable = false, Resizable = true,
            DataCell = new TextBoxCell { Binding = Binding.Delegate<T, string>(r => get(r) ?? "") }
        };
        if (width > 0) col.Width = width; else col.AutoSize = true;
        return col;
    }

    public static GridView Grid<T>(params GridColumn[] cols) where T : class
    {
        var g = new GridView { ShowHeader = true, AllowMultipleSelection = false, GridLines = GridLines.Horizontal };
        foreach (var c in cols) g.Columns.Add(c);
        return g;
    }

    /// <summary>Formularz etykieta–pole; ostatnia kolumna sie rozciaga.</summary>
    public static TableLayout Form(params (string Label, Control? Field)[] rows)
    {
        var t = new TableLayout { Spacing = new Size(8, 6) };
        foreach (var (label, field) in rows)
            t.Rows.Add(new TableRow(new TableCell(Lbl(label)), new TableCell(field, true)));
        return t;
    }

    public static StackLayout Row(params Control?[] items)
    {
        var s = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var i in items) if (i != null) s.Items.Add(i);
        return s;
    }

    public static Button Btn(string text, Action click, int width = -1)
    {
        var b = new Button { Text = text };
        if (width > 0) b.Width = width;
        b.Click += (_, _) => click();
        return b;
    }

    // ---- ikona aplikacji (assets/app.ico) i okno About
    public const string SiteUrl = "https://spec-electronic.pl";
    static Icon? _icon;
    public static Icon? AppIcon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                using var s = typeof(Ui).Assembly.GetManifestResourceStream("app.ico");
                if (s == null) return null;
                var ms = new MemoryStream(); s.CopyTo(ms); ms.Position = 0;
                _icon = new Icon(ms);
            }
            catch { }
            return _icon;
        }
    }

    /// <summary>Ustawia ikone okna (zamiast domyslnej systemowej).</summary>
    public static void Brand(Window w)
    {
        try { if (AppIcon is { } i) w.Icon = i; } catch { }
    }

    public static Button AboutButton(Control parent, string appName) => Btn("About", () => About(parent, appName));

    public static void About(Control? parent, string appName)
    {
        var dlg = new Dialog { Title = "About – " + appName, Resizable = false };
        Brand(dlg);
        var link = new LinkButton { Text = "spec-electronic.pl" };
        link.Click += (_, _) => { try { Application.Instance.Open(SiteUrl); } catch (Exception ex) { Error(dlg, ex.Message); } };
        var close = Btn("OK", () => dlg.Close(), 100);
        var text = new TableLayout
        {
            Spacing = new Size(6, 8),
            Rows = { new TableRow(Lbl(appName, 16, true)), new TableRow(Row(link, Lbl("by P.R. 2026"))), new TableRow(Row(close)) }
        };
        Control? logo = null;
        try { if (AppIcon is { } i) logo = new ImageView { Image = i.WithSize(48, 48), Size = new Size(48, 48) }; } catch { }
        dlg.Content = new TableLayout { Padding = 20, Spacing = new Size(16, 8), Rows = { new TableRow(new TableCell(logo), new TableCell(text, true)) } };
        dlg.DefaultButton = close; dlg.AbortButton = close;
        dlg.ShowModal(parent);
    }

    public static void Info(Control? parent, string text) =>
        MessageBox.Show(parent, text, "MES", MessageBoxButtons.OK, MessageBoxType.Information);

    public static void Error(Control? parent, string text) =>
        MessageBox.Show(parent, text, L.T("MES – błąd", "MES – error"), MessageBoxButtons.OK, MessageBoxType.Error);

    public static bool Confirm(Control? parent, string text) =>
        MessageBox.Show(parent, text, L.T("MES – potwierdzenie", "MES – confirmation"), MessageBoxButtons.YesNo, MessageBoxType.Warning, MessageBoxDefaultButton.No) == DialogResult.Yes;

    public static string Describe(Exception ex) => ex switch
    {
        MesException m => m.Message,
        _ when Db.IsConnectionError(ex) => L.T("Brak połączenia z bazą danych.\n", "No connection to the database.\n") + ex.Message,
        _ => ex.Message
    };

    /// <summary>Wykonuje akcje, a blad pokazuje jako monit. Zwraca true przy powodzeniu.</summary>
    public static bool Try(Control? parent, Action action, FileLogger? log = null)
    {
        try { action(); return true; }
        catch (Exception ex)
        {
            if (ex is not MesException) log?.Error("UI action failed", ex);
            Error(parent, Describe(ex));
            return false;
        }
    }

    /// <summary>CR z klawiatury lub czytnika kodow przenosi fokus dalej (wymaganie ergonomii).</summary>
    public static void OnEnter(Control c, Action next)
    {
        c.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Enter) { e.Handled = true; next(); }
        };
    }

    public static decimal? Dec(string? s) =>
        decimal.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string Dec(decimal? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";

    public static string Local(DateTime? utc, string fmt = "yyyy-MM-dd HH:mm:ss") => utc?.ToLocalTime().ToString(fmt) ?? "";

    // ---- wybor jezyka zapamietany lokalnie per stanowisko (Z37)
    static string LangFile => Path.Combine(AppDir, "language.txt");
    public static void LoadLanguage()
    {
        try { if (File.Exists(LangFile)) L.Set(File.ReadAllText(LangFile).Trim().Equals("en", StringComparison.OrdinalIgnoreCase)); }
        catch { }
    }
    public static void ToggleLanguage()
    {
        L.Set(!L.English);
        try { File.WriteAllText(LangFile, L.English ? "en" : "pl"); } catch { }
    }
    public static Button LanguageButton() => Btn(L.English ? "Polski" : "English", ToggleLanguage);

    /// <summary>Wczytanie setup.ini z folderu aplikacji; brak pliku = czytelny monit.</summary>
    public static SetupConfig? LoadSetup(string fileName = "setup.ini")
    {
        string path = Path.Combine(AppDir, fileName);
        if (!File.Exists(path))
        {
            Error(null, L.T($"Brak pliku {fileName} w folderze aplikacji:\n{AppDir}\n\nPlik generuje Instalator systemu.",
                $"File {fileName} is missing in the application folder:\n{AppDir}\n\nThe file is generated by the system Installer."));
            return null;
        }
        try { return SetupIni.Read(path); }
        catch (Exception ex) { Error(null, Describe(ex)); return null; }
    }
}

/// <summary>Okno logowania (UserID + haslo), wspolne dla ProdAPP i Zarzadzania. Obsluga wylacznie klawiatura: CR przechodzi dalej.</summary>
public class LoginDialog : Dialog<LoginResult?>
{
    readonly AuthRepo _auth;
    readonly Func<string, bool> _roleAllowed;
    readonly string _appName;
    TextBox _login = new();
    PasswordBox _pwd = new();
    Label _msg = new();

    public LoginDialog(AuthRepo auth, string appName, Func<string, bool> roleAllowed)
    {
        _auth = auth; _appName = appName; _roleAllowed = roleAllowed;
        Resizable = false;
        Ui.Brand(this);
        L.Changed += OnLang;
        Closed += (_, _) => L.Changed -= OnLang;
        Build();
    }

    void OnLang() => Application.Instance.AsyncInvoke(Build);

    void Build()
    {
        string keepLogin = _login.Text;
        Title = _appName + " – " + L.T("logowanie", "login");
        _login = new TextBox { Width = 260, Text = keepLogin };
        _pwd = new PasswordBox { Width = 260 };
        _msg = Ui.Lbl("", color: Ui.Red);
        _msg.Wrap = WrapMode.Word; _msg.Width = 360;
        var ok = Ui.Btn(L.T("Zaloguj", "Log in"), DoLogin, 120);
        Ui.OnEnter(_login, () => _pwd.Focus());
        Ui.OnEnter(_pwd, DoLogin);
        Content = new TableLayout
        {
            Padding = 20, Spacing = new Size(8, 10),
            Rows =
            {
                new TableRow(Ui.Lbl(_appName, 16, true)),
                new TableRow(Ui.Form((L.T("Użytkownik (UserID)", "User (UserID)"), _login), (L.T("Hasło", "Password"), _pwd))),
                new TableRow(_msg),
                new TableRow(Ui.Row(ok, Ui.Btn(L.T("Zamknij", "Close"), () => Close(null), 100), Ui.LanguageButton(), Ui.AboutButton(this, _appName))),
            }
        };
        DefaultButton = null;
        Application.Instance.AsyncInvoke(() => _login.Focus());
    }

    void DoLogin()
    {
        try
        {
            var r = _auth.Login(_login.Text.Trim(), _pwd.Text);
            switch (r.Status)
            {
                case "OK":
                    if (!_roleAllowed(r.Role ?? ""))
                    { Fail(L.T("Ta rola nie ma dostępu do tej aplikacji.", "This role has no access to this application.")); return; }
                    if (r.MustChange && !ChangePassword(r.Login, _pwd.Text)) { Fail(L.T("Zmiana hasła jest wymagana.", "Password change is required.")); return; }
                    Close(r);
                    return;
                case "LOCKED": Fail(L.T("Konto zablokowane na 5 minut po 5 nieudanych próbach.", "Account locked for 5 minutes after 5 failed attempts.")); return;
                case "INACTIVE": Fail(L.T("Konto jest nieaktywne.", "The account is inactive.")); return;
                default: Fail(L.T("Błędny użytkownik lub hasło.", "Invalid user or password.")); return;
            }
        }
        catch (Exception ex) { Fail(Ui.Describe(ex)); }
    }

    void Fail(string text)
    {
        _msg.Text = text;
        _pwd.Text = "";
        _pwd.Focus();
    }

    bool ChangePassword(string login, string oldPwd)
    {
        var dlg = new Dialog<bool> { Title = L.T("Wymagana zmiana hasła", "Password change required"), Resizable = false };
        Ui.Brand(dlg);
        var p1 = new PasswordBox { Width = 240 }; var p2 = new PasswordBox { Width = 240 };
        var msg = Ui.Lbl("", color: Ui.Red);
        void Save()
        {
            if (p1.Text != p2.Text) { msg.Text = L.T("Hasła nie są identyczne.", "Passwords do not match."); return; }
            if (p1.Text == oldPwd) { msg.Text = L.T("Nowe hasło musi różnić się od starego.", "The new password must differ from the old one."); return; }
            try { _auth.ChangePassword(login, oldPwd, p1.Text); dlg.Close(true); }
            catch (Exception ex) { msg.Text = Ui.Describe(ex); }
        }
        Ui.OnEnter(p1, () => p2.Focus());
        Ui.OnEnter(p2, Save);
        dlg.Content = new TableLayout
        {
            Padding = 20, Spacing = new Size(8, 10),
            Rows =
            {
                new TableRow(Ui.Lbl(L.T("Pierwsze logowanie lub reset hasła – ustaw nowe hasło.", "First login or password reset – set a new password."))),
                new TableRow(Ui.Form((L.T("Nowe hasło", "New password"), p1), (L.T("Powtórz hasło", "Repeat password"), p2))),
                new TableRow(msg),
                new TableRow(Ui.Row(Ui.Btn(L.T("Zapisz", "Save"), Save, 120), Ui.Btn(L.T("Anuluj", "Cancel"), () => dlg.Close(false), 100))),
            }
        };
        Application.Instance.AsyncInvoke(() => p1.Focus());
        return dlg.ShowModal(this);
    }
}
