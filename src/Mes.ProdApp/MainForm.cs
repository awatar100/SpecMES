using System.Diagnostics;
using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Data;
using Mes.Shared.UI;

namespace Mes.ProdApp;

/// <summary>
/// Stanowisko produkcyjne. Obsluga wylacznie klawiatura i czytnikiem kodow:
/// CR/TAB = nastepne pole, F1 = pomiar automatyczny, F5 = OK, F8 = NOK, Esc = anuluj sztuke.
/// Formularz kroku budowany dynamicznie z konfiguracji kroku w migawce marszruty zlecenia.
/// </summary>
public class MainForm : Form
{
    readonly Db _db;
    readonly ProductionRepo _repo;
    readonly AuthRepo _auth;
    readonly OfflineBuffer _buffer;
    readonly FileLogger _log;
    readonly string _host = Environment.MachineName;

    LoginResult? _user;
    long _stationId;
    UnitContext? _ctx;
    DateTime _startedUtc;
    bool _online = true, _busy, _checking;
    (string Text, Color Color) _msg = ("", Ui.Gray);
    readonly Dictionary<string, DateTime> _lastKey = new();

    // powloka
    TextBox _scan = new();
    Label _status = new(), _message = new(), _progressLbl = new();
    ProgressBar _progress = new();
    Panel _stepPanel = new();
    readonly UITimer _timer = new() { Interval = 30 };
    readonly UITimer _measTimer = new() { Interval = 1 };

    // kontrolki biezacego kroku
    readonly List<(StepMaterial M, TextBox Qty, TextBox Batch)> _mats = new();
    readonly List<Label> _slotLbl = new();
    TextBox? _consumes, _toolSn, _value, _comment;
    Button? _okBtn, _nokBtn, _measBtn;
    Label? _countdown;

    // stan pomiaru automatycznego
    Process? _proc;
    int _measLeft;
    DateTime _measStartUtc;

    public MainForm(Db db, FileLogger log)
    {
        _db = db; _log = log;
        _repo = new ProductionRepo(db);
        _auth = new AuthRepo(db);
        _buffer = new OfflineBuffer(Path.Combine(Ui.AppDir, "buffer"));
        ClientSize = new Size(1100, 760);
        MinimumSize = new Size(800, 560);
        L.Changed += () => Application.Instance.AsyncInvoke(() => { BuildShell(); if (_ctx != null) BuildStep(); });
        KeyDown += OnFormKey;
        _timer.Elapsed += (_, _) => CheckConnection();
        _measTimer.Elapsed += (_, _) => MeasureTick();
        Shown += (_, _) => ShowLogin();
        Closing += (_, _) => { KillProc(); ReleaseQuietly(); };
        BuildShell();
    }

    // ------------------------------------------------------------------ logowanie

    void ShowLogin()
    {
        _timer.Stop();
        _user = null;
        BuildShell();
        while (true)
        {
            var r = new LoginDialog(_auth, "SpecAPP", Permissions.CanUseProdApp).ShowModal(this);
            if (r == null) { Application.Instance.Quit(); return; }
            try
            {
                _stationId = _repo.RegisterStation(_host);
                _user = r;
                break;
            }
            catch (Exception ex) { _log.Error("Station registration failed", ex); Ui.Error(this, Ui.Describe(ex)); }
        }
        _log.Info($"Login: {_user.Login} ({_user.Role}) at {_host}");
        _online = true;
        SetMessage(L.T("Zeskanuj numer seryjny sztuki.", "Scan the unit serial number."), Ui.Gray);
        BuildShell();
        _timer.Start();
        CheckConnection();
        FocusScan();
    }

    void Logout()
    {
        if (_busy) return;
        if (_ctx != null) CancelUnit();
        _log.Info($"Logout: {_user?.Login}");
        ShowLogin();
    }

    // ------------------------------------------------------------------ powloka okna

    void BuildShell()
    {
        Ui.Brand(this); Title = "SpecAPP – " + L.T("stanowisko produkcyjne", "production station");
        var cmdMeas = new Command { MenuText = L.T("Pomiar automatyczny", "Automatic measurement"), Shortcut = Keys.F1 };
        cmdMeas.Executed += (_, _) => Hot("F1", StartMeasure);
        var cmdOk = new Command { MenuText = "OK", Shortcut = Keys.F5 };
        cmdOk.Executed += (_, _) => Hot("F5", () => Decide(true));
        var cmdNok = new Command { MenuText = "NOK", Shortcut = Keys.F8 };
        cmdNok.Executed += (_, _) => Hot("F8", () => Decide(false));
        var cmdCancel = new Command { MenuText = L.T("Anuluj sztukę (Esc)", "Cancel unit (Esc)") };
        cmdCancel.Executed += (_, _) => CancelUnit();
        var cmdLogout = new Command { MenuText = L.T("Wyloguj", "Log out") };
        cmdLogout.Executed += (_, _) => Logout();
        var cmdLang = new Command { MenuText = L.English ? "Polski" : "English" };
        cmdLang.Executed += (_, _) => Ui.ToggleLanguage();
        Menu = new MenuBar
        {
            IncludeSystemItems = MenuBarSystemItems.None,
            Items =
            {
                new SubMenuItem { Text = L.T("&Akcje", "&Actions"), Items = { cmdMeas, cmdOk, cmdNok, cmdCancel } },
                new SubMenuItem { Text = L.T("&Sesja", "&Session"), Items = { cmdLogout, cmdLang } },
            }
        };

        _scan = new TextBox { Font = Fonts.Sans(18), PlaceholderText = L.T("numer seryjny + Enter", "serial number + Enter") };
        Ui.OnEnter(_scan, OnScan);
        _status = Ui.Lbl("", 10, true);
        _message = Ui.Lbl(_msg.Text, 16, true, _msg.Color);
        _message.Wrap = WrapMode.Word;
        _progressLbl = Ui.Lbl("", 13, true);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, Height = 22 };
        _stepPanel = new Panel();

        string who = _user == null ? "—" : $"{_user.Login}" + (string.IsNullOrEmpty(_user.FullName) ? "" : $" ({_user.FullName})");
        var top = new TableLayout
        {
            Spacing = new Size(12, 4),
            Rows = { new TableRow(
                new TableCell(Ui.Lbl("SpecAPP", 14, true)),
                new TableCell(Ui.Lbl($"{L.T("Użytkownik", "User")}: {who}    {L.T("Stanowisko", "Station")}: {_host}"), true),
                new TableCell(_status),
                new TableCell(Ui.Btn(L.T("Sprawdź połączenie", "Check connection"), CheckConnection)),
                new TableCell(Ui.Btn(L.T("Wyloguj", "Log out"), Logout)),
                new TableCell(Ui.AboutButton(this, "SpecAPP"))) }
        };
        var scanRow = new TableLayout
        {
            Spacing = new Size(10, 4),
            Rows = { new TableRow(new TableCell(Ui.Lbl(L.T("Numer seryjny:", "Serial number:"), 14, true)), new TableCell(_scan, true)) }
        };
        var progRow = new TableLayout
        {
            Spacing = new Size(10, 4),
            Rows = { new TableRow(new TableCell(_progressLbl), new TableCell(_progress, true)) }
        };
        Content = new TableLayout
        {
            Padding = 12, Spacing = new Size(6, 10),
            Rows =
            {
                new TableRow(top), new TableRow(scanRow), new TableRow(_message), new TableRow(progRow),
                new TableRow(new Scrollable { Content = _stepPanel, Border = BorderType.Line, ExpandContentWidth = true, ExpandContentHeight = false }) { ScaleHeight = true },
                new TableRow(Ui.Lbl(L.T("Enter / TAB = następne pole     F1 = pomiar automatyczny     F5 = OK     F8 = NOK     Esc = anuluj sztukę",
                    "Enter / TAB = next field     F1 = automatic measurement     F5 = OK     F8 = NOK     Esc = cancel unit"), color: Ui.Gray)),
            }
        };
        UpdateState();
    }

    void SetMessage(string text, Color color)
    {
        _msg = (text, color);
        _message.Text = text;
        _message.TextColor = color;
    }

    void UpdateState()
    {
        int buffered = _buffer.Count;
        if (!_online)
        { _status.Text = L.T("● BRAK POŁĄCZENIA – ponawianie co 30 s", "● NO CONNECTION – retrying every 30 s"); _status.TextColor = Ui.Red; }
        else if (buffered > 0)
        { _status.Text = L.T($"● Bufor: {buffered} – wysyłanie", $"● Buffer: {buffered} – sending"); _status.TextColor = Ui.Amber; }
        else { _status.Text = L.T("● Połączono", "● Connected"); _status.TextColor = Ui.Green; }
        _scan.Enabled = _user != null && _ctx == null && _online && buffered == 0 && !_busy;
    }

    void FocusScan()
    {
        UpdateState();
        _scan.Text = "";
        if (_scan.Enabled) Application.Instance.AsyncInvoke(() => _scan.Focus());
    }

    // ------------------------------------------------------------------ polaczenie i bufor (R2.5, R2.23, Z30)

    void CheckConnection()
    {
        if (_checking || _busy || _user == null) return;
        _checking = true;
        Task.Run(() =>
        {
            bool ok = false; int rejected = 0;
            try
            {
                ok = _db.Ping();
                if (ok && _buffer.Count > 0)
                    _buffer.Flush(_repo, _log, (rec, ex) =>
                    {
                        rejected++;
                        _repo.LogEvent(rec.UserId, _stationId, "APP_ERROR", new { what = "buffered result rejected", rec.SerialNo, rec.StepNo, rec.ClientGuid, error = ex.Message });
                    });
            }
            catch (Exception ex) { _log.Error("Connection check failed", ex); }
            Application.Instance.AsyncInvoke(() =>
            {
                _checking = false;
                bool was = _online;
                _online = ok;
                if (!was && ok) _log.Info("Connection restored");
                if (was && !ok) _log.Warn("Connection lost");
                if (rejected > 0)
                    SetMessage(L.T($"Baza odrzuciła {rejected} buforowany wynik (konflikt stanu sztuki) – szczegóły w logu, plik w buffer/failed. Zgłoś do działu jakości.",
                        $"The database rejected {rejected} buffered result (unit state conflict) – details in the log, file in buffer/failed. Report to quality."), Ui.Red);
                else if (!was && ok && _ctx == null && _buffer.Count == 0)
                    SetMessage(L.T("Połączenie przywrócone. Zeskanuj numer seryjny.", "Connection restored. Scan a serial number."), Ui.Green);
                else if (!ok && _ctx == null)
                    SetMessage(L.T("Brak połączenia z bazą danych – praca zablokowana. Ponawianie co 30 s.", "No database connection – work is blocked. Retrying every 30 s."), Ui.Red);
                bool hadFocus = _scan.HasFocus;
                UpdateState();
                if (_ctx == null && _scan.Enabled && (!was || !hadFocus)) _scan.Focus();
            });
        });
    }

    // ------------------------------------------------------------------ skan numeru

    void OnScan()
    {
        // Z5: usuwane wylacznie koncowe CR/LF, spacje sa czescia numeru
        string serial = _scan.Text.TrimEnd('\r', '\n');
        if (serial.Length == 0 || _user == null || _ctx != null || _busy) return;
        if (_buffer.Count > 0)
        {
            SetMessage(L.T("Bufor lokalny nie został jeszcze wysłany do bazy – kolejna operacja jest zablokowana.", "The local buffer has not been sent to the database yet – the next operation is blocked."), Ui.Amber);
            CheckConnection();
            return;
        }
        LoadUnit(serial, false);
    }

    void LoadUnit(string serial, bool keepMessage)
    {
        try
        {
            _ctx = _repo.OpenUnit(serial, _stationId);
            _startedUtc = DateTime.UtcNow;
            _log.Info($"Unit opened: {serial} step {_ctx.Step.StepNo} ({_ctx.StepIndex}/{_ctx.StepCount}) order {_ctx.Unit.OrderNo}");
            if (!keepMessage) SetMessage("", Ui.Gray);
            _scan.Text = serial;
            BuildStep();
        }
        catch (Exception ex) when (Db.IsConnectionError(ex))
        {
            _log.Error("Scan failed – no connection", ex);
            _online = false; _ctx = null;
            SetMessage(L.T("Brak połączenia z bazą danych – nie można sprawdzić stanu sztuki. Praca zablokowana, ponawianie co 30 s.",
                "No database connection – cannot check the unit state. Work is blocked, retrying every 30 s."), Ui.Red);
            ClearStepPanel();
            UpdateState();
        }
        catch (Exception ex)
        {
            if (ex is MesException m) _log.Warn($"Scan rejected: {serial} [{m.Code}] {m.En}"); else _log.Error("Scan failed: " + serial, ex);
            _ctx = null;
            SetMessage(Ui.Describe(ex), Ui.Red);
            ClearStepPanel();
            UpdateState();
            _scan.Text = serial;
            Application.Instance.AsyncInvoke(() => { _scan.Focus(); _scan.SelectAll(); });
        }
    }

    void ClearStepPanel()
    {
        _mats.Clear(); _slotLbl.Clear();
        _consumes = _toolSn = _value = _comment = null;
        _okBtn = _nokBtn = _measBtn = null; _countdown = null;
        _stepPanel.Content = null;
        _progressLbl.Text = "";
        _progress.Value = 0;
    }

    void ReleaseQuietly()
    {
        if (_ctx == null) return;
        try { _repo.ReleaseUnit(_ctx.Unit.Id, _stationId); } catch (Exception ex) { _log.Warn("Release failed: " + ex.Message); }
    }

    void CancelUnit()
    {
        if (_ctx == null || _busy) return;
        _log.Info($"Unit cancelled by operator: {_ctx.Unit.SerialNo}");
        ReleaseQuietly();
        _ctx = null;
        ClearStepPanel();
        SetMessage(L.T("Anulowano. Zeskanuj numer seryjny.", "Cancelled. Scan a serial number."), Ui.Gray);
        FocusScan();
    }

    // ------------------------------------------------------------------ dynamiczny formularz kroku

    void BuildStep()
    {
        if (_ctx == null) return;
        var s = _ctx.Step;
        ClearStepPanel();
        _progressLbl.Text = L.T($"Krok {_ctx.StepIndex} z {_ctx.StepCount}  (nr {s.StepNo})", $"Step {_ctx.StepIndex} of {_ctx.StepCount}  (no. {s.StepNo})");
        _progress.MaxValue = _ctx.StepCount;
        _progress.Value = _ctx.StepIndex;

        var rows = new List<TableRow>();
        var inputs = new List<TextBox>();
        var big = Fonts.Sans(13);

        rows.Add(new TableRow(Ui.Lbl($"{s.StepNo} – {s.Title}", 18, true)));
        rows.Add(new TableRow(Ui.Lbl($"{L.T("Zlecenie", "Order")} {_ctx.Unit.OrderNo}   BOM {_ctx.Unit.BomVersion}   •   {s.Kind}" +
            (s.IsRepeatable ? "   •   " + L.T("krok powtarzalny", "repeatable step") : "") + (s.IsFinal ? "   •   " + L.T("KROK FINALNY", "FINAL STEP") : ""), color: Ui.Gray)));
        if (!string.IsNullOrWhiteSpace(s.Body))
        {
            var body = Ui.Lbl(s.Body, 13);
            body.Wrap = WrapMode.Word;
            rows.Add(new TableRow(body));
        }
        if (!string.IsNullOrWhiteSpace(s.WorkInstr))
            rows.Add(new TableRow(Ui.Row(Ui.Btn(L.T("Instrukcja stanowiskowa…", "Work instruction…"), OpenInstruction), Ui.Lbl(s.WorkInstr, color: Ui.Gray))));

        // materialy (R3.2c Material declaration): tylko zadeklarowane pola
        if (s.Materials.Count > 0)
        {
            var t = new TableLayout { Spacing = new Size(10, 6) };
            t.Rows.Add(new TableRow(Ui.Lbl(L.T("Materiał", "Material"), bold: true), Ui.Lbl(L.T("Deklarowane", "Declared"), bold: true),
                Ui.Lbl(L.T("Ilość użyta", "Quantity used"), bold: true), Ui.Lbl(L.T("Numer partii (batch)", "Batch number"), bold: true), null));
            foreach (var m in s.Materials)
            {
                var qty = new TextBox { Text = Ui.Dec(m.Qty), Width = 110, Font = big };
                var batch = new TextBox { Width = 320, Font = big, MaxLength = 100, PlaceholderText = string.IsNullOrEmpty(m.BatchPattern) ? "" : m.BatchPattern };
                _mats.Add((m, qty, batch));
                inputs.Add(qty); inputs.Add(batch);
                t.Rows.Add(new TableRow(Ui.Lbl($"{m.SlotNo}. {m.MaterialName}", 13), Ui.Lbl($"{Ui.Dec(m.Qty)} {m.MaterialUnit}", 13), qty, batch, null));
            }
            rows.Add(new TableRow(new GroupBox { Text = L.T("Materiały", "Materials"), Padding = 10, Content = t }));
        }

        if (s.Consumes)
        {
            _consumes = new TextBox { Font = big, MaxLength = 200, Width = 420 };
            inputs.Add(_consumes);
            rows.Add(new TableRow(new GroupBox
            {
                Text = "Consumes", Padding = 10,
                Content = Ui.Row(Ui.Lbl(L.T("Numer części składowej (np. PCB):", "Component number (e.g. PCB):"), 13), _consumes)
            }));
        }

        if (s.ManualMeas)
        {
            _toolSn = new TextBox { Font = big, MaxLength = 100, Width = 300 };
            _value = new TextBox { Font = big, Width = 160 };
            inputs.Add(_toolSn); inputs.Add(_value);
            rows.Add(new TableRow(new GroupBox
            {
                Text = L.T("Pomiar ręczny", "Manual measurement"), Padding = 10,
                Content = new TableLayout
                {
                    Spacing = new Size(10, 6),
                    Rows =
                    {
                        new TableRow(Ui.Lbl(L.T("Numer miernika (skan):", "Meter number (scan):"), 13), _toolSn, null),
                        new TableRow(Ui.Lbl(L.T("Wartość zmierzona:", "Measured value:"), 13), _value,
                            Ui.Lbl($"LSL {Ui.Dec(s.Lsl)}  …  USL {Ui.Dec(s.Usl)}  {s.Unit}", 13, true, Ui.Blue)),
                    }
                }
            }));
        }

        if (s.AutoMeas)
        {
            _measBtn = new Button { Text = L.T("POMIAR  (F1)", "MEASURE  (F1)"), Font = Fonts.Sans(15, FontStyle.Bold), Height = 52, Width = 260 };
            _measBtn.Click += (_, _) => StartMeasure();
            _countdown = Ui.Lbl("", 14, true, Ui.Blue);
            var t = new TableLayout { Spacing = new Size(14, 6) };
            t.Rows.Add(new TableRow(Ui.Lbl("#", bold: true), Ui.Lbl(L.T("Wymaganie", "Requirement"), bold: true), Ui.Lbl(L.T("Wynik", "Result"), bold: true), null));
            foreach (var sl in s.Slots)
            {
                var res = Ui.Lbl("—", 14, true);
                _slotLbl.Add(res);
                string req = sl.Kind == "TEXT" ? $"= \"{sl.ExpectedText}\"" : $"{Ui.Dec(sl.Lsl)} … {Ui.Dec(sl.Usl)} {sl.Unit}";
                t.Rows.Add(new TableRow(Ui.Lbl(sl.SlotNo.ToString(), 13), Ui.Lbl(req, 13), res, null));
            }
            rows.Add(new TableRow(new GroupBox
            {
                Text = L.T("Pomiar automatyczny", "Automatic measurement") + $"  –  {L.T("narzędzie", "tool")} {s.ToolSn},  timeout {s.TimeoutS} s",
                Padding = 10,
                Content = new TableLayout { Spacing = new Size(6, 10), Rows = { new TableRow(Ui.Row(_measBtn, _countdown)), new TableRow(t) } }
            }));
        }

        _comment = new TextBox { Font = big, MaxLength = 500 };
        rows.Add(new TableRow(new TableLayout
        {
            Spacing = new Size(10, 4),
            Rows = { new TableRow(new TableCell(Ui.Lbl(L.T("Komentarz (wymagany przy NOK):", "Comment (required for NOK):"), 13)), new TableCell(_comment, true)) }
        }));

        if (!s.AutoMeas)
        {
            _okBtn = new Button { Text = "OK  (F5)", Font = Fonts.Sans(16, FontStyle.Bold), Height = 56, Width = 220, BackgroundColor = Ui.Green, TextColor = Colors.White };
            _okBtn.Click += (_, _) => Decide(true);
            _nokBtn = new Button { Text = "NOK  (F8)", Font = Fonts.Sans(16, FontStyle.Bold), Height = 56, Width = 220, BackgroundColor = Ui.Red, TextColor = Colors.White };
            _nokBtn.Click += (_, _) => Decide(false);
            rows.Add(new TableRow(Ui.Row(_okBtn, _nokBtn, Ui.Btn(L.T("Anuluj (Esc)", "Cancel (Esc)"), CancelUnit, 140))));
        }
        else
            rows.Add(new TableRow(Ui.Row(Ui.Btn(L.T("Anuluj (Esc)", "Cancel (Esc)"), CancelUnit, 140),
                Ui.Lbl(L.T("Wynik OK/NOK zostanie wyliczony i zapisany automatycznie po pomiarze.", "The OK/NOK result is computed and saved automatically after the measurement."), color: Ui.Gray))));

        // CR (klawiatura / czytnik) = nastepne pole; ostatnie pole -> komentarz
        inputs.Add(_comment);
        for (int i = 0; i < inputs.Count - 1; i++)
        {
            var cur = inputs[i]; var next = inputs[i + 1];
            Ui.OnEnter(cur, () =>
            {
                if (cur == _toolSn && !CheckToolField()) return;
                next.Focus(); next.SelectAll();
            });
        }
        Ui.OnEnter(_comment, () => { });

        var layout = new TableLayout { Padding = 14, Spacing = new Size(6, 12) };
        foreach (var r in rows) layout.Rows.Add(r);
        layout.Rows.Add(new TableRow { ScaleHeight = true });
        _stepPanel.Content = layout;
        UpdateState();
        Application.Instance.AsyncInvoke(() =>
        {
            if (s.AutoMeas) _measBtn?.Focus();
            else { inputs[0].Focus(); inputs[0].SelectAll(); }
        });
    }

    void OpenInstruction()
    {
        try { Application.Instance.Open(_ctx!.Step.WorkInstr); }
        catch (Exception ex)
        {
            _log.Error("Cannot open work instruction " + _ctx?.Step.WorkInstr, ex);
            SetMessage(L.T("Nie można otworzyć instrukcji: ", "Cannot open the instruction: ") + _ctx?.Step.WorkInstr, Ui.Red);
        }
    }

    /// <summary>Z18: numer miernika musi byc rowny narzedziu z marszruty i wazny.</summary>
    bool CheckToolField()
    {
        if (_ctx == null || _toolSn == null) return false;
        string sn = _toolSn.Text.TrimEnd('\r', '\n');
        string? problem = null;
        if (sn != _ctx.Step.ToolSn)
            problem = L.T($"Miernik „{sn}” nie jest narzędziem przypisanym do tego kroku (wymagany: {_ctx.Step.ToolSn}).", $"Meter \"{sn}\" is not the tool assigned to this step (required: {_ctx.Step.ToolSn}).");
        else
        {
            try
            {
                if (!_repo.CheckTool(sn).Valid)
                    problem = L.T($"Miernik „{sn}” jest NIEWAŻNY (nieaktywny lub poza datami ważności). Pomiar niedozwolony.", $"Meter \"{sn}\" is INVALID (inactive or outside validity dates). Measurement not allowed.");
            }
            catch (Exception ex)
            {
                _log.Error("Tool check failed", ex);
                problem = L.T("Nie można sprawdzić ważności miernika: ", "Cannot verify the meter validity: ") + Ui.Describe(ex);
            }
        }
        if (problem == null) { SetMessage("", Ui.Gray); return true; }
        _log.Warn($"Tool rejected: '{sn}' for {_ctx.Unit.SerialNo} step {_ctx.Step.StepNo}");
        SetMessage(problem, Ui.Red);
        _toolSn.Focus(); _toolSn.SelectAll();
        return false;
    }

    // ------------------------------------------------------------------ klawisze funkcyjne

    /// <summary>Ochrona przed podwojnym wywolaniem (skrot menu + zdarzenie klawisza).</summary>
    void Hot(string key, Action a)
    {
        var now = DateTime.UtcNow;
        if (_lastKey.TryGetValue(key, out var t) && (now - t).TotalMilliseconds < 400) return;
        _lastKey[key] = now;
        a();
    }

    void OnFormKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Keys.F1: e.Handled = true; Hot("F1", StartMeasure); break;
            case Keys.F5: e.Handled = true; Hot("F5", () => Decide(true)); break;
            case Keys.F8: e.Handled = true; Hot("F8", () => Decide(false)); break;
            case Keys.Escape: e.Handled = true; Hot("Esc", CancelUnit); break;
        }
    }

    // ------------------------------------------------------------------ decyzja operatora OK / NOK (R2.17)

    StepResultRecord NewRecord(string result) => new()
    {
        UnitId = _ctx!.Unit.Id, SerialNo = _ctx.Unit.SerialNo, UnitVersion = _ctx.Unit.Version, StepNo = _ctx.Step.StepNo,
        Result = result, Comment = (_comment?.Text ?? "").Trim(), UserId = _user!.UserId!.Value,
        DurationS = Math.Max(0, (DateTime.UtcNow - _startedUtc).TotalSeconds)
    };

    void Decide(bool ok)
    {
        if (_ctx == null || _busy || _user == null) return;
        if (_ctx.Step.AutoMeas)
        {
            SetMessage(L.T("Krok automatyczny: wynik wylicza system. Naciśnij F1 (POMIAR).", "Automatic step: the result is computed by the system. Press F1 (MEASURE)."), Ui.Amber);
            return;
        }
        var rec = NewRecord(ok ? "OK" : "NOK");
        var (error, focus) = Collect(rec, ok);
        if (error == null && !ok && rec.Comment.Length == 0)
            (error, focus) = (L.T("Wynik NOK wymaga komentarza z przyczyną.", "A NOK result requires a comment with the reason."), _comment);
        if (error != null)
        {
            SetMessage(error, Ui.Red);
            if (focus is TextBox tb) { tb.Focus(); tb.SelectAll(); }
            return;
        }
        Save(rec, "");
    }

    /// <summary>Zebranie i walidacja pol. Przy OK wszystkie pola wymagane; przy NOK zapisywane jest to, co poprawnie wypelniono.</summary>
    (string? Error, Control? Focus) Collect(StepResultRecord rec, bool strict)
    {
        var s = _ctx!.Step;
        foreach (var (m, qtyBox, batchBox) in _mats)
        {
            var qty = Ui.Dec(qtyBox.Text);
            string batch = batchBox.Text.TrimEnd('\r', '\n');
            bool qtyOk = qty is > 0;
            bool batchOk = batch.Length > 0 && TextRules.IsValid(batch) && BatchPattern.Matches(m.BatchPattern, batch);
            if (strict)
            {
                if (!qtyOk) return (L.T($"Materiał {m.SlotNo} ({m.MaterialName}): wpisz użytą ilość (liczba > 0).", $"Material {m.SlotNo} ({m.MaterialName}): enter the quantity used (number > 0)."), qtyBox);
                if (batch.Length == 0) return (L.T($"Materiał {m.SlotNo} ({m.MaterialName}): zeskanuj numer partii.", $"Material {m.SlotNo} ({m.MaterialName}): scan the batch number."), batchBox);
                if (!TextRules.IsValid(batch)) return (L.T($"Materiał {m.SlotNo}: numer partii zawiera niedozwolone znaki ({TextRules.Forbidden}).", $"Material {m.SlotNo}: the batch number contains forbidden characters ({TextRules.Forbidden})."), batchBox);
                if (!batchOk) return (L.T($"Materiał {m.SlotNo} ({m.MaterialName}): numer partii nie pasuje do wzorca {m.BatchPattern}.", $"Material {m.SlotNo} ({m.MaterialName}): the batch number does not match pattern {m.BatchPattern}."), batchBox);
            }
            if (qtyOk && batchOk)
                rec.Materials.Add(new MaterialUse { SlotNo = m.SlotNo, MaterialId = m.MaterialId, Qty = qty!.Value, BatchNo = batch });
        }

        if (s.Consumes && _consumes != null)
        {
            string part = _consumes.Text.TrimEnd('\r', '\n');
            bool partOk = part.Length > 0 && TextRules.IsValid(part);
            if (strict && !partOk)
                return (L.T("Consumes: zeskanuj numer części składowej (bez znaków " + TextRules.Forbidden + ").", "Consumes: scan the component number (without characters " + TextRules.Forbidden + ")."), _consumes);
            if (partOk) rec.ConsumedPart = part;
        }

        if (s.ManualMeas && _toolSn != null && _value != null)
        {
            string tool = _toolSn.Text.TrimEnd('\r', '\n');
            bool isNum = Evaluator.TryNumManual(_value.Text, out var v);
            bool inLimits = isNum && Evaluator.InLimits(v, s.Lsl, s.Usl);
            if (strict)
            {
                if (!CheckToolField()) return (_msg.Text, _toolSn);
                if (!isNum) return (L.T("Pomiar ręczny: wpisz wartość liczbową.", "Manual measurement: enter a numeric value."), _value);
                if (!inLimits)
                    return (L.T($"Wartość {Ui.Dec(v)} jest poza granicami {Ui.Dec(s.Lsl)} … {Ui.Dec(s.Usl)} {s.Unit}. Wynik OK jest niemożliwy – popraw pomiar albo zatwierdź NOK (F8).",
                        $"Value {Ui.Dec(v)} is outside limits {Ui.Dec(s.Lsl)} … {Ui.Dec(s.Usl)} {s.Unit}. OK is not possible – correct the measurement or confirm NOK (F8)."), _value);
            }
            if (tool.Length > 0 && TextRules.IsValid(tool)) rec.ToolSn = tool;
            if (isNum)
            {
                rec.Values.Add(new ResultValue { SlotNo = 1, RawValue = Ui.Dec(v), NumValue = v, InLimits = inLimits });
                if (!inLimits) rec.ErrorCode = ErrorCodes.OutOfLimits;
            }
        }
        return (null, null);
    }

    // ------------------------------------------------------------------ zapis wyniku / bufor

    void Save(StepResultRecord rec, string details)
    {
        try
        {
            var o = _repo.SaveResult(rec, _stationId);
            _log.Info($"Result saved: {rec.SerialNo} step {rec.StepNo} {o.Result} {o.ErrorCode} -> {o.UnitStatus} next {o.NextStepNo}");
            AfterSaved(rec, o, details);
        }
        catch (Exception ex) when (Db.IsConnectionError(ex))
        {
            // utrata komunikacji w trakcie pracy: wynik do pliku lokalnego, praca zablokowana do wyslania
            _log.Error($"Save failed – no connection, buffering: {rec.SerialNo} step {rec.StepNo} {rec.Result} guid {rec.ClientGuid}", ex);
            try
            {
                _buffer.Add(rec, _stationId);
                _online = false; _ctx = null;
                ClearStepPanel();
                SetMessage(L.T($"Brak połączenia – wynik {rec.Result} sztuki {rec.SerialNo} zapisano w buforze lokalnym. Kolejna operacja będzie możliwa po wysłaniu bufora (ponawianie co 30 s).",
                    $"No connection – result {rec.Result} of unit {rec.SerialNo} stored in the local buffer. The next operation is possible after the buffer is sent (retrying every 30 s).") + details, Ui.Amber);
                FocusScan();
            }
            catch (Exception ex2)
            {
                _log.Error("BUFFER WRITE FAILED – result not stored", ex2);
                SetMessage(L.T("BŁĄD KRYTYCZNY: nie można zapisać wyniku ani do bazy, ani do bufora. Nie odkładaj sztuki – ponów F5/F8.",
                    "CRITICAL ERROR: cannot store the result in the database or the buffer. Keep the unit – retry F5/F8."), Ui.Red);
            }
        }
        catch (Exception ex)
        {
            if (ex is MesException m) _log.Warn($"Save rejected: {rec.SerialNo} step {rec.StepNo} [{m.Code}] {m.En}");
            else
            {
                _log.Error($"Save failed: {rec.SerialNo} step {rec.StepNo}", ex);
                _repo.LogEvent(rec.UserId, _stationId, "APP_ERROR", new { what = "save failed", rec.SerialNo, rec.StepNo, error = ex.Message });
            }
            ReleaseQuietly();
            _ctx = null;
            ClearStepPanel();
            SetMessage(Ui.Describe(ex), Ui.Red);
            FocusScan();
        }
    }

    void AfterSaved(StepResultRecord rec, SaveOutcome o, string details)
    {
        string sn = rec.SerialNo;
        bool repeat = o.Result == "NOK" && o.UnitStatus == UnitStatus.InProgress;
        _ctx = null;
        ClearStepPanel();
        string msg; Color col;
        if (o.Result == "OK")
        {
            col = Ui.Green;
            msg = o.UnitStatus == UnitStatus.FinishedOk
                ? L.T($"OK – sztuka {sn} ZAKOŃCZYŁA produkcję (krok finalny).", $"OK – unit {sn} has FINISHED production (final step).")
                : L.T($"OK – sztuka {sn} przeszła do kroku {o.NextStepNo}.", $"OK – unit {sn} moved to step {o.NextStepNo}.");
        }
        else
        {
            col = Ui.Red;
            string why = string.IsNullOrEmpty(o.ErrorCode) ? "" : " " + ErrorCodes.Name(o.ErrorCode) + ".";
            msg = $"NOK – {sn}." + why + " " + o.UnitStatus switch
            {
                UnitStatus.Blocked => L.T("Sztuka ZABLOKOWANA – wymagana decyzja działu jakości.", "Unit BLOCKED – a quality department decision is required."),
                UnitStatus.FinishedNok => L.T("Sztuka zakończona jako NOK (krok finalny).", "Unit finished as NOK (final step)."),
                _ => L.T("Krok powtarzalny – ponów operację.", "Repeatable step – repeat the operation.")
            };
        }
        SetMessage(msg + details, col);
        if (repeat) LoadUnit(sn, true);   // krok powtarzalny: od razu ponowienie na tej samej sztuce
        else FocusScan();
    }

    // ------------------------------------------------------------------ pomiar automatyczny (R3.1d, Z19–Z23)

    void StartMeasure()
    {
        if (_ctx == null || !_ctx.Step.AutoMeas || _busy) return;
        var s = _ctx.Step;
        _busy = true;
        if (_measBtn != null) _measBtn.Enabled = false;
        foreach (var l in _slotLbl) { l.Text = "—"; l.TextColor = Ui.Gray; }
        UpdateState();
        _measStartUtc = DateTime.UtcNow;
        _measLeft = Math.Max(1, s.TimeoutS);
        try
        {
            var psi = new ProcessStartInfo(s.ExePath)
            {
                WorkingDirectory = Path.GetDirectoryName(s.ExePath) ?? "", UseShellExecute = false, CreateNoWindow = true
            };
            psi.ArgumentList.Add("-" + _ctx.Unit.SerialNo);   // Z20: jeden argument, poprawne cudzyslowy na kazdej platformie
            _proc = Process.Start(psi);
            _log.Info($"Measurement started: {s.ExePath} -\"{_ctx.Unit.SerialNo}\" timeout {s.TimeoutS}s");
        }
        catch (Exception ex)
        {
            _log.Error("Cannot start measurement program " + s.ExePath, ex);
            FinishMeasure(ErrorCodes.ProcessError, null);
            return;
        }
        if (_countdown != null) _countdown.Text = L.T($"Pomiar trwa… {_measLeft} s", $"Measuring… {_measLeft} s");
        _measTimer.Start();
    }

    /// <summary>R2.8: czekamy pelny timeout i dopiero wtedy sprawdzamy plik.</summary>
    void MeasureTick()
    {
        if (!_busy) { _measTimer.Stop(); return; }
        _measLeft--;
        if (_measLeft > 0)
        {
            if (_countdown != null) _countdown.Text = L.T($"Pomiar trwa… {_measLeft} s", $"Measuring… {_measLeft} s");
            return;
        }
        _measTimer.Stop();
        KillProc();   // Z22: proces konczony niezaleznie od wyniku
        if (_ctx == null) { _busy = false; return; }
        var s = _ctx.Step;
        string csv = Path.Combine(Path.GetDirectoryName(s.ExePath) ?? "", "current.csv");
        try
        {
            if (!File.Exists(csv)) { FinishMeasure(ErrorCodes.MeasTimeout, null); return; }
            var r = CsvMeasurement.ParseFile(csv, _ctx.Unit.SerialNo, s.Slots.Count, _measStartUtc);
            FinishMeasure(r.Error, r);
        }
        catch (Exception ex)
        {
            _log.Error("Cannot read " + csv, ex);
            FinishMeasure(ErrorCodes.MeasFormat, null);
        }
    }

    void KillProc()
    {
        try { if (_proc is { HasExited: false }) { _proc.Kill(true); _log.Warn("Measurement process killed after timeout"); } }
        catch (Exception ex) { _log.Warn("Kill failed: " + ex.Message); }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
    }

    void FinishMeasure(string? error, CsvResult? csv)
    {
        var s = _ctx!.Step;
        var rec = NewRecord("NOK");
        rec.ErrorCode = error;
        string details = "";
        if (csv != null && error == null)
        {
            rec.ToolSn = csv.ToolSn;
            rec.Values = Evaluator.EvaluateSlots(s.Slots, csv.Values, out bool allOk);
            for (int i = 0; i < rec.Values.Count && i < _slotLbl.Count; i++)
            {
                _slotLbl[i].Text = rec.Values[i].RawValue + (rec.Values[i].InLimits ? "  ✓" : "  ✗");
                _slotLbl[i].TextColor = rec.Values[i].InLimits ? Ui.Green : Ui.Red;
            }
            details = "\n" + string.Join("   ", rec.Values.Select(v => $"[{v.SlotNo}] {v.RawValue} {(v.InLimits ? "✓" : "✗")}"));
            if (csv.ToolSn != s.ToolSn) rec.ErrorCode = ErrorCodes.ToolMismatch;      // Z19
            else if (!allOk) rec.ErrorCode = ErrorCodes.OutOfLimits;
            else rec.Result = "OK";
        }
        else if (csv != null && csv.ToolSn.Length > 0 && TextRules.IsValid(csv.ToolSn)) rec.ToolSn = csv.ToolSn;
        _log.Info($"Measurement finished: {rec.SerialNo} step {rec.StepNo} {rec.Result} {rec.ErrorCode} tool {rec.ToolSn}{details.Replace('\n', ' ')}");
        _busy = false;
        if (_countdown != null) _countdown.Text = "";
        Save(rec, details);
    }
}
