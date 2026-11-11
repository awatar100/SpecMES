using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Shared.UI;

namespace Mes.Management;

/// <summary>
/// R3.2c: marszruta produkcyjna wyrobu. Add / Update edytuja marszrute biezaca;
/// zlecenia juz wystartowane pracuja na zamrozonej migawce, wiec zmiany obowiazuja od kolejnego zlecenia.
/// </summary>
public class RoutingPanel : Panel
{
    readonly Ctx _c;
    readonly DropDown _product = new();
    readonly GridView _grid;
    List<Step> _steps = new();
    List<Material> _materials = new();

    readonly TextBox _no = new() { Width = 80 }, _title = new() { MaxLength = 50 }, _duration = new() { Text = "0", Width = 80 }, _instr = new() { MaxLength = 500 },
        _lsl = new() { Width = 90 }, _usl = new() { Width = 90 }, _unit = new() { MaxLength = 10, Width = 90 }, _exe = new() { MaxLength = 500 }, _timeout = new() { Text = "10", Width = 80 };
    readonly TextArea _body = new() { Height = 70 };
    readonly CheckBox _manual = new(), _auto = new(), _consumes = new(), _final = new(), _repeat = new();
    readonly DropDown _tool = new();
    readonly (DropDown Kind, TextBox Lsl, TextBox Usl, TextBox Expected, TextBox Unit)[] _slots = new (DropDown, TextBox, TextBox, TextBox, TextBox)[10];
    readonly (CheckBox Used, DropDown Material, TextBox Qty)[] _mats = new (CheckBox, DropDown, TextBox)[10];

    public RoutingPanel(Ctx c, bool write)
    {
        _c = c;
        _manual.Text = L.T("Manual measurement – pomiar ręczny (1 wartość, wymaga skanu miernika)", "Manual measurement (1 value, requires meter scan)");
        _auto.Text = L.T("Automatic Measurement – pomiar automatyczny (przycisk POMIAR / F1)", "Automatic Measurement (MEASURE button / F1)");
        _consumes.Text = L.T("Consumes – skan numeru części składowej (np. PCB)", "Consumes – scan of a component number (e.g. PCB)");
        _final.Text = L.T("Final Step? – krok finalny", "Final Step?");
        _repeat.Text = L.T("Repeatable Step? – NOK nie blokuje, operator ponawia", "Repeatable Step? – NOK does not block, operator retries");
        _manual.CheckedChanged += (_, _) => { if (_manual.Checked == true) _auto.Checked = false; };
        _auto.CheckedChanged += (_, _) => { if (_auto.Checked == true) _manual.Checked = false; };

        _grid = Ui.Grid<Step>(
            Ui.Col<Step>(L.T("Krok", "Step"), s => s.StepNo.ToString(), 60), Ui.Col<Step>(L.T("Opis", "Title"), s => s.Title, 260),
            Ui.Col<Step>(L.T("Rodzaj", "Kind"), s => s.Kind, 170), Ui.Col<Step>(L.T("Narzędzie", "Tool"), s => s.ToolSn ?? "", 130),
            Ui.Col<Step>(L.T("Materiały", "Materials"), s => s.Materials.Count == 0 ? "" : s.Materials.Count.ToString(), 80),
            Ui.Col<Step>("Consumes", s => s.Consumes ? "✓" : "", 80), Ui.Col<Step>(L.T("Powtarzalny", "Repeatable"), s => s.IsRepeatable ? "✓" : "", 100),
            Ui.Col<Step>(L.T("Finalny", "Final"), s => s.IsFinal ? "✓" : "", 70), Ui.Col<Step>(L.T("Czas [s]", "Time [s]"), s => s.DurationS.ToString(), 70));
        _grid.Height = 200;
        _grid.SelectionChanged += (_, _) => { if (_grid.SelectedItem is Step s) Show(s); };
        _product.SelectedIndexChanged += (_, _) => LoadSteps();

        // 10 wynikow pomiaru automatycznego
        var slotTable = new TableLayout { Spacing = new Size(6, 4) };
        slotTable.Rows.Add(new TableRow(Ui.Lbl("#"), Ui.Lbl(L.T("Typ", "Type")), Ui.Lbl("LSL"), Ui.Lbl("USL"), Ui.Lbl(L.T("Tekst oczekiwany", "Expected text")), Ui.Lbl("Unit"), null));
        for (int i = 0; i < 10; i++)
        {
            _slots[i] = (Mx.Drop(new[] { ("", "—"), ("NUM", L.T("Liczba", "Number")), ("TEXT", L.T("Tekst", "Text")) }), new TextBox { Width = 80 }, new TextBox { Width = 80 },
                new TextBox { Width = 130, MaxLength = 100 }, new TextBox { Width = 70, MaxLength = 10 });
            slotTable.Rows.Add(new TableRow(Ui.Lbl((i + 1).ToString()), _slots[i].Kind, _slots[i].Lsl, _slots[i].Usl, _slots[i].Expected, _slots[i].Unit, null));
        }
        // 10 materialow
        var matTable = new TableLayout { Spacing = new Size(6, 4) };
        matTable.Rows.Add(new TableRow(Ui.Lbl("Is used?"), Ui.Lbl("Material Name"), Ui.Lbl("Quantity"), null));
        for (int i = 0; i < 10; i++)
        {
            var row = (new CheckBox { Text = (i + 1).ToString() }, new DropDown { Width = 260 }, new TextBox { Width = 90 });
            _mats[i] = row;
            row.Item2.SelectedIndexChanged += (_, _) =>
            {
                var m = _materials.FirstOrDefault(x => x.Id == row.Item2.KeyId());
                if (m != null && string.IsNullOrWhiteSpace(row.Item3.Text)) row.Item3.Text = Ui.Dec(m.DefaultQty);
                if (m != null) row.Item1.Checked = true;
            };
            matTable.Rows.Add(new TableRow(row.Item1, row.Item2, row.Item3, null));
        }

        var add = Ui.Btn("Add – " + L.T("dodaj krok", "add step"), () => SaveStep(true), 170); add.Enabled = write;
        var upd = Ui.Btn("Update – " + L.T("aktualizuj krok", "update step"), () => SaveStep(false), 190); upd.Enabled = write;
        var del = Ui.Btn(L.T("Usuń krok", "Delete step"), Delete, 110); del.Enabled = write;

        var general = new GroupBox
        {
            Text = L.T("Krok", "Step"), Padding = 10,
            Content = new TableLayout
            {
                Spacing = new Size(6, 8),
                Rows =
                {
                    new TableRow(Ui.Form(
                        (L.T("Numer kroku (10, 20, 30 …)", "Step number (10, 20, 30 …)"), Ui.Row(_no, Ui.Btn(L.T("Nowy krok – podpowiedz numer", "New step – suggest number"), NewStep))),
                        (L.T("Opis kroku (do 50 znaków)", "Step title (up to 50 chars)"), _title),
                        (L.T("Treść kroku (do 500 znaków)", "Step body (up to 500 chars)"), _body),
                        ("Step duration [s]", _duration),
                        (L.T("Work Instructions (ścieżka na stanowisku)", "Work Instructions (path on the station)"), _instr))),
                    new TableRow(_manual),
                    new TableRow(Ui.Row(Ui.Lbl("LSL"), _lsl, Ui.Lbl("USL"), _usl, Ui.Lbl("Unit"), _unit)),
                    new TableRow(_auto),
                    new TableRow(Ui.Form((L.T("Program pomiarowy (current.csv w tym samym katalogu)", "Measurement program (current.csv in the same folder)"), _exe), ("Timeout [s]", _timeout),
                        (L.T("Narzędzie (ręczny: skan miernika; automat: ToolID z CSV)", "Tool (manual: meter scan; automatic: ToolID from CSV)"), _tool))),
                    new TableRow(_consumes), new TableRow(_repeat), new TableRow(_final),
                    new TableRow(Ui.Row(add, upd, del)),
                    new TableRow(Ui.Lbl(L.T("Zmiany obowiązują od kolejnego zlecenia – zlecenia aktywne mają zamrożoną marszrutę.", "Changes apply from the next order – active orders use a frozen routing."), color: Ui.Gray)),
                }
            }
        };
        var right = new TableLayout
        {
            Spacing = new Size(6, 8),
            Rows =
            {
                new TableRow(new GroupBox { Text = L.T("Wyniki pomiaru automatycznego (wypełnione kolejno od 1 = liczba wartości w current.csv)", "Automatic measurement results (filled consecutively from 1 = number of values in current.csv)"), Padding = 8, Content = slotTable }),
                new TableRow(new GroupBox { Text = "Material declaration", Padding = 8, Content = matTable }), null
            }
        };
        var editor = new Scrollable { Border = BorderType.None, Content = new TableLayout { Spacing = new Size(10, 8), Rows = { new TableRow(new TableCell(general, true), new TableCell(right)), null } } };
        Content = new TableLayout
        {
            Spacing = new Size(6, 8),
            Rows =
            {
                new TableRow(Ui.Row(Ui.Lbl(L.T("Wyrób:", "Product:"), bold: true), _product, Ui.Btn(L.T("Odśwież", "Refresh"), LoadLists))),
                new TableRow(_grid),
                new TableRow(editor) { ScaleHeight = true }
            }
        };
        LoadLists();
    }

    void LoadLists() => Ui.Try(this, () =>
    {
        string? sel = _product.SelectedKey;
        _materials = _c.Master.Materials();
        var tools = _c.Master.Tools();
        _tool.Items.Clear();
        _tool.Items.Add(new ListItem { Key = "", Text = "—" });
        foreach (var t in tools) _tool.Items.Add(new ListItem { Key = t.Id.ToString(), Text = t.ToolSn + (t.Name.Length > 0 ? "  –  " + t.Name : "") + (t.IsValidOn(DateTime.Today) ? "" : L.T("  (NIEWAŻNE)", "  (INVALID)")) });
        _tool.SelectedIndex = 0;
        foreach (var m in _mats)
        {
            m.Material.Items.Clear();
            m.Material.Items.Add(new ListItem { Key = "", Text = "—" });
            foreach (var mt in _materials) m.Material.Items.Add(new ListItem { Key = mt.Id.ToString(), Text = $"{mt.Name} [{mt.Unit}]" });
            m.Material.SelectedIndex = 0;
        }
        _product.Items.Clear();
        foreach (var p in _c.Master.Products()) _product.Items.Add(new ListItem { Key = p.Id.ToString(), Text = p.Pattern });
        if (sel != null && _product.Items.Any(i => i.Key == sel)) _product.SelectedKey = sel;
        else if (_product.Items.Count > 0) _product.SelectedIndex = 0;
        LoadSteps();
    }, _c.Log);

    void LoadSteps()
    {
        var pid = _product.KeyId();
        Ui.Try(this, () =>
        {
            _steps = pid == null ? new List<Step>() : _c.Routing.RoutingSteps(pid.Value);
            _grid.DataStore = _steps;
            NewStep();
        }, _c.Log);
    }

    void NewStep() => Show(new Step { StepNo = StepValidator.SuggestNext(_steps.Select(s => s.StepNo)), TimeoutS = 10 });

    void Show(Step s)
    {
        _no.Text = s.StepNo.ToString(); _title.Text = s.Title; _body.Text = s.Body; _duration.Text = s.DurationS.ToString(); _instr.Text = s.WorkInstr;
        _manual.Checked = s.ManualMeas; _auto.Checked = s.AutoMeas;
        _lsl.Text = Ui.Dec(s.Lsl); _usl.Text = Ui.Dec(s.Usl); _unit.Text = s.Unit;
        _exe.Text = s.ExePath; _timeout.Text = s.TimeoutS.ToString();
        _tool.SelectedKey = s.ToolId?.ToString() ?? "";
        _consumes.Checked = s.Consumes; _final.Checked = s.IsFinal; _repeat.Checked = s.IsRepeatable;
        for (int i = 0; i < 10; i++)
        {
            var sl = s.Slots.FirstOrDefault(x => x.SlotNo == i + 1);
            _slots[i].Kind.SelectedKey = sl?.Kind ?? "";
            _slots[i].Lsl.Text = Ui.Dec(sl?.Lsl); _slots[i].Usl.Text = Ui.Dec(sl?.Usl); _slots[i].Expected.Text = sl?.ExpectedText ?? ""; _slots[i].Unit.Text = sl?.Unit ?? "";
            var m = s.Materials.FirstOrDefault(x => x.SlotNo == i + 1);
            _mats[i].Material.SelectedKey = m?.MaterialId.ToString() ?? "";
            _mats[i].Qty.Text = m == null ? "" : Ui.Dec(m.Qty);
            _mats[i].Used.Checked = m != null;
        }
    }

    Step Read()
    {
        var s = new Step
        {
            StepNo = Mx.Int(_no.Text, "Numer kroku", "Step number"), Title = _title.Text.Trim(), Body = _body.Text ?? "",
            DurationS = Mx.Int(_duration.Text, "Step duration", "Step duration"), WorkInstr = _instr.Text.Trim(),
            ManualMeas = _manual.Checked == true, AutoMeas = _auto.Checked == true, Lsl = Ui.Dec(_lsl.Text), Usl = Ui.Dec(_usl.Text), Unit = _unit.Text.Trim(),
            ExePath = _exe.Text.Trim(), TimeoutS = _auto.Checked == true ? Mx.Int(_timeout.Text, "Timeout", "Timeout") : 0, ToolId = _tool.KeyId(),
            Consumes = _consumes.Checked == true, IsFinal = _final.Checked == true, IsRepeatable = _repeat.Checked == true
        };
        for (int i = 0; i < 10; i++)
        {
            string kind = _slots[i].Kind.SelectedKey ?? "";
            if (kind.Length > 0)
                s.Slots.Add(new StepSlot { SlotNo = i + 1, Kind = kind, Lsl = Ui.Dec(_slots[i].Lsl.Text), Usl = Ui.Dec(_slots[i].Usl.Text), ExpectedText = _slots[i].Expected.Text.Trim(), Unit = _slots[i].Unit.Text.Trim() });
            if (_mats[i].Used.Checked == true)
                s.Materials.Add(new StepMaterial { SlotNo = i + 1, MaterialId = _mats[i].Material.KeyId() ?? 0, Qty = Ui.Dec(_mats[i].Qty.Text) ?? 0 });
        }
        return s;
    }

    void SaveStep(bool isNew) => Ui.Try(this, () =>
    {
        var pid = _product.KeyId() ?? throw new MesException("Wybierz wyrób.", "Choose a product.");
        var s = Read();
        if (s.IsFinal && _steps.Any(x => x.IsFinal && x.StepNo != s.StepNo))
            throw new MesException("Marszruta ma już krok finalny. Odznacz „Final Step?” w tamtym kroku (Update), a potem oznacz ten.", "The routing already has a final step. Untick \"Final Step?\" there (Update) first, then mark this one.");
        _c.Routing.SaveStep(pid, s, isNew, _c.UserId);
        LoadSteps();
    }, _c.Log);

    void Delete() => Ui.Try(this, () =>
    {
        var pid = _product.KeyId() ?? throw new MesException("Wybierz wyrób.", "Choose a product.");
        int no = Mx.Int(_no.Text, "Numer kroku", "Step number");
        if (!Ui.Confirm(this, L.T($"Usunąć krok {no} z marszruty bieżącej?", $"Delete step {no} from the current routing?"))) return;
        _c.Routing.DeleteStep(pid, no, _c.UserId);
        LoadSteps();
    }, _c.Log);
}
