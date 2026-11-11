using System.Globalization;

namespace Mes.Core;

/// <summary>Przelaczanie jezyka PL/EN (R2.3, Z37). Teksty podawane parami w miejscu uzycia.</summary>
public static class L
{
    public static bool English { get; private set; }
    public static event Action? Changed;
    public static string T(string pl, string en) => English ? en : pl;
    public static void Set(bool english)
    {
        if (English == english) return;
        English = english;
        Changed?.Invoke();
    }
}

/// <summary>Blad biznesowy z komunikatem w obu jezykach.</summary>
public class MesException : Exception
{
    public string Pl { get; }
    public string En { get; }
    public string Code { get; }
    public MesException(string pl, string en, string code = "") : base(en) { Pl = pl; En = en; Code = code; }
    public override string Message => L.T(Pl, En);
}

public static class Roles
{
    public const string Production = "PRODUCTION", Planner = "PLANNER", Technologist = "TECHNOLOGIST",
        Quality = "QUALITY", MasterData = "MASTERDATA";
    public static readonly string[] All = { Production, Planner, Technologist, Quality, MasterData };
    public static string Name(string role) => role switch
    {
        Production => L.T("Produkcja", "Production"),
        Planner => L.T("Planista", "Planner"),
        Technologist => L.T("Technolog", "Technologist"),
        Quality => L.T("Jakość", "Quality"),
        MasterData => "MasterDATA",
        _ => role
    };
}

public static class UnitStatus
{
    public const string InProgress = "IN_PROGRESS", Blocked = "BLOCKED", FinishedOk = "FINISHED_OK",
        FinishedNok = "FINISHED_NOK", Scrapped = "SCRAPPED";
    public static string Name(string? s) => s switch
    {
        InProgress => L.T("W toku", "In progress"),
        Blocked => L.T("Zablokowana", "Blocked"),
        FinishedOk => L.T("Zakończona OK", "Finished OK"),
        FinishedNok => L.T("Zakończona NOK", "Finished NOK"),
        Scrapped => "SCRAP",
        _ => s ?? ""
    };
}

public static class OrderStatus
{
    public const string Created = "CREATED", Active = "ACTIVE", Closed = "CLOSED", Cancelled = "CANCELLED";
    public static string Name(string? s) => s switch
    {
        Created => L.T("Utworzone", "Created"),
        Active => L.T("Aktywne", "Active"),
        Closed => L.T("Zamknięte", "Closed"),
        Cancelled => L.T("Anulowane", "Cancelled"),
        _ => s ?? ""
    };
}

public static class ErrorCodes
{
    public const string MeasTimeout = "MEAS_TIMEOUT", MeasFormat = "MEAS_FORMAT", MeasStale = "MEAS_STALE",
        ToolMismatch = "TOOL_MISMATCH", ToolInvalid = "TOOL_INVALID", OutOfLimits = "OUT_OF_LIMITS",
        ProcessError = "PROCESS_ERROR";
    public static string Name(string? code) => code switch
    {
        MeasTimeout => L.T("Błąd zewnętrznego systemu pomiarowego (brak pliku current.csv po upływie czasu)", "External measurement system error (no current.csv after timeout)"),
        MeasFormat => L.T("Zły format pliku current.csv", "Bad current.csv format"),
        MeasStale => L.T("Stary lub obcy plik current.csv", "Stale or foreign current.csv file"),
        ToolMismatch => L.T("Narzędzie niezgodne z marszrutą", "Tool does not match the routing"),
        ToolInvalid => L.T("Narzędzie nieważne (nieaktywne lub poza datami)", "Tool invalid (inactive or out of validity dates)"),
        OutOfLimits => L.T("Wynik poza granicami", "Result out of limits"),
        ProcessError => L.T("Nie można uruchomić programu pomiarowego", "Cannot start the measurement program"),
        null or "" => "",
        _ => code
    };
}

public enum Module { Products, Orders, Routing, Quality, Export, Materials, Tools, Kpi, Users }
public enum Access { None, Read, Write }

/// <summary>Macierz rol i uprawnien z analizy wymagan.</summary>
public static class Permissions
{
    //                                   Products Orders Routing Quality Export Materials Tools Kpi Users
    static readonly Dictionary<string, string> Matrix = new()
    {
        [Roles.Production] = "---------",
        [Roles.Planner] = "WWR-RWRR-",
        [Roles.Technologist] = "RRW-RWWR-",
        [Roles.Quality] = "RRRWWRWR-",
        [Roles.MasterData] = "WWWWWWWWW",
    };
    public static Access Get(string role, Module m) =>
        Matrix.TryGetValue(role, out var row)
            ? row[(int)m] switch { 'W' => Access.Write, 'R' => Access.Read, _ => Access.None }
            : Access.None;
    public static bool CanUseProdApp(string role) => role is Roles.Production or Roles.MasterData;
    public static bool CanUseManagement(string role) => role != Roles.Production && Matrix.ContainsKey(role);
}

/// <summary>R2.16: alfanumeryczny = dowolne znaki drukowalne z wyjatkiem $%^&amp;;# (N1: takze bez znakow sterujacych).</summary>
public static class TextRules
{
    public const string Forbidden = "$%^&;#";
    public static bool IsValid(string? s) => s != null && !s.Any(c => Forbidden.Contains(c) || char.IsControl(c));
    public static void Require(string? s, string fieldPl, string fieldEn, int maxLen, bool required = true)
    {
        s ??= "";
        if (required && s.Length == 0)
            throw new MesException($"Pole „{fieldPl}” jest wymagane.", $"Field \"{fieldEn}\" is required.");
        if (s.Length > maxLen)
            throw new MesException($"Pole „{fieldPl}” może mieć najwyżej {maxLen} znaków.", $"Field \"{fieldEn}\" may have at most {maxLen} characters.");
        if (!IsValid(s))
            throw new MesException($"Pole „{fieldPl}” zawiera niedozwolone znaki ({Forbidden} lub znaki sterujące).", $"Field \"{fieldEn}\" contains forbidden characters ({Forbidden} or control characters).");
    }
}

/// <summary>Numer seryjny: [PREFIX][WARTOSC ERP][Num++][SUFIX] (R1).</summary>
public static class SerialNumber
{
    public const int MinLen = 3, MaxLen = 6;
    public static int MaxValue(int numLength) => (int)Math.Pow(10, numLength) - 1;
    public static string Build(string prefix, string? erp, string? suffix, int numLength, int n) =>
        prefix + (erp ?? "") + n.ToString("D" + numLength, CultureInfo.InvariantCulture) + (suffix ?? "");

    public static void ValidateDefinition(string? prefix, string? erp, string? suffix, int numLength)
    {
        TextRules.Require(prefix, "PREFIX", "PREFIX", 50);
        TextRules.Require(erp, "WARTOŚĆ ERP", "ERP VALUE", 50, false);
        TextRules.Require(suffix, "SUFIX", "SUFFIX", 50, false);
        if (numLength < MinLen || numLength > MaxLen)
            throw new MesException($"Długość pola Num++ musi być w zakresie {MinLen}–{MaxLen}.", $"Num++ length must be between {MinLen} and {MaxLen}.");
    }

    /// <summary>Nazwa pliku z zamienionymi znakami zakazanymi przez system plikow (logika pomiar.exe, poza zakresem – pomocniczo).</summary>
    public static string FileSafe(string serial)
    {
        var bad = new HashSet<char>("\\/:*?\"<>|");
        return new string(serial.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
    }
}

/// <summary>Wzorzec walidacji partii materialu, np. KLEJ* (Z15). Pusty = dowolny ciag.</summary>
public static class BatchPattern
{
    public static bool Matches(string? pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, rx);
    }
}

/// <summary>Prosty log plikowy: pliki dzienne, 30 dni (Z32). Zawsze po angielsku.</summary>
public class FileLogger
{
    readonly string _dir;
    readonly object _lock = new();
    public FileLogger(string dir)
    {
        _dir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f);
        }
        catch { /* logging must never crash the app */ }
    }
    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : msg + " :: " + ex);
    void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(Path.Combine(_dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
