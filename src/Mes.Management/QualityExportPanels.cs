using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Data;
using Mes.Shared.UI;

namespace Mes.Management;

/// <summary>R3.2d: modul jakosci – skan numeru, stan sztuki, odblokowanie, cofniecie do kroku, scrap.</summary>
public class QualityPanel : Panel
{
    readonly Ctx _c;
    readonly TextBox _scan = new() { Font = Fonts.Sans(14) }, _where = new() { ReadOnly = true, Font = Fonts.Sans(12) }, _comment = new() { MaxLength = 500 };
    readonly DropDown _moveTo = new() { Width = 260 };
    readonly GridView _history, _details;
    UnitInfo? _unit;

    public QualityPanel(Ctx c, bool write)
    {
        _c = c;
        Ui.OnEnter(_scan, Find);
        _history = Ui.Grid<ResultRow>(
            Ui.Col<ResultRow>(L.T("Zakończono", "Finished"), r => Ui.Local(r.FinishedAt), 145), Ui.Col<ResultRow>(L.T("Krok", "Step"), r => r.StepNo.ToString(), 50),
            Ui.Col<ResultRow>(L.T("Opis", "Title"), r => r.StepTitle, 180), Ui.Col<ResultRow>(L.T("Wynik", "Result"), r => r.Result, 60),
            Ui.Col<ResultRow>(L.T("Przyczyna", "Reason"), r => ErrorCodes.Name(r.ErrorCode), 220), Ui.Col<ResultRow>(L.T("Komentarz", "Comment"), r => r.Comment, 200),
            Ui.Col<ResultRow>(L.T("Użytkownik", "User"), r => r.UserLogin, 100), Ui.Col<ResultRow>(L.T("Stanowisko", "Station"), r => r.Station, 130),
            Ui.Col<ResultRow>(L.T("Narzędzie", "Tool"), r => r.ToolSn ?? "", 120),
            Ui.Col<ResultRow>(L.T("Czas [s]", "Time [s]"), r => (r.FinishedAt - r.StartedAt).TotalSeconds.ToString("0.0"), 70),
            Ui.Col<ResultRow>(L.T("Bufor", "Buffered"), r => r.IsBuffered ? "✓" : "", 60));
        _details = Ui.Grid<KeyValueRow>(Ui.Col<KeyValueRow>(L.T("Pozycja", "Item"), r => r.Key, 140), Ui.Col<KeyValueRow>(L.T("Wartość", "Value"), r => r.Value, 420));
        _history.SelectionChanged += (_, _) =>
        {
            if (_history.SelectedItem is ResultRow r) Ui.Try(this, () => _details.DataStore = _c.Quality.ResultDetails(r.Id), _c.Log);
        };

        var unblock = Ui.Btn(L.T("Odblokuj (ponów krok)", "Unblock (retry step)"), () => Decide("unblock"), 200);
        var move = Ui.Btn(L.T("Przesuń do kroku", "Move to step"), () => Decide("move"), 160);
        var scrap = Ui.Btn("SCRAP", () => Decide("scrap"), 110);
        var unlock = Ui.Btn(L.T("Zdejmij znacznik „w pracy”", "Release \"in work\" lock"), () => Decide("unlock"), 220);
        foreach (var b in new[] { unblock, move, scrap, unlock }) b.Enabled = write;

        Content = new TableLayout
        {
            Spacing = new Size(6, 10),
            Rows =
            {
                new TableRow(new TableLayout { Spacing = new Size(10, 4), Rows = { new TableRow(new TableCell(Ui.Lbl(L.T("Numer seryjny (skan + Enter):", "Serial number (scan + Enter):"), 12, true)), new TableCell(_scan, true), new TableCell(Ui.Btn(L.T("Szukaj", "Find"), Find, 100))) } }),
                new TableRow(new TableLayout { Spacing = new Size(10, 4), Rows = { new TableRow(new TableCell(Ui.Lbl(L.T("Gdzie jest teraz:", "Where it is now:"), 12, true)), new TableCell(_where, true)) } }),
                new TableRow(new GroupBox
                {
                    Text = L.T("Decyzja jakości (wymaga komentarza, zapisywana w dzienniku audytu)", "Quality decision (comment required, stored in the audit log)"), Padding = 10,
                    Content = new TableLayout
                    {
                        Spacing = new Size(8, 8),
                        Rows =
                        {
                            new TableRow(new TableLayout { Spacing = new Size(10, 4), Rows = { new TableRow(new TableCell(Ui.Lbl(L.T("Komentarz:", "Comment:"))), new TableCell(_comment, true)) } }),
                            new TableRow(Ui.Row(unblock, Ui.Lbl("   " + L.T("Przesuń do:", "Move to:")), _moveTo, move, Ui.Lbl("   "), scrap, unlock)),
                        }
                    }
                }),
                new TableRow(new Splitter
                {
                    Orientation = Orientation.Horizontal, Position = 760,
                    Panel1 = new GroupBox { Text = L.T("Historia wyników (ostatni na górze)", "Result history (latest on top)"), Padding = 6, Content = _history },
                    Panel2 = new GroupBox { Text = L.T("Wyniki zaznaczonego kroku", "Results of the selected step"), Padding = 6, Content = _details }
                }) { ScaleHeight = true }
            }
        };
    }

    void Find()
    {
        string serial = _scan.Text.TrimEnd('\r', '\n');
        if (serial.Length == 0) return;
        Ui.Try(this, () =>
        {
            _unit = _c.Quality.Find(serial);
            _moveTo.Items.Clear();
            _details.DataStore = new List<KeyValueRow>();
            if (_unit == null)
            {
                _where.Text = L.T("Nie znaleziono numeru seryjnego.", "Serial number not found.");
                _history.DataStore = new List<ResultRow>();
                return;
            }
            var u = _unit;
            var nos = _c.Quality.StepNos(u.OrderId);
            int idx = u.CurrentStepNo == null ? 0 : nos.IndexOf(u.CurrentStepNo.Value) + 1;
            _where.Text = $"{UnitStatus.Name(u.Status)}   •   " + (nos.Count == 0 ? L.T("zlecenie niewystartowane", "order not started") : L.T($"krok {u.CurrentStepNo} ({idx} z {nos.Count})", $"step {u.CurrentStepNo} ({idx} of {nos.Count})")) +
                $"   •   {L.T("zlecenie", "order")} {u.OrderNo} [{OrderStatus.Name(u.OrderStatus)}]   •   BOM {u.BomVersion}" +
                (u.LockActive ? $"   •   {L.T("W PRACY na", "IN WORK at")} {u.LockedHost}" : "");
            bool finished = u.Status is UnitStatus.FinishedOk or UnitStatus.FinishedNok;
            foreach (int no in nos.Where(n => finished ? n <= u.CurrentStepNo : n < u.CurrentStepNo).Reverse())
                _moveTo.Items.Add(new ListItem { Key = no.ToString(), Text = L.T("krok ", "step ") + no });
            if (_moveTo.Items.Count > 0) _moveTo.SelectedIndex = 0;
            var hist = _c.Quality.History(u.Id);
            _history.DataStore = hist;
            if (hist.Count > 0) _history.SelectRow(0);   // tabela z wynikami ostatniego kroku
        }, _c.Log);
        _scan.SelectAll();
    }

    void Decide(string what)
    {
        if (_unit == null) { Ui.Info(this, L.T("Najpierw zeskanuj numer seryjny.", "Scan a serial number first.")); return; }
        var u = _unit;
        string comment = _comment.Text.Trim();
        bool ok = Ui.Try(this, () =>
        {
            switch (what)
            {
                case "unblock": _c.Quality.Unblock(u.Id, comment, _c.UserId); break;
                case "move":
                    int step = (int)(_moveTo.KeyId() ?? throw new MesException("Brak kroku, do którego można cofnąć sztukę.", "There is no step the unit can be moved back to."));
                    _c.Quality.MoveTo(u.Id, step, comment, _c.UserId); break;
                case "scrap":
                    if (string.IsNullOrWhiteSpace(comment)) throw new MesException("Decyzja jakości wymaga komentarza.", "A quality decision requires a comment.");
                    if (!Ui.Confirm(this, L.T($"Zezłomować sztukę {u.SerialNo}? SCRAP jest stanem końcowym i nie można go cofnąć.", $"Scrap unit {u.SerialNo}? SCRAP is a final state and cannot be undone."))) return;
                    _c.Quality.Scrap(u.Id, comment, _c.UserId); break;
                case "unlock": _c.Quality.ReleaseLock(u.Id, _c.UserId); break;
            }
            _c.Log.Info($"Quality decision {what} on {u.SerialNo} by {_c.User.Login}: {comment}");
        }, _c.Log);
        if (ok) { _comment.Text = ""; _scan.Text = u.SerialNo; Find(); }
    }
}

/// <summary>R3.2e/f: eksport zlecenia i numeru seryjnego do CSV (Z36) oraz raport Excel.</summary>
public class ExportPanel : Panel
{
    readonly Ctx _c;
    readonly ComboBox _order = new() { Width = 300 };
    readonly TextBox _serial = new() { Width = 300 };

    public ExportPanel(Ctx c)
    {
        _c = c;
        Ui.Try(this, () => { foreach (var o in _c.Orders.Orders()) _order.Items.Add(o.OrderNo); }, _c.Log);
        Ui.OnEnter(_serial, ExportSerial);
        Content = new TableLayout
        {
            Spacing = new Size(6, 14),
            Rows =
            {
                new TableRow(new GroupBox
                {
                    Text = L.T("Eksport zlecenia – stan każdego kroku każdej sztuki, wartości, czasy, narzędzia, materiały", "Order export – every step of every unit, values, times, tools, materials"), Padding = 12,
                    Content = Ui.Row(Ui.Lbl(L.T("Numer zlecenia:", "Order number:")), _order, Ui.Btn(L.T("Eksport CSV…", "Export CSV…"), ExportOrder, 140), Ui.Btn(L.T("Raport Excel…", "Excel report…"), ExportExcel, 140))
                }),
                new TableRow(new GroupBox
                {
                    Text = L.T("Eksport numeru seryjnego – komplet informacji o jednej sztuce (wyniki + decyzje jakości)", "Serial number export – complete information about one unit (results + quality decisions)"), Padding = 12,
                    Content = Ui.Row(Ui.Lbl(L.T("Numer seryjny:", "Serial number:")), _serial, Ui.Btn(L.T("Eksport CSV…", "Export CSV…"), ExportSerial, 140))
                }),
                new TableRow(Ui.Lbl(L.T("Format CSV: UTF-8 z BOM, separator ; , kropka dziesiętna, jeden wiersz na wynik kroku.", "CSV format: UTF-8 with BOM, separator ; , decimal point, one row per step result."), color: Ui.Gray)),
                null
            }
        };
    }

    void ExportOrder() => Ui.Try(this, () =>
    {
        string no = (_order.Text ?? "").Trim();
        var rows = _c.Reports.OrderRows(no);
        string path = Mx.SaveDialog(this, no + ".csv", "csv");
        if (path.Length == 0) return;
        ReportRepo.WriteCsv(path, rows);
        Ui.Info(this, L.T($"Zapisano {rows.Count} wierszy: {path}", $"{rows.Count} rows saved: {path}"));
    }, _c.Log);

    void ExportExcel() => Ui.Try(this, () =>
    {
        string no = (_order.Text ?? "").Trim();
        var o = _c.Orders.ByNo(no) ?? throw new MesException($"Zlecenie „{no}” nie istnieje.", $"Order \"{no}\" does not exist.");
        string path = Mx.SaveDialog(this, no + ".xlsx", "xlsx");
        if (path.Length == 0) return;
        _c.Reports.WriteExcel(path, o);
        Ui.Info(this, L.T("Zapisano raport: ", "Report saved: ") + path);
    }, _c.Log);

    void ExportSerial() => Ui.Try(this, () =>
    {
        string sn = _serial.Text.TrimEnd('\r', '\n');
        var rows = _c.Reports.SerialRows(sn);
        string path = Mx.SaveDialog(this, SerialNumber.FileSafe(sn) + ".csv", "csv");
        if (path.Length == 0) return;
        ReportRepo.WriteCsv(path, rows, _c.Reports.SerialAudit(sn));
        Ui.Info(this, L.T($"Zapisano: {path}", $"Saved: {path}"));
    }, _c.Log);
}
