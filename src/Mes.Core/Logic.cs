using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mes.Core;

/// <summary>Wynik parsowania current.csv.</summary>
public record CsvResult(string? Error, string ToolSn, string[] Values)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Parser pliku current.csv (R1, Z21, Z23):
/// LINIA 1 = NUMER_SERYJNY;NUMER_URZADZENIA;WYNIK1;...;WYNIKn;# + CR/LF/CRLF.
/// </summary>
public static class CsvMeasurement
{
    public static CsvResult Parse(string content, string serial, int expectedCount)
    {
        content = content.TrimStart('﻿');
        int nl = content.IndexOfAny(new[] { '\r', '\n' });
        if (nl < 0) return new CsvResult(ErrorCodes.MeasFormat, "", Array.Empty<string>());   // brak ack ;#+CR
        var line = content[..nl];
        if (!line.EndsWith(";#", StringComparison.Ordinal)) return new CsvResult(ErrorCodes.MeasFormat, "", Array.Empty<string>());
        var parts = line[..^2].Split(';');
        if (parts.Length < 2) return new CsvResult(ErrorCodes.MeasFormat, "", Array.Empty<string>());
        if (parts[0] != serial) return new CsvResult(ErrorCodes.MeasStale, parts[1], Array.Empty<string>());
        if (parts.Length != expectedCount + 2) return new CsvResult(ErrorCodes.MeasFormat, parts[1], Array.Empty<string>());
        return new CsvResult(null, parts[1], parts.Skip(2).ToArray());
    }

    public static CsvResult ParseFile(string path, string serial, int expectedCount, DateTime startedUtc)
    {
        if (File.GetLastWriteTimeUtc(path) < startedUtc.AddSeconds(-1))
            return new CsvResult(ErrorCodes.MeasStale, "", Array.Empty<string>());
        return Parse(File.ReadAllText(path, Encoding.UTF8), serial, expectedCount);
    }
}

public static class Evaluator
{
    public static bool TryNum(string? s, out decimal v) =>
        decimal.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Pomiar reczny: operator moze wpisac przecinek dziesietny.</summary>
    public static bool TryNumManual(string? s, out decimal v) => TryNum((s ?? "").Replace(',', '.'), out v);

    /// <summary>Z24: granice wlacznie.</summary>
    public static bool InLimits(decimal v, decimal? lsl, decimal? usl) => (lsl == null || v >= lsl) && (usl == null || v <= usl);

    /// <summary>Ocena slotow pomiaru automatycznego (N4: slot liczbowy LSL/USL albo tekstowy z wartoscia oczekiwana).</summary>
    public static List<ResultValue> EvaluateSlots(IList<StepSlot> slots, string[] values, out bool allOk)
    {
        var list = new List<ResultValue>();
        allOk = slots.Count == values.Length && slots.Count > 0;
        for (int i = 0; i < Math.Min(slots.Count, values.Length); i++)
        {
            var slot = slots[i];
            var rv = new ResultValue { SlotNo = slot.SlotNo, RawValue = values[i] };
            if (slot.Kind == "TEXT")
                rv.InLimits = string.Equals(values[i].Trim(), slot.ExpectedText.Trim(), StringComparison.Ordinal);
            else if (TryNum(values[i], out var v))
            {
                rv.NumValue = v;
                rv.InLimits = InLimits(v, slot.Lsl, slot.Usl);
            }
            if (!rv.InLimits) allOk = false;
            list.Add(rv);
        }
        return list;
    }
}

/// <summary>Walidacja definicji kroku i marszruty (R2.6, N2, N5, N6, Z8, Z13).</summary>
public static class StepValidator
{
    public static void Validate(Step s)
    {
        if (s.StepNo <= 0) throw new MesException("Numer kroku musi być większy od 0.", "Step number must be greater than 0.");
        TextRules.Require(s.Title, "Opis kroku", "Step title", 50);
        if ((s.Body ?? "").Length > 500) throw new MesException("Treść kroku może mieć najwyżej 500 znaków.", "Step body may have at most 500 characters.");
        if ((s.Unit ?? "").Length > 10) throw new MesException("Jednostka może mieć najwyżej 10 znaków.", "Unit may have at most 10 characters.");
        if (s.DurationS < 0) throw new MesException("Czas trwania kroku nie może być ujemny.", "Step duration cannot be negative.");
        if (s.ManualMeas && s.AutoMeas)
            throw new MesException("Pomiar automatyczny wyklucza pomiar ręczny.", "Automatic measurement excludes manual measurement.");

        if (s.AutoMeas)
        {
            if (s.Materials.Count > 0 || s.Consumes)
                throw new MesException("Pomiar automatyczny wyklucza dodawanie materiału i Consumes.", "Automatic measurement excludes materials and Consumes.");
            if (s.Slots.Count is < 1 or > 10)
                throw new MesException("Pomiar automatyczny wymaga od 1 do 10 zdefiniowanych wyników.", "Automatic measurement requires 1 to 10 defined results.");
            for (int i = 0; i < s.Slots.Count; i++)
            {
                var sl = s.Slots[i];
                if (sl.SlotNo != i + 1)
                    throw new MesException("Wyniki pomiaru automatycznego muszą być wypełnione kolejno od 1 (bez przerw).", "Automatic results must be filled consecutively from 1 (no gaps).");
                if (sl.Kind == "TEXT")
                {
                    if (string.IsNullOrWhiteSpace(sl.ExpectedText) || !TextRules.IsValid(sl.ExpectedText))
                        throw new MesException($"Wynik {sl.SlotNo}: podaj poprawną wartość oczekiwaną.", $"Result {sl.SlotNo}: enter a valid expected value.");
                }
                else if (sl.Lsl == null || sl.Usl == null || sl.Lsl > sl.Usl)
                    throw new MesException($"Wynik {sl.SlotNo}: wymagane LSL ≤ USL.", $"Result {sl.SlotNo}: LSL ≤ USL required.");
            }
            if (string.IsNullOrWhiteSpace(s.ExePath))
                throw new MesException("Podaj ścieżkę programu pomiarowego.", "Enter the measurement program path.");
            if (s.TimeoutS <= 0)
                throw new MesException("Timeout pomiaru musi być większy od 0 s.", "Measurement timeout must be greater than 0 s.");
            if (s.ToolId == null)
                throw new MesException("Pomiar automatyczny wymaga wskazania narzędzia (ToolID z pliku CSV).", "Automatic measurement requires a tool (ToolID from the CSV file).");
        }
        else if (s.Slots.Count > 0)
            throw new MesException("Wyniki 1–10 są dostępne tylko dla pomiaru automatycznego.", "Results 1–10 are only available for automatic measurement.");

        if (s.ManualMeas)
        {
            if (s.Lsl == null || s.Usl == null || s.Lsl > s.Usl)
                throw new MesException("Pomiar ręczny wymaga LSL ≤ USL.", "Manual measurement requires LSL ≤ USL.");
            if (s.ToolId == null)
                throw new MesException("Pomiar ręczny wymaga wskazania narzędzia.", "Manual measurement requires a tool.");
        }

        if (s.Materials.Count > 10) throw new MesException("Maksymalnie 10 materiałów w kroku.", "At most 10 materials per step.");
        foreach (var m in s.Materials)
        {
            if (m.MaterialId <= 0) throw new MesException($"Materiał {m.SlotNo}: wybierz materiał z listy.", $"Material {m.SlotNo}: choose a material from the list.");
            if (m.Qty <= 0) throw new MesException($"Materiał {m.SlotNo}: ilość musi być większa od 0.", $"Material {m.SlotNo}: quantity must be greater than 0.");
        }
        int inputs = s.Materials.Count + (s.ManualMeas ? 1 : 0) + (s.Consumes ? 1 : 0);
        if (inputs > 10)
            throw new MesException("Krok może mieć najwyżej 10 akcji (materiały + pomiar ręczny + Consumes).", "A step may have at most 10 actions (materials + manual measurement + Consumes).");
    }

    /// <summary>Z8: start zlecenia wymaga min. 1 kroku i dokladnie jednego kroku finalnego o najwyzszym numerze.</summary>
    public static void ValidateRoutingForStart(IList<Step> steps)
    {
        if (steps.Count == 0)
            throw new MesException("Marszruta wyrobu nie ma żadnego kroku.", "The product routing has no steps.");
        var finals = steps.Where(x => x.IsFinal).ToList();
        if (finals.Count != 1)
            throw new MesException("Marszruta musi mieć dokładnie jeden krok finalny.", "The routing must have exactly one final step.");
        if (finals[0].StepNo != steps.Max(x => x.StepNo))
            throw new MesException("Krok finalny musi mieć najwyższy numer w marszrucie.", "The final step must have the highest number in the routing.");
        foreach (var s in steps) Validate(s);
    }

    /// <summary>R2.12: podpowiedz kolejnej wielokrotnosci 10.</summary>
    public static int SuggestNext(IEnumerable<int> existing)
    {
        int max = existing.DefaultIfEmpty(0).Max();
        return (max / 10 + 1) * 10;
    }
}

/// <summary>Skroty hasel uzytkownikow aplikacji: PBKDF2-SHA256. Format: pbkdf2$iter$salt$hash.</summary>
public static class PasswordHasher
{
    const int Iter = 100_000;
    public static string Hash(string password)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        return $"pbkdf2${Iter}${salt}${Derive(password, Iter, salt)}";
    }
    public static string Derive(string password, int iter, string saltB64) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(saltB64), iter, HashAlgorithmName.SHA256, 32));

    public static string RandomPassword(int len = 20)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return new string(Enumerable.Range(0, len).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }
}

public class SetupConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "";
    public string Schema { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>setup.ini: adres, port, baza, schemat jawnie; login i haslo roli bazodanowej zaszyfrowane AES-256-GCM (R2.13).</summary>
public static class SetupIni
{
    // Klucz wbudowany w aplikacje (ryzyko opisane w analizie: rola produkcyjna ma minimalne uprawnienia).
    static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("MES::setup.ini::v1::8c1f4e0b-5d0a-4f57-9d3e-2a6b7c91e455"));

    public static string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public static string Decrypt(string b64)
    {
        var all = Convert.FromBase64String(b64);
        var nonce = all[..12]; var tag = all[12..28]; var cipher = all[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static void Write(string path, SetupConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; MES setup.ini - wygenerowane przez Instalator / generated by the Installer");
        sb.AppendLine("[mes]");
        sb.AppendLine($"host={c.Host}");
        sb.AppendLine($"port={c.Port}");
        sb.AppendLine($"database={c.Database}");
        sb.AppendLine($"schema={c.Schema}");
        sb.AppendLine($"credentials={Encrypt(c.User + "\n" + c.Password)}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    public static SetupConfig Read(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;
            int i = line.IndexOf('=');
            if (i > 0) d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        string Get(string k) => d.TryGetValue(k, out var v) && v.Length > 0 ? v
            : throw new MesException($"Brak wpisu „{k}” w setup.ini.", $"Missing \"{k}\" entry in setup.ini.");
        string[] cred;
        try { cred = Decrypt(Get("credentials")).Split('\n', 2); }
        catch (Exception ex) when (ex is not MesException)
        { throw new MesException("Nie można odszyfrować danych logowania w setup.ini.", "Cannot decrypt credentials in setup.ini."); }
        return new SetupConfig
        {
            Host = Get("host"), Port = int.Parse(Get("port")), Database = Get("database"), Schema = Get("schema"),
            User = cred[0], Password = cred.Length > 1 ? cred[1] : ""
        };
    }
}
