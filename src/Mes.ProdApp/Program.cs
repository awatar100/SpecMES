using Eto.Forms;
using Mes.Core;
using Mes.Data;
using Mes.Shared.UI;

namespace Mes.ProdApp;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var log = new FileLogger(Path.Combine(Ui.AppDir, "logs"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error("Unhandled exception", e.ExceptionObject as Exception);
        try
        {
            Ui.LoadLanguage();
            var app = new Application(Eto.Platform.Detect);
            app.UnhandledException += (_, e) => log.Error("Unhandled UI exception", e.ExceptionObject as Exception);
            var cfg = Ui.LoadSetup();
            if (cfg == null) { log.Error("setup.ini missing or invalid"); return; }
            log.Info($"SpecAPP start, station {Environment.MachineName}, server {cfg.Host}:{cfg.Port}/{cfg.Database} schema {cfg.Schema}");
            // maly limit puli: przy 1000 stanowisk polaczenia sa otwierane tylko na czas operacji
            app.Run(new MainForm(new Db(cfg, 2, "SpecAPP"), log));
        }
        catch (Exception ex) { log.Error("Fatal", ex); throw; }
    }
}
