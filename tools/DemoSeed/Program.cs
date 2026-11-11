// Narzedzie deweloperskie: instaluje schemat demonstracyjny, wpisuje przykladowe dane
// i generuje pliki setup.ini dla ProdAPP i Zarzadzania (to samo robi recznie Instalator).
// Uzycie: DemoSeed <host> <port> <baza> <admin> <haslo> <schemat> <katalog repo> [--drop]
using Mes.Core;
using Mes.Data;

if (args.Length < 7) { Console.WriteLine("DemoSeed <host> <port> <db> <adminUser> <adminPassword> <schema> <repoRoot> [--drop]"); return 2; }
string host = args[0]; int port = int.Parse(args[1]); string database = args[2], schema = args[5], root = args[6];
string adminCs = SchemaInstaller.AdminConnString(host, port, database, args[3], args[4]);
string prodUser = schema + "_prod", mgmtUser = schema + "_mgmt";

try
{
    if (args.Contains("--drop")) { SchemaInstaller.Drop(adminCs, schema, prodUser, mgmtUser); Console.WriteLine($"Dropped schema {schema}."); }
    string prodPwd = PasswordHasher.RandomPassword(), mgmtPwd = PasswordHasher.RandomPassword();
    SchemaInstaller.Install(adminCs, schema, prodUser, prodPwd, mgmtUser, mgmtPwd, Console.WriteLine);

    var baseCfg = new SetupConfig { Host = host, Port = port, Database = database, Schema = schema };
    var mgmtCfg = new SetupConfig { Host = host, Port = port, Database = database, Schema = schema, User = mgmtUser, Password = mgmtPwd };
    var prodCfg = new SetupConfig { Host = host, Port = port, Database = database, Schema = schema, User = prodUser, Password = prodPwd };
    foreach (var (dir, cfg) in new[] { (Path.Combine(root, "src", "Mes.ProdApp", "bin", "Wpf", "Debug"), prodCfg), (Path.Combine(root, "src", "Mes.Management", "bin", "Wpf", "Debug"), mgmtCfg) })
    {
        Directory.CreateDirectory(dir);
        SetupIni.Write(Path.Combine(dir, "setup.ini"), cfg);
        Console.WriteLine("setup.ini -> " + dir);
    }

    var mgmt = new Db(mgmtCfg, 4); var prod = new Db(prodCfg, 2);
    var auth = new AuthRepo(mgmt); var master = new MasterRepo(mgmt); var routing = new RoutingRepo(mgmt); var orders = new OrderRepo(mgmt); var quality = new QualityRepo(mgmt);
    long admin = auth.Login(SchemaInstaller.MasterLogin, SchemaInstaller.MasterPassword).UserId!.Value;

    // konta demonstracyjne – hasla startowe zapisane w pliku lokalnym (poza repozytorium), zmiana wymuszana przy 1. logowaniu
    var accounts = new[] { ("oper1", "Operator Demo", Roles.Production), ("planista", "Planista Demo", Roles.Planner), ("technolog", "Technolog Demo", Roles.Technologist), ("jakosc", "Jakosc Demo", Roles.Quality) };
    var lines = new List<string> { $"MES demo accounts – schema {schema} ({host}/{database})", "login;role;initial password (change is enforced at first login)",
        $"{SchemaInstaller.MasterLogin};MASTERDATA;{SchemaInstaller.MasterPassword}" };
    foreach (var (login, name, role) in accounts)
    {
        string pwd = PasswordHasher.RandomPassword(8);
        master.AddUser(new AppUser { Login = login, FullName = name, Role = role }, pwd, admin);
        lines.Add($"{login};{role};{pwd}");
    }
    string accFile = Path.Combine(root, "demo-accounts.txt");
    File.WriteAllLines(accFile, lines);
    Console.WriteLine("Demo accounts -> " + accFile);

    var glue = new Material { Name = "Klej epoksydowy", Unit = "g", DefaultQty = 2.5m, BatchPattern = "KLEJ*" }; master.SaveMaterial(glue);
    var screw = new Material { Name = "Sruba M3x8", Unit = "szt", DefaultQty = 5 }; master.SaveMaterial(screw);
    var meter = new Tool { ToolSn = "MIERNIK-001", Name = "Multimetr Fluke 87V", ValidFrom = DateTime.Today.AddMonths(-6), ValidTo = DateTime.Today.AddMonths(6) }; master.SaveTool(meter);
    var tester = new Tool { ToolSn = "TESTER-7", Name = "Tester koncowy FW" }; master.SaveTool(tester);
    master.SaveTool(new Tool { ToolSn = "MIERNIK-OLD", Name = "Multimetr po terminie", ValidFrom = DateTime.Today.AddYears(-2), ValidTo = DateTime.Today.AddDays(-30) });

    string pomiar = Path.Combine(root, "tools", "PomiarSim", "bin", "Debug", "net8.0", OperatingSystem.IsWindows() ? "pomiar.exe" : "pomiar");
    long pid = master.AddProduct(new Product { Prefix = "TELEFON/", ErpValue = "NOKIA10/", Suffix = " FW1.00", NumLength = 4, Notes = "Wyrob demonstracyjny (przyklad a z wymagan)" }, admin);
    master.AddProduct(new Product { Prefix = "TELEFON-", NumLength = 4, Notes = "Minimalny numer (przyklad b z wymagan)" }, admin);
    routing.SaveStep(pid, new Step { StepNo = 10, Title = "Inspekcja wizualna", Body = "Sprawdz obudowe: brak rys, pekniec i zabrudzen.", DurationS = 20 }, true, admin);
    routing.SaveStep(pid, new Step
    {
        StepNo = 20, Title = "Montaz PCB, sruby i klej", Body = "Wloz plytke PCB (zeskanuj jej numer), wkrec 5 srub, naloz klej na krawedz.", DurationS = 90, Consumes = true,
        Materials = { new StepMaterial { SlotNo = 1, MaterialId = screw.Id, Qty = 5 }, new StepMaterial { SlotNo = 2, MaterialId = glue.Id, Qty = 2.5m } }
    }, true, admin);
    routing.SaveStep(pid, new Step { StepNo = 30, Title = "Pomiar napiecia TP1", Body = "Zmierz napiecie w punkcie testowym TP1.", ManualMeas = true, Lsl = 3.0m, Usl = 3.6m, Unit = "V", ToolId = meter.Id, IsRepeatable = true, DurationS = 30 }, true, admin);
    routing.SaveStep(pid, new Step
    {
        StepNo = 40, Title = "Test automatyczny + firmware", Body = "Podlacz wyrob do testera i nacisnij F1.", AutoMeas = true, ExePath = pomiar, TimeoutS = 3, ToolId = tester.Id, IsRepeatable = true, DurationS = 10,
        Slots = { new StepSlot { SlotNo = 1, Kind = "NUM", Lsl = 4.9m, Usl = 5.1m, Unit = "V" }, new StepSlot { SlotNo = 2, Kind = "TEXT", ExpectedText = "OK" } }
    }, true, admin);
    routing.SaveStep(pid, new Step { StepNo = 50, Title = "Kontrola koncowa i pakowanie", Body = "Sprawdz kompletnosc, zapakuj.", IsFinal = true, DurationS = 30 }, true, admin);

    var order = orders.Create(pid, "BOM-A1", 8, DateTime.Today, DateTime.Today.AddDays(5), "Zlecenie demonstracyjne", admin);
    orders.Start(order.Id, admin);
    Console.WriteLine($"Order {order.OrderNo}: {order.FirstSerial} … {order.LastSerial}");

    // kilka sztuk przeprowadzonych przez proces, zeby dashboard KPI mial dane
    var production = new ProductionRepo(prod);
    long station = production.RegisterStation("DEMO-STATION");
    long oper = master.Users().Single(u => u.Login == "oper1").Id;
    var rnd = new Random(7);
    for (int n = 1; n <= 5; n++)
    {
        string sn = SerialNumber.Build("TELEFON/", "NOKIA10/", " FW1.00", 4, n);
        int stopAfter = n switch { 4 => 30, 5 => 20, _ => 99 };   // 4: blokada jakosci pozniej, 5: w toku
        while (true)
        {
            UnitContext ctx;
            try { ctx = production.OpenUnit(sn, station); } catch (MesException) { break; }
            var s = ctx.Step;
            if (s.StepNo > stopAfter) { production.ReleaseUnit(ctx.Unit.Id, station); break; }
            var rec = new StepResultRecord { UnitId = ctx.Unit.Id, SerialNo = sn, UnitVersion = ctx.Unit.Version, StepNo = s.StepNo, UserId = oper, DurationS = s.DurationS * (0.7 + rnd.NextDouble() * 0.8) };
            foreach (var m in s.Materials) rec.Materials.Add(new MaterialUse { SlotNo = m.SlotNo, MaterialId = m.MaterialId, Qty = m.Qty + (n == 2 && m.SlotNo == 1 ? 1 : 0), BatchNo = m.BatchPattern.Length > 0 ? "KLEJ-2026-11" : "SR-4471" });
            if (s.Consumes) rec.ConsumedPart = $"PCB-{100200 + n}";
            if (n == 4 && s.StepNo == 20) { rec.Result = "NOK"; rec.Comment = "zerwany gwint w obudowie"; }   // krok niepowtarzalny -> blokada
            if (s.ManualMeas)
            {
                bool retry = n == 3 && !quality.History(ctx.Unit.Id).Any(h => h.StepNo == s.StepNo);
                decimal v = retry ? 3.8m : 3.2m + n * 0.05m;
                rec.ToolSn = s.ToolSn; rec.Values.Add(new ResultValue { SlotNo = 1, RawValue = Ui(v), NumValue = v, InLimits = Evaluator.InLimits(v, s.Lsl, s.Usl) });
                if (retry) { rec.Result = "NOK"; rec.ErrorCode = ErrorCodes.OutOfLimits; rec.Comment = "za wysokie napiecie, ponowny pomiar"; }
            }
            if (s.AutoMeas)
            {
                rec.ToolSn = s.ToolSn;
                rec.Values = Evaluator.EvaluateSlots(s.Slots, new[] { Ui(5.0m + n * 0.01m), "OK" }, out bool ok);
                rec.Result = ok ? "OK" : "NOK";
            }
            production.SaveResult(rec, station);
            Thread.Sleep(150);
        }
    }
    Console.WriteLine("Demo data ready.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("ERROR: " + ex.Message);
    return 1;
}

static string Ui(decimal v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
