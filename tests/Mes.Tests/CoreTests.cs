using Mes.Core;
using Xunit;

namespace Mes.Tests;

public class CoreTests
{
    [Fact]
    public void Serial_examples_from_requirements()
    {
        // a) TELEFON/NOKIA10/0001 FW1.00   b) TELEFON-0001
        Assert.Equal("TELEFON/NOKIA10/0001 FW1.00", SerialNumber.Build("TELEFON/", "NOKIA10/", " FW1.00", 4, 1));
        Assert.Equal("TELEFON-0001", SerialNumber.Build("TELEFON-", "", "", 4, 1));
        Assert.Equal(999, SerialNumber.MaxValue(3));
        Assert.Equal(999999, SerialNumber.MaxValue(6));
        Assert.Equal("TELEFON_NOKIA10_0001 FW1.00", SerialNumber.FileSafe("TELEFON/NOKIA10/0001 FW1.00"));
    }

    [Theory]
    [InlineData("AB$", "", "", 4)]
    [InlineData("AB", "x;y", "", 4)]
    [InlineData("AB", "", "a#", 4)]
    [InlineData("", "", "", 4)]
    [InlineData("AB", "", "", 2)]
    [InlineData("AB", "", "", 7)]
    [InlineData("A\tB", "", "", 4)]
    public void Serial_definition_rejects_invalid(string p, string e, string s, int len) =>
        Assert.Throws<MesException>(() => SerialNumber.ValidateDefinition(p, e, s, len));

    [Fact]
    public void Csv_parse_ok_and_errors()
    {
        const string sn = "TELEFON/NOKIA10/0001 FW1.00";
        var ok = CsvMeasurement.Parse(sn + ";TOOL-1;3.30;5.01;OK;#\r\n", sn, 3);
        Assert.True(ok.Ok);
        Assert.Equal("TOOL-1", ok.ToolSn);
        Assert.Equal(new[] { "3.30", "5.01", "OK" }, ok.Values);
        Assert.True(CsvMeasurement.Parse("\uFEFF" + sn + ";T;1;#\n", sn, 1).Ok);
        Assert.Equal(ErrorCodes.MeasFormat, CsvMeasurement.Parse(sn + ";T;1;2;#\r", sn, 3).Error);     // zla liczba
        Assert.Equal(ErrorCodes.MeasFormat, CsvMeasurement.Parse(sn + ";T;1;2;3\r\n", sn, 3).Error);    // brak ;#
        Assert.Equal(ErrorCodes.MeasFormat, CsvMeasurement.Parse(sn + ";T;1;2;3;#", sn, 3).Error);      // brak CR
        Assert.Equal(ErrorCodes.MeasStale, CsvMeasurement.Parse("OTHER;T;1;2;3;#\r\n", sn, 3).Error);   // obcy numer
    }

    [Fact]
    public void Evaluator_limits_inclusive_and_text_slots()
    {
        Assert.True(Evaluator.InLimits(3.0m, 3.0m, 3.6m));
        Assert.True(Evaluator.InLimits(3.6m, 3.0m, 3.6m));
        Assert.False(Evaluator.InLimits(3.61m, 3.0m, 3.6m));
        Assert.True(Evaluator.TryNumManual("3,3", out var v) && v == 3.3m);

        var slots = new List<StepSlot>
        {
            new() { SlotNo = 1, Kind = "NUM", Lsl = 3.0m, Usl = 3.6m },
            new() { SlotNo = 2, Kind = "TEXT", ExpectedText = "OK" },
        };
        Evaluator.EvaluateSlots(slots, new[] { "3.3", "OK" }, out bool all);
        Assert.True(all);
        var r = Evaluator.EvaluateSlots(slots, new[] { "3.3", "OTHERS" }, out all);
        Assert.False(all); Assert.False(r[1].InLimits);
        Evaluator.EvaluateSlots(slots, new[] { "abc", "OK" }, out all);
        Assert.False(all);
        Evaluator.EvaluateSlots(slots, new[] { "3.3" }, out all);
        Assert.False(all);
    }

    static Step ManualStep() => new() { StepNo = 10, Title = "Inspekcja" };

    [Fact]
    public void Step_validation_rules()
    {
        StepValidator.Validate(ManualStep());   // Z13: sama decyzja OK/NOK

        var auto = new Step { StepNo = 20, Title = "Test", AutoMeas = true, ExePath = "C:\\t\\pomiar.exe", TimeoutS = 5, ToolId = 1,
            Slots = { new StepSlot { SlotNo = 1, Kind = "NUM", Lsl = 1, Usl = 2 } } };
        StepValidator.Validate(auto);
        auto.Consumes = true;                    // N6
        Assert.Throws<MesException>(() => StepValidator.Validate(auto));
        auto.Consumes = false; auto.ManualMeas = true;   // R2.6
        Assert.Throws<MesException>(() => StepValidator.Validate(auto));
        auto.ManualMeas = false; auto.Slots[0].SlotNo = 2;   // przerwa w slotach
        Assert.Throws<MesException>(() => StepValidator.Validate(auto));

        var many = ManualStep();
        many.ManualMeas = true; many.Lsl = 1; many.Usl = 2; many.ToolId = 1;
        for (int i = 1; i <= 10; i++) many.Materials.Add(new StepMaterial { SlotNo = i, MaterialId = 1, Qty = 1 });
        Assert.Throws<MesException>(() => StepValidator.Validate(many));   // N5: 11 akcji
        many.ManualMeas = false;
        StepValidator.Validate(many);

        Assert.Equal(10, StepValidator.SuggestNext(Array.Empty<int>()));
        Assert.Equal(30, StepValidator.SuggestNext(new[] { 10, 15, 20 }));
        Assert.Equal(30, StepValidator.SuggestNext(new[] { 10, 25 }));
    }

    [Fact]
    public void Routing_start_requires_single_final_last_step()
    {
        var a = ManualStep(); var b = new Step { StepNo = 20, Title = "B" };
        Assert.Throws<MesException>(() => StepValidator.ValidateRoutingForStart(new List<Step>()));
        Assert.Throws<MesException>(() => StepValidator.ValidateRoutingForStart(new[] { a, b }));
        a.IsFinal = true;
        Assert.Throws<MesException>(() => StepValidator.ValidateRoutingForStart(new[] { a, b }));
        a.IsFinal = false; b.IsFinal = true;
        StepValidator.ValidateRoutingForStart(new[] { a, b });
    }

    [Fact]
    public void Setup_ini_roundtrip_and_passwords()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ini");
        try
        {
            SetupIni.Write(path, new SetupConfig { Host = "10.0.0.1", Port = 5432, Database = "db", Schema = "prod1", User = "prod1_prod", Password = "s3cr=et\"x" });
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("s3cr", text);
            Assert.DoesNotContain("prod1_prod", text);
            var c = SetupIni.Read(path);
            Assert.Equal("prod1_prod", c.User); Assert.Equal("s3cr=et\"x", c.Password); Assert.Equal("prod1", c.Schema);
        }
        finally { File.Delete(path); }

        var h = PasswordHasher.Hash("DATA").Split('$');
        Assert.Equal(h[3], PasswordHasher.Derive("DATA", int.Parse(h[1]), h[2]));
        Assert.NotEqual(h[3], PasswordHasher.Derive("data", int.Parse(h[1]), h[2]));
    }

    [Fact]
    public void Permissions_matrix_and_batch_pattern()
    {
        Assert.Equal(Access.None, Permissions.Get(Roles.Production, Module.Orders));
        Assert.Equal(Access.Write, Permissions.Get(Roles.Planner, Module.Orders));
        Assert.Equal(Access.Read, Permissions.Get(Roles.Planner, Module.Routing));
        Assert.Equal(Access.Write, Permissions.Get(Roles.Technologist, Module.Routing));
        Assert.Equal(Access.Write, Permissions.Get(Roles.Quality, Module.Quality));
        Assert.Equal(Access.None, Permissions.Get(Roles.Technologist, Module.Quality));
        Assert.Equal(Access.None, Permissions.Get(Roles.Quality, Module.Users));
        foreach (Module m in Enum.GetValues<Module>()) Assert.Equal(Access.Write, Permissions.Get(Roles.MasterData, m));
        Assert.True(Permissions.CanUseProdApp(Roles.Production));
        Assert.False(Permissions.CanUseProdApp(Roles.Planner));
        Assert.False(Permissions.CanUseManagement(Roles.Production));

        Assert.True(BatchPattern.Matches("", "anything"));
        Assert.True(BatchPattern.Matches("KLEJ*", "KLEJ-2026/11"));
        Assert.False(BatchPattern.Matches("KLEJ*", "SRUBA-1"));
    }
}
