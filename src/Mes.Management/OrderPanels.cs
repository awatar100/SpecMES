using Eto.Drawing;
using Eto.Forms;
using Mes.Core;
using Mes.Data;
using Mes.Shared.UI;

namespace Mes.Management;

/// <summary>R3.2b: zlecenia produkcyjne – tworzenie (generowanie numerow), start (zamrozenie marszruty), zamkniecie, statystyka.</summary>
public class OrdersPanel : Panel
{
    readonly Ctx _c;
    readonly GridView _grid;
    readonly DropDown _filter, _product = new();
    readonly TextBox _bom = new() { MaxLength = 25 }, _qty = new() { Text = "1", Width = 100 };
    readonly DateTimePicker _start = new() { Mode = DateTimePickerMode.Date, Value = DateTime.Today }, _end = new() { Mode = DateTimePickerMode.Date, Value = DateTime.Today.AddDays(7) };
    readonly TextArea _notes = new() { Height = 60 }, _stats = new() { ReadOnly = true, Font = Fonts.Monospace(10) };

    public OrdersPanel(Ctx c, bool write)
    {
        _c = c;
        _filter = Mx.Drop(new[] { ("open", L.T("Aktywne i utworzone", "Active and created")), ("closed", L.T("Zamknięte i anulowane", "Closed and cancelled")), ("all", L.T("Wszystkie", "All")) });
        _filter.SelectedIndexChanged += (_, _) => Reload();
        _grid = Ui.Grid<WorkOrder>(
            Ui.Col<WorkOrder>(L.T("Numer zlecenia", "Order number"), o => o.OrderNo, 150), Ui.Col<WorkOrder>("Status", o => OrderStatus.Name(o.Status), 100),
            Ui.Col<WorkOrder>(L.T("Wyrób", "Product"), o => o.ProductPattern, 220), Ui.Col<WorkOrder>("BOM", o => o.BomVersion, 100),
            Ui.Col<WorkOrder>(L.T("Ilość", "Qty"), o => o.Qty.ToString(), 60),
            Ui.Col<WorkOrder>(L.T("Utworzono", "Created"), o => Ui.Local(o.CreatedAt, "yyyy-MM-dd HH:mm"), 125),
            Ui.Col<WorkOrder>(L.T("Plan start", "Planned start"), o => o.PlannedStart?.ToString("yyyy-MM-dd") ?? "", 95),
            Ui.Col<WorkOrder>(L.T("Plan koniec", "Planned end"), o => o.PlannedEnd?.ToString("yyyy-MM-dd") ?? "", 95),
            Ui.Col<WorkOrder>(L.T("Start", "Started"), o => Ui.Local(o.StartedAt, "yyyy-MM-dd HH:mm"), 125),
            Ui.Col<WorkOrder>(L.T("Zamknięto", "Closed"), o => Ui.Local(o.ClosedAt, "yyyy-MM-dd HH:mm"), 125),
            Ui.Col<WorkOrder>(L.T("Notatki", "Notes"), o => o.Notes.Replace('\n', ' '), 200));
        _grid.SelectionChanged += (_, _) => ShowStats();

        var create = Ui.Btn(L.T("Utwórz zlecenie", "Create order"), Create, 160); create.Enabled = write;
        var start = Ui.Btn(L.T("▶ Start produkcji (wystartuj zlecenie)", "▶ Start production (start order)"), Start, 280); start.Enabled = write;
        var close = Ui.Btn(L.T("■ Zakończ zlecenie", "■ Close order"), CloseOrder, 180); close.Enabled = write;

        var form = new GroupBox
        {
            Text = L.T("Nowe zlecenie produkcyjne", "New production order"), Padding = 10, Width = 470,
            Content = new TableLayout
            {
                Spacing = new Size(6, 8),
                Rows =
                {
                    new TableRow(Ui.Form((L.T("Wyrób (z marszrutą)", "Product (with routing)"), _product), (L.T("Wersja BOM (do 25 znaków)", "BOM version (up to 25 chars)"), _bom),
                        (L.T("Ilość do produkcji", "Quantity to produce"), _qty), (L.T("Przewidywana data startu", "Planned start date"), _start),
                        (L.T("Przewidywana data ukończenia", "Planned end date"), _end), (L.T("Notatki", "Notes"), _notes))),
                    new TableRow(create), null
                }
            }
        };
        var sel = new GroupBox
        {
            Text = L.T("Wybrane zlecenie – statystyka", "Selected order – statistics"), Padding = 10,
            Content = new TableLayout { Spacing = new Size(6, 8), Rows = { new TableRow(Ui.Row(start, close, Ui.Btn(L.T("Odśwież", "Refresh"), Reload))), new TableRow(_stats) { ScaleHeight = true } } }
        };
        Content = new Splitter
        {
            Orientation = Orientation.Vertical, Position = 280,
            Panel1 = new TableLayout { Spacing = new Size(6, 6), Rows = { new TableRow(Ui.Row(Ui.Lbl(L.T("Lista zleceń:", "Order list:"), bold: true), _filter)), new TableRow(_grid) { ScaleHeight = true } } },
            Panel2 = new TableLayout { Spacing = new Size(10, 8), Rows = { new TableRow(new TableCell(form), new TableCell(sel, true)) } }
        };
        Reload();
    }

    void Reload() => Ui.Try(this, () =>
    {
        string? keep = (_grid.SelectedItem as WorkOrder)?.OrderNo;
        string? selProd = _product.SelectedKey;
        _product.Items.Clear();
        foreach (var p in _c.Master.Products().Where(p => p.HasRouting)) _product.Items.Add(new ListItem { Key = p.Id.ToString(), Text = p.Pattern });
        if (selProd != null && _product.Items.Any(i => i.Key == selProd)) _product.SelectedKey = selProd;
        else if (_product.Items.Count > 0) _product.SelectedIndex = 0;

        var list = _c.Orders.Orders(_filter.SelectedKey switch { "open" => true, "closed" => false, _ => null });
        _grid.DataStore = list;
        int idx = keep == null ? -1 : list.FindIndex(o => o.OrderNo == keep);
        if (idx >= 0) _grid.SelectRow(idx); else _stats.Text = "";
    }, _c.Log);

    void ShowStats()
    {
        if (_grid.SelectedItem is not WorkOrder o) { _stats.Text = ""; return; }
        Ui.Try(this, () => _stats.Text = ReportRepo.KpiText(o, _c.Reports.Kpi(o.Id)), _c.Log);
    }

    void Create() => Ui.Try(this, () =>
    {
        long pid = _product.KeyId() ?? throw new MesException("Wybierz wyrób ze zdefiniowaną marszrutą.", "Choose a product with a defined routing.");
        int qty = Mx.Int(_qty.Text, "Ilość do produkcji", "Quantity to produce");
        var o = _c.Orders.Create(pid, _bom.Text.Trim(), qty, _start.Value, _end.Value, _notes.Text ?? "", _c.UserId);
        _filter.SelectedKey = "open";
        Reload();
        Ui.Info(this, L.T($"Utworzono zlecenie {o.OrderNo}.\nNumery seryjne: {o.FirstSerial} … {o.LastSerial}\n\nZlecenie wymaga jeszcze uruchomienia przyciskiem „Start produkcji”.",
            $"Order {o.OrderNo} created.\nSerial numbers: {o.FirstSerial} … {o.LastSerial}\n\nThe order still has to be started with \"Start production\"."));
    }, _c.Log);

    void Start()
    {
        if (_grid.SelectedItem is not WorkOrder o) return;
        if (!Ui.Confirm(this, L.T($"Wystartować zlecenie {o.OrderNo}?\n\nBieżąca marszruta wyrobu zostanie ZAMROŻONA dla tego zlecenia – późniejsze zmiany marszruty go nie obejmą.",
            $"Start order {o.OrderNo}?\n\nThe current product routing will be FROZEN for this order – later routing changes will not affect it."))) return;
        if (Ui.Try(this, () => _c.Orders.Start(o.Id, _c.UserId), _c.Log)) Reload();
    }

    void CloseOrder()
    {
        if (_grid.SelectedItem is not WorkOrder o) return;
        int open = 0;
        if (!Ui.Try(this, () => open = _c.Orders.UnfinishedUnits(o.Id), _c.Log)) return;
        string warn = o.Status == OrderStatus.Created
            ? L.T($"Zlecenie {o.OrderNo} nie zostało wystartowane – zostanie ANULOWANE. Operacja jest nieodwracalna.", $"Order {o.OrderNo} has not been started – it will be CANCELLED. This cannot be undone.")
            : L.T($"Zakończyć zlecenie {o.OrderNo}?\n\nUWAGA: w zleceniu jest {open} niedokończonych sztuk. Zamknięcie PERMANENTNIE zablokuje możliwość pracy nad otwartymi numerami seryjnymi. Operacja jest nieodwracalna.",
                $"Close order {o.OrderNo}?\n\nWARNING: the order has {open} unfinished units. Closing will PERMANENTLY block any further work on the open serial numbers. This cannot be undone.");
        if (!Ui.Confirm(this, warn)) return;
        if (Ui.Try(this, () => _c.Orders.Close(o.Id, _c.UserId), _c.Log)) Reload();
    }
}

/// <summary>Prosty wykres slupkowy rysowany na Drawable (bez zaleznosci zewnetrznych – dziala na WPF i GTK).</summary>
public class BarChart : Drawable
{
    public string Caption = "";
    public string[] Legend = Array.Empty<string>();
    public Color[] Colors = Array.Empty<Color>();
    public List<(string Label, double[] Values)> Groups = new();
    public bool Stacked;
    public Color[]? GroupColors;   // kolor per grupa (wykres z jedna seria)

    public BarChart() { Height = 230; Paint += OnPaint; }

    void OnPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        var fg = SystemColors.ControlText;
        var font = Fonts.Sans(9); var bold = Fonts.Sans(10, FontStyle.Bold);
        float w = Width, h = Height;
        g.DrawText(bold, fg, 4, 2, Caption);
        float lx = 4, ly = 22;
        for (int i = 0; i < Legend.Length; i++)
        {
            g.FillRectangle(Colors[i], lx, ly + 3, 10, 10);
            g.DrawText(font, fg, lx + 14, ly, Legend[i]);
            lx += 24 + g.MeasureString(font, Legend[i]).Width;
        }
        float left = 46, top = 46, bottom = h - 24, right = w - 8;
        if (Groups.Count == 0 || right - left < 40 || bottom - top < 30)
        { g.DrawText(font, Ui.Gray, left, top + 10, L.T("brak danych", "no data")); return; }
        double max = Groups.Max(gr => Stacked ? gr.Values.Sum() : gr.Values.DefaultIfEmpty(0).Max());
        if (max <= 0) max = 1;
        g.DrawLine(Ui.Gray, left, bottom, right, bottom);
        g.DrawLine(Ui.Gray, left, top, left, bottom);
        for (int t = 0; t <= 4; t++)
        {
            float y = bottom - (bottom - top) * t / 4f;
            g.DrawText(font, Ui.Gray, 2, y - 7, (max * t / 4).ToString(max >= 20 ? "0" : "0.#"));
            if (t > 0) g.DrawLine(Color.FromArgb(128, 128, 128, 60), left, y, right, y);
        }
        float gw = (right - left) / Groups.Count;
        for (int gi = 0; gi < Groups.Count; gi++)
        {
            var (label, vals) = Groups[gi];
            float x0 = left + gi * gw + gw * 0.15f, bw = gw * 0.7f;
            if (Stacked)
            {
                float y = bottom;
                for (int i = 0; i < vals.Length; i++)
                {
                    float bh = (float)(Math.Max(0, vals[i]) / max) * (bottom - top);
                    g.FillRectangle(Colors[i % Colors.Length], x0, y - bh, bw, bh);
                    y -= bh;
                }
            }
            else
            {
                float sw = bw / Math.Max(1, vals.Length);
                for (int i = 0; i < vals.Length; i++)
                {
                    float bh = (float)(Math.Max(0, vals[i]) / max) * (bottom - top);
                    g.FillRectangle(GroupColors != null && gi < GroupColors.Length ? GroupColors[gi] : Colors[i % Colors.Length], x0 + i * sw, bottom - bh, sw - 2, bh);
                }
            }
            var size = g.MeasureString(font, label);
            g.DrawText(font, fg, left + gi * gw + (gw - size.Width) / 2, bottom + 4, label);
        }
    }
}

/// <summary>Dashboard KPI zlecenia (R2.15, R3.2b) + raport Excel.</summary>
public class KpiPanel : Panel
{
    readonly Ctx _c;
    readonly DropDown _order = new() { Width = 420 };
    readonly Label[] _tiles = new Label[6];
    readonly TextArea _text = new() { ReadOnly = true, Font = Fonts.Monospace(10), Height = 200 };
    readonly BarChart _status = new(), _time = new(), _okNok = new(), _trans = new();
    List<WorkOrder> _orders = new();

    public KpiPanel(Ctx c)
    {
        _c = c;
        _order.SelectedIndexChanged += (_, _) => Show();
        string[] names =
        {
            L.T("OK", "OK"), L.T("NOK + SCRAP", "NOK + SCRAP"), L.T("Czas taktu", "Takt time"),
            L.T("Zlecenie otwarte", "Order open"), L.T("Śr. czas produkcji", "Avg production time"), L.T("Ponowienia", "Retries")
        };
        var tileRow = new TableRow();
        for (int i = 0; i < 6; i++)
        {
            _tiles[i] = Ui.Lbl("—", 17, true);
            tileRow.Cells.Add(new TableCell(new GroupBox { Text = names[i], Padding = 8, Content = _tiles[i] }, true));
        }
        var charts = new TableLayout
        {
            Spacing = new Size(10, 10),
            Rows = { new TableRow(new TableCell(_status, true), new TableCell(_okNok, true)), new TableRow(new TableCell(_time, true), new TableCell(_trans, true)) }
        };
        Content = new Scrollable
        {
            Border = BorderType.None,
            Content = new TableLayout
            {
                Spacing = new Size(6, 10),
                Rows =
                {
                    new TableRow(Ui.Row(Ui.Lbl(L.T("Zlecenie:", "Order:"), bold: true), _order, Ui.Btn(L.T("Odśwież", "Refresh"), Reload), Ui.Btn(L.T("Raport Excel…", "Excel report…"), Excel))),
                    new TableRow(new TableLayout { Spacing = new Size(8, 4), Rows = { tileRow } }),
                    new TableRow(charts), new TableRow(_text), null
                }
            }
        };
        Reload();
    }

    void Reload() => Ui.Try(this, () =>
    {
        string? keep = _order.SelectedKey;
        _orders = _c.Orders.Orders();
        _order.Items.Clear();
        foreach (var o in _orders) _order.Items.Add(new ListItem { Key = o.Id.ToString(), Text = $"{o.OrderNo}  [{OrderStatus.Name(o.Status)}]  {o.ProductPattern}  ×{o.Qty}" });
        if (keep != null && _order.Items.Any(i => i.Key == keep)) _order.SelectedKey = keep;
        else if (_order.Items.Count > 0) _order.SelectedIndex = 0;
        Show();
    }, _c.Log);

    WorkOrder? Selected => _orders.FirstOrDefault(o => o.Id == _order.KeyId());

    void Show()
    {
        var o = Selected;
        if (o == null) return;
        Ui.Try(this, () =>
        {
            var k = _c.Reports.Kpi(o.Id);
            _tiles[0].Text = $"{k.Ok}  ({k.OkPct:0.0}%)"; _tiles[0].TextColor = Ui.Green;
            _tiles[1].Text = $"{k.Nok + k.Scrapped}  ({k.NokPct:0.0}%)"; _tiles[1].TextColor = Ui.Red;
            _tiles[2].Text = OrderKpi.Dur(k.TaktS); _tiles[3].Text = OrderKpi.Dur(k.OpenS);
            _tiles[4].Text = OrderKpi.Dur(k.AvgProductionS); _tiles[5].Text = k.Retries.ToString();
            _text.Text = ReportRepo.KpiText(o, k);

            _status.Caption = L.T("Stan sztuk w zleceniu [szt.]", "Unit states in the order [pcs]");
            _status.Legend = Array.Empty<string>();
            _status.Colors = new[] { Ui.Green }; _status.GroupColors = new[] { Ui.Green, Ui.Red, Ui.Gray, Ui.Amber, Ui.Blue };
            _status.Groups = new()
            {
                ("OK", new double[] { k.Ok }), ("NOK", new double[] { k.Nok }), ("SCRAP", new double[] { k.Scrapped }),
                (L.T("Zablok.", "Blocked"), new double[] { k.Blocked }), (L.T("W toku", "In progress"), new double[] { k.InProgress })
            };
            _okNok.Caption = L.T("Wyniki OK / NOK na krokach [liczba operacji]", "OK / NOK results per step [operations]");
            _okNok.Legend = new[] { "OK", "NOK" }; _okNok.Colors = new[] { Ui.Green, Ui.Red }; _okNok.Stacked = true;
            _okNok.Groups = k.Steps.Select(s => (s.StepNo.ToString(), new double[] { s.OkCount, s.NokCount })).ToList();
            _time.Caption = L.T("Czas kroku: planowany a rzeczywisty średni [s]", "Step time: planned vs. actual average [s]");
            _time.Legend = new[] { L.T("planowany", "planned"), L.T("rzeczywisty", "actual") }; _time.Colors = new[] { Ui.Gray, Ui.Blue };
            _time.Groups = k.Steps.Select(s => (s.StepNo.ToString(), new double[] { s.PlannedS, s.AvgActualS ?? 0 })).ToList();
            _trans.Caption = L.T("Średni czas przejścia do kroku z poprzedniego [s]", "Average transition time into the step [s]");
            _trans.Legend = Array.Empty<string>(); _trans.Colors = new[] { Ui.Amber };
            _trans.Groups = k.Steps.Select(s => (s.StepNo.ToString(), new double[] { s.AvgTransitionS ?? 0 })).ToList();
            foreach (var ch in new[] { _status, _okNok, _time, _trans }) ch.Invalidate();
        }, _c.Log);
    }

    void Excel()
    {
        var o = Selected;
        if (o == null) return;
        string path = Mx.SaveDialog(this, o.OrderNo + ".xlsx", "xlsx");
        if (path.Length == 0) return;
        if (Ui.Try(this, () => _c.Reports.WriteExcel(path, o), _c.Log)) Ui.Info(this, L.T("Zapisano raport: ", "Report saved: ") + path);
    }
}
