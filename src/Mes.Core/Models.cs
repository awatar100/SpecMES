namespace Mes.Core;

public class Product
{
    public long Id { get; set; }
    public string Prefix { get; set; } = "";
    public string ErpValue { get; set; } = "";
    public string Suffix { get; set; } = "";
    public int NumLength { get; set; } = 4;
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int LastValue { get; set; }
    public bool HasRouting { get; set; }
    public string Pattern => Prefix + ErpValue + "[" + new string('n', NumLength) + "]" + Suffix;
    public string Example => SerialNumber.Build(Prefix, ErpValue, Suffix, NumLength, 1);
}

public class Material
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal DefaultQty { get; set; }
    public string BatchPattern { get; set; } = "";
}

public class Tool
{
    public long Id { get; set; }
    public string ToolSn { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsValidOn(DateTime day) =>
        IsActive && (ValidFrom == null || ValidFrom.Value.Date <= day.Date) && (ValidTo == null || ValidTo.Value.Date >= day.Date);
}

public class AppUser
{
    public long Id { get; set; }
    public string Login { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Role { get; set; } = Roles.Production;
    public bool IsActive { get; set; } = true;
    public bool MustChangePwd { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public class AttachmentInfo
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string Mime { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LoginResult
{
    public string Status { get; set; } = "INVALID";   // OK / INVALID / LOCKED / INACTIVE
    public long? UserId { get; set; }
    public string? Role { get; set; }
    public string? FullName { get; set; }
    public bool MustChange { get; set; }
    public string Login { get; set; } = "";
}

public class StepSlot
{
    public long StepId { get; set; }
    public int SlotNo { get; set; }
    public string Kind { get; set; } = "NUM";      // NUM / TEXT (N4)
    public decimal? Lsl { get; set; }
    public decimal? Usl { get; set; }
    public string ExpectedText { get; set; } = "";
    public string Unit { get; set; } = "";
}

public class StepMaterial
{
    public long StepId { get; set; }
    public int SlotNo { get; set; }
    public long MaterialId { get; set; }
    public decimal Qty { get; set; }
    public string MaterialName { get; set; } = "";
    public string MaterialUnit { get; set; } = "";
    public string BatchPattern { get; set; } = "";
}

/// <summary>Krok marszruty (routing_step) lub jego migawka w zleceniu (order_step).</summary>
public class Step
{
    public long Id { get; set; }
    public int StepNo { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool ManualMeas { get; set; }
    public decimal? Lsl { get; set; }
    public decimal? Usl { get; set; }
    public string Unit { get; set; } = "";
    public bool AutoMeas { get; set; }
    public string ExePath { get; set; } = "";
    public int TimeoutS { get; set; }
    public long? ToolId { get; set; }
    public string? ToolSn { get; set; }
    public int DurationS { get; set; }
    public string WorkInstr { get; set; } = "";
    public bool Consumes { get; set; }
    public bool IsFinal { get; set; }
    public bool IsRepeatable { get; set; }
    public List<StepSlot> Slots { get; set; } = new();
    public List<StepMaterial> Materials { get; set; } = new();

    public string Kind => AutoMeas ? L.T("Pomiar automatyczny", "Automatic measurement")
        : ManualMeas ? L.T("Pomiar ręczny", "Manual measurement") : L.T("Praca ręczna", "Manual work");
}

public class WorkOrder
{
    public long Id { get; set; }
    public string OrderNo { get; set; } = "";
    public long ProductId { get; set; }
    public string ProductPattern { get; set; } = "";
    public string BomVersion { get; set; } = "";
    public int Qty { get; set; }
    public DateTime? PlannedStart { get; set; }
    public DateTime? PlannedEnd { get; set; }
    public string Notes { get; set; } = "";
    public string Status { get; set; } = OrderStatus.Created;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? FirstSerial { get; set; }
    public string? LastSerial { get; set; }
}

public class UnitInfo
{
    public long Id { get; set; }
    public string SerialNo { get; set; } = "";
    public long OrderId { get; set; }
    public string OrderNo { get; set; } = "";
    public string OrderStatus { get; set; } = "";
    public string BomVersion { get; set; } = "";
    public int? CurrentStepNo { get; set; }
    public string Status { get; set; } = "";
    public int Version { get; set; }
    public long? LockedStationId { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LockedHost { get; set; }
    public bool LockActive { get; set; }
}

/// <summary>Sztuka otwarta na stanowisku wraz z konfiguracja biezacego kroku.</summary>
public class UnitContext
{
    public UnitInfo Unit { get; set; } = new();
    public Step Step { get; set; } = new();
    public int StepIndex { get; set; }   // 1..StepCount
    public int StepCount { get; set; }
}

public class ResultValue
{
    public int SlotNo { get; set; }
    public string RawValue { get; set; } = "";
    public decimal? NumValue { get; set; }
    public bool InLimits { get; set; }
}

public class MaterialUse
{
    public int SlotNo { get; set; }
    public long MaterialId { get; set; }
    public decimal Qty { get; set; }
    public string BatchNo { get; set; } = "";
}

/// <summary>Wynik kroku – zapisywany do bazy lub do bufora offline (JSON).</summary>
public class StepResultRecord
{
    public Guid ClientGuid { get; set; } = Guid.NewGuid();
    public long UnitId { get; set; }
    public string SerialNo { get; set; } = "";
    public int UnitVersion { get; set; }
    public int StepNo { get; set; }
    public string Result { get; set; } = "OK";
    public string Comment { get; set; } = "";
    public long UserId { get; set; }
    public string? ToolSn { get; set; }
    public double DurationS { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public bool IsBuffered { get; set; }
    public string? ErrorCode { get; set; }
    public List<ResultValue> Values { get; set; } = new();
    public List<MaterialUse> Materials { get; set; } = new();
    public string? ConsumedPart { get; set; }
}

public class SaveOutcome
{
    public bool AlreadySaved { get; set; }
    public string Result { get; set; } = "OK";
    public string? ErrorCode { get; set; }
    public string UnitStatus { get; set; } = "";
    public int? NextStepNo { get; set; }
}

public class ResultRow
{
    public long Id { get; set; }
    public int StepNo { get; set; }
    public string StepTitle { get; set; } = "";
    public string Result { get; set; } = "";
    public string? ErrorCode { get; set; }
    public string Comment { get; set; } = "";
    public string UserLogin { get; set; } = "";
    public string Station { get; set; } = "";
    public string? ToolSn { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public bool IsBuffered { get; set; }
}

public class KeyValueRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public KeyValueRow() { }
    public KeyValueRow(string k, string? v) { Key = k; Value = v ?? ""; }
}

public class StepKpi
{
    public int StepNo { get; set; }
    public string Title { get; set; } = "";
    public int PlannedS { get; set; }
    public double? AvgActualS { get; set; }
    public int OkCount { get; set; }
    public int NokCount { get; set; }
    public double? AvgTransitionS { get; set; }   // od konca poprzedniego kroku do startu tego
}

public class OrderKpi
{
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Nok { get; set; }
    public int Scrapped { get; set; }
    public int Blocked { get; set; }
    public int InProgress { get; set; }
    public double? TaktS { get; set; }
    public double? OpenS { get; set; }
    public double? AvgProductionS { get; set; }
    public double? AvgTransitionS { get; set; }
    public int Retries { get; set; }
    public int ProductOk { get; set; }
    public int ProductNok { get; set; }
    public List<StepKpi> Steps { get; set; } = new();

    public double OkPct => Total == 0 ? 0 : 100.0 * Ok / Total;
    public double NokPct => Total == 0 ? 0 : 100.0 * (Nok + Scrapped) / Total;
    public double ProductDefectPct => ProductOk + ProductNok == 0 ? 0 : 100.0 * ProductNok / (ProductOk + ProductNok);

    public static string Dur(double? s)
    {
        if (s == null) return "—";
        var t = TimeSpan.FromSeconds(s.Value);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min"
            : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min {t.Seconds} s"
            : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s" : $"{s.Value:0.0} s";
    }
}
