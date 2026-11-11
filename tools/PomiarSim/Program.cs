// Symulator zewnetrznego programu pomiarowego (pomiar.exe) – tylko do testow ProdAPP.
// Wywolanie:  pomiar -"NUMER SERYJNY"
// Kasuje current.csv i tworzy go na nowo:  NUMER;TOOL;WYNIK1;...;WYNIKn;# + CR LF
// oraz rownolegle plik <numer z zamienionymi znakami>.csv.
// Zachowanie ustawia plik pomiar.cfg w katalogu programu (klucz=wartosc):
//   tool=TESTER-7            numer urzadzenia pomiarowego wpisywany do CSV
//   values=5.02;OK           wyniki (kropka dziesietna, separator ;)
//   mode=ok                  ok | nofile | badformat | wrongserial | hang
//   delay=1                  opoznienie zapisu pliku [s]
using System.Text;

string dir = AppContext.BaseDirectory;
string serial = args.Length > 0 ? args[0] : "";
if (serial.StartsWith('-')) serial = serial[1..];

var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tool"] = "TESTER-7", ["values"] = "5.02;OK", ["mode"] = "ok", ["delay"] = "1" };
string cfgPath = Path.Combine(dir, "pomiar.cfg");
if (File.Exists(cfgPath))
    foreach (var line in File.ReadAllLines(cfgPath))
    {
        int i = line.IndexOf('=');
        if (i > 0 && !line.TrimStart().StartsWith('#')) cfg[line[..i].Trim()] = line[(i + 1)..].Trim();
    }

string current = Path.Combine(dir, "current.csv");
if (cfg["mode"] != "stale") File.Delete(current);

Thread.Sleep((int)(double.Parse(cfg["delay"], System.Globalization.CultureInfo.InvariantCulture) * 1000));

switch (cfg["mode"])
{
    case "nofile": return 1;
    case "stale": return 0;                               // zostawia stary plik bez zmian
    case "hang": Thread.Sleep(Timeout.Infinite); return 0;
}
string sn = cfg["mode"] == "wrongserial" ? "INNY-NUMER-0001" : serial;
string content = $"{sn};{cfg["tool"]};{cfg["values"]}" + (cfg["mode"] == "badformat" ? "" : ";#") + "\r\n";
File.WriteAllText(current, content, new UTF8Encoding(false));

var bad = new HashSet<char>("\\/:*?\"<>|");
string safe = new string(serial.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
if (safe.Length > 0) File.WriteAllText(Path.Combine(dir, safe + ".csv"), content, new UTF8Encoding(false));
return 0;
