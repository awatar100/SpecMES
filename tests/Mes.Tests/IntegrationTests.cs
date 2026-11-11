using Mes.Core;
using Mes.Data;
using Xunit;

namespace Mes.Tests;

/// <summary>
/// Testy integracyjne na osobnym, losowym schemacie testowym (nigdy produkcyjnym), usuwanym po tescie.
/// Wymagaja zmiennych: MES_TEST_HOST, MES_TEST_DB, MES_TEST_USER, MES_TEST_PASSWORD (opcjonalnie MES_TEST_PORT).
/// Bez nich test konczy sie bez wykonania.
/// </summary>
public class IntegrationTests
{
    static string? Env(string n) => Environment.GetEnvironmentVariable(n);

    [Fact]
    public void Full_flow_on_test_schema()
    {
        if (Env("MES_TEST_HOST") is not { Length: > 0 } host) return;
        int port = int.Parse(Env("MES_TEST_PORT") ?? "5432");
        string database = Env("MES_TEST_DB")!, admin = SchemaInstaller.AdminConnString(host, port, database, Env("MES_TEST_USER")!, Env("MES_TEST_PASSWORD")!);
        string schema = "t" + Guid.NewGuid().ToString("N")[..4];
        string prodUser = schema + "_prod", mgmtUser = schema + "_mgmt", prodPwd = PasswordHasher.RandomPassword(), mgmtPwd = PasswordHasher.RandomPassword();

        SchemaInstaller.Install(admin, schema, prodUser, prodPwd, mgmtUser, mgmtPwd);
        try
        {
            // Odbior etapu 1: ponowne uruchomienie na tym samym schemacie odmawia nadpisania
            Assert.Throws<MesException>(() => SchemaInstaller.Install(admin, schema, prodUser, prodPwd, mgmtUser, mgmtPwd));

            var mgmt = new Db(new SetupConfig { Host = host, Port = port, Database = database, Schema = schema, User = mgmtUser, Password = mgmtPwd }, 4);
            var prod = new Db(new SetupConfig { Host = host, Port = port, Database = database, Schema = schema, User = prodUser, Password = prodPwd }, 2);
            var auth = new AuthRepo(mgmt); var master = new MasterRepo(mgmt); var routing = new RoutingRepo(mgmt);
            var orders = new OrderRepo(mgmt); var quality = new QualityRepo(mgmt); var reports = new ReportRepo(mgmt);
            var production = new ProductionRepo(prod); var prodAuth = new AuthRepo(prod);

            // --- logowanie MasterDATA / DATA, wymuszona zmiana hasla, blokada po 5 probach
            var login = auth.Login("MasterDATA", "DATA");
            Assert.Equal("OK", login.Status); Assert.True(login.MustChange); Assert.Equal(Roles.MasterData, login.Role);
            long adminId = login.UserId!.Value;
            auth.ChangePassword("MasterDATA", "DATA", "Nowe#Haslo1");
            Assert.Equal("INVALID", auth.Login("MasterDATA", "DATA").Status);
            Assert.False(auth.Login("MasterDATA", "Nowe#Haslo1").MustChange);

            master.AddUser(new AppUser { Login = "oper1", FullName = "Operator 1", Role = Roles.Production }, "oper1pwd", adminId);
            Assert.Throws<MesException>(() => master.AddUser(new AppUser { Login = "oper1", Role = Roles.Production }, "x12345", adminId));
            for (int i = 0; i < 5; i++) Assert.Equal("INVALID", prodAuth.Login("oper1", "zle").Status);
            Assert.Equal("LOCKED", prodAuth.Login("oper1", "oper1pwd").Status);
            var oper = master.Users().Single(u => u.Login == "oper1");
            master.UpdateUser(oper, null, adminId);   // zdjecie blokady
            var operLogin = prodAuth.Login("oper1", "oper1pwd");
            Assert.Equal("OK", operLogin.Status);
            long operId = operLogin.UserId!.Value;

            // --- rola produkcyjna: minimalne uprawnienia
            using (var c = prod.Open())
            {
                AssertDenied(() => Exec(c, "select password_hash from app_user"));
                AssertDenied(() => Exec(c, "delete from step_result"));
                AssertDenied(() => Exec(c, "update step_result set result='OK'"));
                AssertDenied(() => Exec(c, "update unit set serial_no='x'"));
                AssertDenied(() => Exec(c, "delete from unit"));
            }
            using (var c = mgmt.Open())
            {
                AssertDenied(() => Exec(c, "update product set prefix='x'"));
                AssertDenied(() => Exec(c, "delete from product"));
                AssertDenied(() => Exec(c, "delete from step_result"));
                AssertDenied(() => Exec(c, "delete from audit_log"));
            }

            // --- wyroby: przyklady a) i b), zakaz nadpisania
            long pA = master.AddProduct(new Product { Prefix = "TELEFON/", ErpValue = "NOKIA10/", Suffix = " FW1.00", NumLength = 4, Notes = "a" }, adminId);
            long pB = master.AddProduct(new Product { Prefix = "TELEFON-", NumLength = 4 }, adminId);
            long pSmall = master.AddProduct(new Product { Prefix = "MINI-", NumLength = 3 }, adminId);
            Assert.Throws<MesException>(() => master.AddProduct(new Product { Prefix = "TELEFON-", NumLength = 4 }, adminId));
            master.AddProduct(new Product { Prefix = "TELEFON-", NumLength = 5 }, adminId);   // inna dlugosc = nowy wyrob

            // --- slowniki
            var glue = new Material { Name = "Klej", Unit = "g", DefaultQty = 2.5m, BatchPattern = "KLEJ*" }; master.SaveMaterial(glue);
            var screw = new Material { Name = "Sruba M3", Unit = "szt", DefaultQty = 5 }; master.SaveMaterial(screw);
            var meter = new Tool { ToolSn = "MIERNIK-001", Name = "Multimetr", ValidFrom = DateTime.Today.AddDays(-10), ValidTo = DateTime.Today.AddDays(10) }; master.SaveTool(meter);
            var tester = new Tool { ToolSn = "TESTER-7", Name = "Tester" }; master.SaveTool(tester);
            master.AddAttachment("TOOL", meter.Id, "swiadectwo.pdf", new byte[] { 1, 2, 3 }, adminId);
            Assert.Single(master.Attachments("TOOL", meter.Id));
            Assert.Throws<MesException>(() => master.AddAttachment("TOOL", meter.Id, "big.pdf", new byte[MasterRepo.MaxAttachment + 1], adminId));

            // --- marszruta: 10 praca reczna + materialy + consumes, 20 pomiar reczny (powtarzalny), 30 automat, 40 final
            Assert.Throws<MesException>(() => orders.Create(pA, "BOM-1", 3, null, null, "", adminId));   // brak marszruty
            routing.SaveStep(pA, new Step
            {
                StepNo = 10, Title = "Montaz", Body = "Wkrec sruby, naloz klej", Consumes = true, DurationS = 60,
                Materials = { new StepMaterial { SlotNo = 1, MaterialId = screw.Id, Qty = 5 }, new StepMaterial { SlotNo = 2, MaterialId = glue.Id, Qty = 2.5m } }
            }, true, adminId);
            routing.SaveStep(pA, new Step { StepNo = 20, Title = "Pomiar reczny", ManualMeas = true, Lsl = 3.0m, Usl = 3.6m, Unit = "V", ToolId = meter.Id, IsRepeatable = true }, true, adminId);
            routing.SaveStep(pA, new Step
            {
                StepNo = 30, Title = "Test automatyczny", AutoMeas = true, ExePath = "C:\\temp\\pomiar.exe", TimeoutS = 2, ToolId = tester.Id,
                Slots = { new StepSlot { SlotNo = 1, Kind = "NUM", Lsl = 4.9m, Usl = 5.1m, Unit = "V" }, new StepSlot { SlotNo = 2, Kind = "TEXT", ExpectedText = "OK" } }
            }, true, adminId);
            routing.SaveStep(pA, new Step { StepNo = 40, Title = "Kontrola koncowa", IsFinal = true }, true, adminId);
            Assert.Throws<MesException>(() => routing.SaveStep(pA, new Step { StepNo = 40, Title = "dup" }, true, adminId));
            Assert.Throws<MesException>(() => routing.SaveStep(pA, new Step { StepNo = 45, Title = "brak" }, false, adminId));
            Assert.Equal(50, StepValidator.SuggestNext(routing.RoutingSteps(pA).Select(s => s.StepNo)));
            Assert.Equal(2, routing.RoutingSteps(pA).Single(s => s.StepNo == 10).Materials.Count);

            // --- zlecenie: generowanie numerow
            var o1 = orders.Create(pA, "BOM-1", 3, DateTime.Today, DateTime.Today.AddDays(2), "pierwsze", adminId);
            Assert.Matches(@"^ZP-\d{8}-001$", o1.OrderNo);
            Assert.Equal("TELEFON/NOKIA10/0001 FW1.00", o1.FirstSerial);
            Assert.Equal("TELEFON/NOKIA10/0003 FW1.00", o1.LastSerial);
            const string sn1 = "TELEFON/NOKIA10/0001 FW1.00", sn2 = "TELEFON/NOKIA10/0002 FW1.00", sn3 = "TELEFON/NOKIA10/0003 FW1.00";

            long station = production.RegisterStation("TEST-STATION-1"), station2 = production.RegisterStation("TEST-STATION-2");
            Assert.Equal(station, production.RegisterStation("TEST-STATION-1"));
            Assert.Equal("ORDER_NOT_STARTED", Assert.Throws<MesException>(() => production.OpenUnit(sn1, station)).Code);
            Assert.Equal("UNKNOWN", Assert.Throws<MesException>(() => production.OpenUnit("NIEMA", station)).Code);

            orders.Start(o1.Id, adminId);
            Assert.Throws<MesException>(() => orders.Start(o1.Id, adminId));

            // zmiana marszruty po starcie nie zmienia migawki zlecenia (odbior etapu 3)
            routing.SaveStep(pA, new Step { StepNo = 10, Title = "Montaz v2", Body = "zmieniony" }, false, adminId);
            routing.SaveStep(pA, new Step { StepNo = 15, Title = "Wstawiony" }, true, adminId);
            var snap = routing.OrderSteps(o1.Id);
            Assert.Equal(new[] { 10, 20, 30, 40 }, snap.Select(s => s.StepNo));
            Assert.Equal("Montaz", snap[0].Title); Assert.Equal(2, snap[0].Materials.Count); Assert.Equal(2, snap[2].Slots.Count);

            // --- praca na stanowisku: krok 10
            var ctx = production.OpenUnit(sn1, station);
            Assert.Equal(10, ctx.Step.StepNo); Assert.Equal(1, ctx.StepIndex); Assert.Equal(4, ctx.StepCount);
            Assert.Equal("KLEJ*", ctx.Step.Materials[1].BatchPattern);
            Assert.Equal("LOCKED", Assert.Throws<MesException>(() => production.OpenUnit(sn1, station2)).Code);   // Z25
            var rec = Rec(ctx, operId, "OK");
            rec.Materials.Add(new MaterialUse { SlotNo = 1, MaterialId = screw.Id, Qty = 10, BatchNo = "SR-77" });   // zuzycie wieksze niz 5
            rec.Materials.Add(new MaterialUse { SlotNo = 2, MaterialId = glue.Id, Qty = 2.5m, BatchNo = "KLEJ-1" });
            rec.ConsumedPart = "PCB-000123";
            var outc = production.SaveResult(rec, station);
            Assert.Equal(UnitStatus.InProgress, outc.UnitStatus); Assert.Equal(20, outc.NextStepNo);
            Assert.True(production.SaveResult(rec, station).AlreadySaved);   // idempotencja po GUID (bufor)

            // konflikt wersji: stary kontekst
            var stale = Rec(ctx, operId, "OK");
            Assert.Equal("CONFLICT", Assert.Throws<MesException>(() => production.SaveResult(stale, station)).Code);

            // --- krok 20: pomiar reczny powtarzalny – NOK nie blokuje
            Assert.Equal((true, true), production.CheckTool("MIERNIK-001"));
            Assert.Equal((false, false), production.CheckTool("NIEMA"));
            ctx = production.OpenUnit(sn1, station2);
            Assert.Equal(20, ctx.Step.StepNo); Assert.Equal("MIERNIK-001", ctx.Step.ToolSn);
            rec = Rec(ctx, operId, "NOK"); rec.ToolSn = "MIERNIK-001"; rec.Comment = "za wysokie"; rec.ErrorCode = ErrorCodes.OutOfLimits;
            rec.Values.Add(new ResultValue { SlotNo = 1, RawValue = "3.9", NumValue = 3.9m, InLimits = false });
            outc = production.SaveResult(rec, station2);
            Assert.Equal(UnitStatus.InProgress, outc.UnitStatus); Assert.Equal(20, outc.NextStepNo);

            // narzedzie przeterminowane -> NOK mimo wyniku w granicach (R1)
            meter.ValidTo = DateTime.Today.AddDays(-1); master.SaveTool(meter);
            Assert.Equal((true, false), production.CheckTool("MIERNIK-001"));
            ctx = production.OpenUnit(sn1, station);
            rec = Rec(ctx, operId, "OK"); rec.ToolSn = "MIERNIK-001";
            rec.Values.Add(new ResultValue { SlotNo = 1, RawValue = "3.3", NumValue = 3.3m, InLimits = true });
            outc = production.SaveResult(rec, station);
            Assert.Equal("NOK", outc.Result); Assert.Equal(ErrorCodes.ToolInvalid, outc.ErrorCode); Assert.Equal(20, outc.NextStepNo);
            meter.ValidTo = DateTime.Today.AddDays(10); master.SaveTool(meter);

            ctx = production.OpenUnit(sn1, station);
            rec = Rec(ctx, operId, "OK"); rec.ToolSn = "MIERNIK-001";
            rec.Values.Add(new ResultValue { SlotNo = 1, RawValue = "3.3", NumValue = 3.3m, InLimits = true });
            Assert.Equal(30, production.SaveResult(rec, station).NextStepNo);

            // --- krok 30: automat, NOK niepowtarzalny -> blokada na kazdym stanowisku
            ctx = production.OpenUnit(sn1, station);
            Assert.True(ctx.Step.AutoMeas); Assert.Equal("TESTER-7", ctx.Step.ToolSn);
            var csv = CsvMeasurement.Parse(sn1 + ";TESTER-7;5.3;OK;#\r\n", sn1, ctx.Step.Slots.Count);
            var vals = Evaluator.EvaluateSlots(ctx.Step.Slots, csv.Values, out bool allOk);
            Assert.False(allOk);
            rec = Rec(ctx, operId, "NOK"); rec.ToolSn = csv.ToolSn; rec.Values = vals; rec.ErrorCode = ErrorCodes.OutOfLimits;
            Assert.Equal(UnitStatus.Blocked, production.SaveResult(rec, station).UnitStatus);
            Assert.Equal("BLOCKED", Assert.Throws<MesException>(() => production.OpenUnit(sn1, station)).Code);
            Assert.Equal("BLOCKED", Assert.Throws<MesException>(() => production.OpenUnit(sn1, station2)).Code);

            // --- jakosc: odblokowanie, cofniecie, komentarz wymagany
            var qu = quality.Find(sn1)!;
            Assert.Equal(UnitStatus.Blocked, qu.Status);
            Assert.Throws<MesException>(() => quality.Unblock(qu.Id, " ", adminId));
            quality.Unblock(qu.Id, "ponowny pomiar", adminId);
            Assert.Throws<MesException>(() => quality.MoveTo(qu.Id, 40, "do przodu", adminId));
            Assert.Throws<MesException>(() => quality.MoveTo(qu.Id, 15, "krok spoza migawki", adminId));
            quality.MoveTo(qu.Id, 20, "powtorzyc pomiar reczny", adminId);
            Assert.Equal(20, quality.Find(sn1)!.CurrentStepNo);
            Assert.Equal(5, quality.History(qu.Id).Count);
            Assert.NotEmpty(quality.ResultDetails(quality.History(qu.Id).Last().Id));

            // sn1: 20 -> 30 -> 40 -> koniec OK
            foreach (int expected in new[] { 20, 30, 40 })
            {
                ctx = production.OpenUnit(sn1, station);
                Assert.Equal(expected, ctx.Step.StepNo);
                rec = Rec(ctx, operId, "OK");
                if (expected == 20) { rec.ToolSn = "MIERNIK-001"; rec.Values.Add(new ResultValue { SlotNo = 1, RawValue = "3.2", NumValue = 3.2m, InLimits = true }); }
                if (expected == 30) { rec.ToolSn = "TESTER-7"; rec.Values = Evaluator.EvaluateSlots(ctx.Step.Slots, new[] { "5.0", "OK" }, out _); }
                outc = production.SaveResult(rec, station);
            }
            Assert.Equal(UnitStatus.FinishedOk, outc.UnitStatus);
            Assert.Equal("FINISHED", Assert.Throws<MesException>(() => production.OpenUnit(sn1, station)).Code);

            // sn2: scrap przez jakosc; sn3: bufor offline (zapis z flaga i czasem lokalnym)
            quality.Scrap(quality.Find(sn2)!.Id, "uszkodzenie mechaniczne", adminId);
            Assert.Equal("SCRAPPED", Assert.Throws<MesException>(() => production.OpenUnit(sn2, station)).Code);
            Assert.Throws<MesException>(() => quality.Unblock(quality.Find(sn2)!.Id, "x", adminId));

            ctx = production.OpenUnit(sn3, station);
            production.ReleaseUnit(ctx.Unit.Id, station);
            ctx = production.OpenUnit(sn3, station2);
            var bufDir = Path.Combine(Path.GetTempPath(), "mesbuf_" + Guid.NewGuid().ToString("N"));
            var buffer = new OfflineBuffer(bufDir); var log = new FileLogger(Path.Combine(bufDir, "logs"));
            rec = Rec(ctx, operId, "OK");
            rec.Materials.Add(new MaterialUse { SlotNo = 1, MaterialId = screw.Id, Qty = 5, BatchNo = "SR-78" });
            rec.FinishedAtUtc = DateTime.UtcNow.AddMinutes(-3);
            buffer.Add(rec, station2);
            Assert.Equal(1, buffer.Count);
            Assert.Equal(0, buffer.Flush(production, log));
            Assert.True(quality.History(ctx.Unit.Id).Single().IsBuffered);
            Assert.Equal(20, quality.Find(sn3)!.CurrentStepNo);
            // bufor z konfliktem trafia do failed, nie blokuje
            var conflict = Rec(ctx, operId, "OK"); buffer.Add(conflict, station2);
            int rejected = 0;
            Assert.Equal(0, buffer.Flush(production, log, (_, _) => rejected++));
            Assert.Equal(1, rejected);
            Assert.Single(Directory.GetFiles(Path.Combine(bufDir, "failed")));
            Directory.Delete(bufDir, true);

            // --- KPI i eksport
            var k = reports.Kpi(o1.Id);
            Assert.Equal(3, k.Total); Assert.Equal(1, k.Ok); Assert.Equal(1, k.Scrapped); Assert.Equal(1, k.InProgress);
            // sn1: krok 20 x4 (3 ponowienia), krok 30 x2 (1 ponowienie) = 4
            Assert.Equal(4, k.Retries);
            Assert.Equal(1, k.ProductOk); Assert.Equal(1, k.ProductNok);
            Assert.Equal(4, k.Steps.Count);
            Assert.Equal(2, k.Steps[0].OkCount);                 // krok 10: sn1 + sn3
            Assert.Equal(2, k.Steps[1].NokCount);                // krok 20: 2x NOK
            Assert.NotNull(k.TaktS); Assert.NotNull(k.OpenS); Assert.NotNull(k.AvgProductionS);
            Assert.Contains(o1.OrderNo, ReportRepo.KpiText(orders.ByNo(o1.OrderNo)!, k));

            var rows = reports.OrderRows(o1.OrderNo);
            Assert.Equal(8 + 1 + 1, rows.Count);                 // 8 wynikow sn1, sn2 bez wynikow (1 wiersz), sn3 1 wynik
            var first = rows.First(r => (string?)r["serial_no"] == sn1 && (int?)r["step_no"] == 10);
            Assert.Contains("SR-77", (string)first["materials"]!); Assert.Equal("PCB-000123", first["consumed_part"]);
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            ReportRepo.WriteCsv(tmp + ".csv", reports.SerialRows(sn1), reports.SerialAudit(sn1));
            var csvText = File.ReadAllText(tmp + ".csv");
            Assert.Contains("QUALITY_MOVE", csvText); Assert.Contains("TESTER-7", csvText);
            reports.WriteExcel(tmp + ".xlsx", orders.ByNo(o1.OrderNo)!);
            Assert.True(new FileInfo(tmp + ".xlsx").Length > 2000);
            File.Delete(tmp + ".csv"); File.Delete(tmp + ".xlsx");
            Assert.Throws<MesException>(() => reports.OrderRows("ZP-NIEMA"));

            // --- zamkniecie zlecenia blokuje prace i decyzje jakosci
            Assert.Equal(1, orders.UnfinishedUnits(o1.Id));
            Assert.Equal(OrderStatus.Closed, orders.Close(o1.Id, adminId));
            Assert.Equal("ORDER_CLOSED", Assert.Throws<MesException>(() => production.OpenUnit(sn3, station)).Code);
            Assert.Throws<MesException>(() => quality.Scrap(quality.Find(sn3)!.Id, "po zamknieciu", adminId));
            Assert.Throws<MesException>(() => orders.Close(o1.Id, adminId));

            // --- licznik kontynuuje (R2.21), drugie zlecenie tego dnia -002, anulowanie niewystartowanego
            var o2 = orders.Create(pA, "BOM-2", 2, null, null, "", adminId);
            Assert.EndsWith("-002", o2.OrderNo);
            Assert.Equal("TELEFON/NOKIA10/0004 FW1.00", o2.FirstSerial);
            Assert.Equal(OrderStatus.Cancelled, orders.Close(o2.Id, adminId));
            // nowa migawka z biezacej marszruty (z krokiem 15)
            var o3 = orders.Create(pA, "BOM-2", 1, null, null, "", adminId);
            orders.Start(o3.Id, adminId);
            Assert.Equal(new[] { 10, 15, 20, 30, 40 }, routing.OrderSteps(o3.Id).Select(s => s.StepNo));
            Assert.Equal("Montaz v2", routing.OrderSteps(o3.Id)[0].Title);

            // --- przepelnienie licznika (R2.11, Z4): brak czesciowego tworzenia
            routing.SaveStep(pSmall, new Step { StepNo = 10, Title = "Jedyny", IsFinal = true }, true, adminId);
            orders.Create(pSmall, "B", 990, null, null, "", adminId);
            var ovf = Assert.Throws<MesException>(() => orders.Create(pSmall, "B", 10, null, null, "", adminId));
            Assert.Equal("COUNTER_OVERFLOW", ovf.Code); Assert.Contains("9", ovf.Pl);
            Assert.Equal("MINI-999", orders.Create(pSmall, "B", 9, null, null, "", adminId).LastSerial);

            // przyklad b)
            routing.SaveStep(pB, new Step { StepNo = 10, Title = "Jedyny", IsFinal = true }, true, adminId);
            Assert.Equal("TELEFON-0001", orders.Create(pB, "B", 1, null, null, "", adminId).FirstSerial);

            // usuwanie kroku: finalnego nie mozna, dopoki sa inne
            Assert.Throws<MesException>(() => routing.DeleteStep(pA, 40, adminId));
            routing.DeleteStep(pA, 15, adminId);
            Assert.DoesNotContain(routing.RoutingSteps(pA), s => s.StepNo == 15);
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            SchemaInstaller.Drop(admin, schema, prodUser, mgmtUser);
        }
    }

    static StepResultRecord Rec(UnitContext ctx, long userId, string result) => new()
    {
        UnitId = ctx.Unit.Id, SerialNo = ctx.Unit.SerialNo, UnitVersion = ctx.Unit.Version, StepNo = ctx.Step.StepNo,
        Result = result, UserId = userId, DurationS = 1.5
    };

    static void Exec(Npgsql.NpgsqlConnection c, string sql)
    {
        using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    static void AssertDenied(Action a)
    {
        var ex = Assert.Throws<Npgsql.PostgresException>(a);
        Assert.Equal("42501", ex.SqlState);
    }
}
