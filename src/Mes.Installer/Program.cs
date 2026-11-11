using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Data;
using Mes.Shared.UI;

namespace Mes.Installer;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Ui.LoadLanguage();
        new Application(Eto.Platform.Detect).Run(new InstallerForm());
    }
}

/// <summary>
/// Instalator systemu (R3.3): pyta o adres serwera PostgreSQL, login, haslo i 5-znakowy prefiks bazy,
/// tworzy schemat, role i konto MasterDATA oraz generuje zaszyfrowane pliki setup.ini.
/// </summary>
public class InstallerForm : Form
{
    TextBox _host = new() { Text = "" }, _port = new() { Text = "5432" }, _database = new() { Text = "" },
        _admin = new() { Text = "" }, _schema = new() { Text = "prod1", MaxLength = 5 },
        _prodUser = new(), _mgmtUser = new(), _prodPwd = new(), _mgmtPwd = new(), _outDir = new();
    PasswordBox _adminPwd = new();
    TextArea _log = new() { ReadOnly = true, Height = 150 };
    bool _usersEdited;

    public InstallerForm()
    {
        ClientSize = new Size(640, 640);
        _prodPwd.Text = PasswordHasher.RandomPassword();
        _mgmtPwd.Text = PasswordHasher.RandomPassword();
        _outDir.Text = Path.Combine(Ui.AppDir, "setup-output");
        L.Changed += () => Application.Instance.AsyncInvoke(Build);
        _schema.TextChanged += (_, _) => SyncUsers();
        _prodUser.KeyUp += (_, _) => _usersEdited = true;
        _mgmtUser.KeyUp += (_, _) => _usersEdited = true;
        SyncUsers();
        Build();
    }

    void SyncUsers()
    {
        if (_usersEdited) return;
        string s = _schema.Text.Trim().ToLowerInvariant();
        _prodUser.Text = s + "_prod"; _mgmtUser.Text = s + "_mgmt";
    }

    void Build()
    {
        Ui.Brand(this); Title = L.T("SpecInstall – Instalator systemu", "SpecInstall – System Installer");
        TextBox[] order = { _host, _port, _database, _admin };
        for (int i = 0; i < order.Length - 1; i++) { var next = order[i + 1]; Ui.OnEnter(order[i], () => next.Focus()); }
        Ui.OnEnter(_admin, () => _adminPwd.Focus());
        Ui.OnEnter(_adminPwd, () => _schema.Focus());

        var server = new GroupBox
        {
            Text = L.T("Serwer PostgreSQL", "PostgreSQL server"), Padding = 10,
            Content = Ui.Form(
                (L.T("Adres IP / host", "IP address / host"), _host), (L.T("Port", "Port"), _port), (L.T("Baza danych", "Database"), _database),
                (L.T("Login administratora", "Administrator login"), _admin), (L.T("Hasło administratora", "Administrator password"), _adminPwd))
        };
        var inst = new GroupBox
        {
            Text = L.T("Instalacja", "Installation"), Padding = 10,
            Content = Ui.Form(
                (L.T("Prefiks bazy (5 znaków, np. prod1)", "Database prefix (5 chars, e.g. prod1)"), _schema),
                (L.T("Użytkownik bazy – produkcja (minimalne uprawnienia)", "DB user – production (minimal privileges)"), _prodUser),
                (L.T("Hasło – produkcja", "Password – production"), _prodPwd),
                (L.T("Użytkownik bazy – zarządzanie", "DB user – management"), _mgmtUser),
                (L.T("Hasło – zarządzanie", "Password – management"), _mgmtPwd),
                (L.T("Folder na pliki setup.ini", "Folder for setup.ini files"), _outDir))
        };
        Content = new TableLayout
        {
            Padding = 14, Spacing = new Size(8, 10),
            Rows =
            {
                new TableRow(Ui.Row(Ui.Lbl(L.T("SpecInstall – instalator systemu", "SpecInstall – system installer"), 15, true), Ui.LanguageButton(), Ui.AboutButton(this, "SpecInstall"))),
                new TableRow(server), new TableRow(inst),
                new TableRow(Ui.Row(Ui.Btn(L.T("Test połączenia", "Test connection"), TestConnection, 160), Ui.Btn(L.T("Instaluj", "Install"), Install, 160))),
                new TableRow(_log) { ScaleHeight = true },
            }
        };
    }

    void Log(string s) { _log.Append(s + Environment.NewLine, true); }

    string AdminCs()
    {
        if (!int.TryParse(_port.Text.Trim(), out int port)) throw new MesException("Niepoprawny port.", "Invalid port.");
        if (_host.Text.Trim().Length == 0 || _database.Text.Trim().Length == 0 || _admin.Text.Trim().Length == 0)
            throw new MesException("Podaj adres serwera, nazwę bazy i login.", "Enter the server address, database name and login.");
        return SchemaInstaller.AdminConnString(_host.Text.Trim(), port, _database.Text.Trim(), _admin.Text.Trim(), _adminPwd.Text);
    }

    void TestConnection() => Ui.Try(this, () =>
    {
        string schema = _schema.Text.Trim().ToLowerInvariant();
        SchemaInstaller.ValidateSchemaName(schema);
        bool exists = SchemaInstaller.SchemaExists(AdminCs(), schema);
        Log(L.T("Połączenie OK. ", "Connection OK. ") + (exists
            ? L.T($"Schemat „{schema}” JUŻ ISTNIEJE – instalacja zostanie odrzucona.", $"Schema \"{schema}\" ALREADY EXISTS – installation will be refused.")
            : L.T($"Schemat „{schema}” jest wolny.", $"Schema \"{schema}\" is free.")));
    });

    void Install() => Ui.Try(this, () =>
    {
        string schema = _schema.Text.Trim().ToLowerInvariant();
        string cs = AdminCs();
        SchemaInstaller.Install(cs, schema, _prodUser.Text.Trim(), _prodPwd.Text, _mgmtUser.Text.Trim(), _mgmtPwd.Text, Log);

        var cfg = new SetupConfig { Host = _host.Text.Trim(), Port = int.Parse(_port.Text.Trim()), Database = _database.Text.Trim(), Schema = schema };
        string dir = _outDir.Text.Trim();
        string prodDir = Path.Combine(dir, "SpecAPP"), mgmtDir = Path.Combine(dir, "SpecManagment");
        Directory.CreateDirectory(prodDir); Directory.CreateDirectory(mgmtDir);
        cfg.User = _prodUser.Text.Trim(); cfg.Password = _prodPwd.Text;
        SetupIni.Write(Path.Combine(prodDir, "setup.ini"), cfg);
        cfg.User = _mgmtUser.Text.Trim(); cfg.Password = _mgmtPwd.Text;
        SetupIni.Write(Path.Combine(mgmtDir, "setup.ini"), cfg);
        Log(L.T("Zapisano: ", "Written: ") + Path.Combine(prodDir, "setup.ini"));
        Log(L.T("Zapisano: ", "Written: ") + Path.Combine(mgmtDir, "setup.ini"));

        Ui.Info(this, L.T(
            $"Instalacja zakończona.\n\nSchemat: {schema}\n\nUżytkownik aplikacji Zarządzanie:\n   login: {SchemaInstaller.MasterLogin}\n   hasło: {SchemaInstaller.MasterPassword}\n\nUWAGA: zmień to hasło przy pierwszym logowaniu (system to wymusi) i utwórz własnych użytkowników.\n\nPliki setup.ini (zaszyfrowane dane logowania do bazy) skopiuj oddzielną drogą:\n • SpecAPP\\setup.ini → do folderu SpecAPP na stanowiskach produkcyjnych\n • SpecManagment\\setup.ini → do folderu aplikacji SpecManagment",
            $"Installation finished.\n\nSchema: {schema}\n\nManagement application user:\n   login: {SchemaInstaller.MasterLogin}\n   password: {SchemaInstaller.MasterPassword}\n\nNOTE: change this password at first login (the system enforces it) and create your own users.\n\nCopy the setup.ini files (encrypted database credentials) via a separate channel:\n • SpecAPP\\setup.ini → to the SpecAPP folder on production stations\n • SpecManagment\\setup.ini → to the SpecManagment application folder"));
    });
}
