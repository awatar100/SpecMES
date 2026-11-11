using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Dapper;
using Mes.Core;
using Npgsql;

namespace Mes.Data;

/// <summary>Modul jakosci: podglad sztuki, odblokowanie, cofniecie, scrap (Z33, Z34) – kazda decyzja z komentarzem i wpisem audytu.</summary>
public class QualityRepo
{
    readonly Db _db;
    public QualityRepo(Db db) { _db = db; }

    const string UnitSelect = @"select u.*, o.order_no, o.status as order_status, o.bom_version, st.hostname as locked_host,
        (u.locked_until is not null and u.locked_until > now()) as lock_active
        from unit u join work_order o on o.id=u.order_id left join station st on st.id=u.locked_station_id";

    public UnitInfo? Find(string serial)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<UnitInfo>(UnitSelect + " where u.serial_no=@serial", new { serial });
    }

    public List<int> StepNos(long orderId)
    {
        using var c = _db.Open();
        return c.Query<int>("select step_no from order_step where order_id=@orderId order by step_no", new { orderId }).ToList();
    }

    public List<ResultRow> History(long unitId)
    {
        using var c = _db.Open();
        return c.Query<ResultRow>(@"select r.id, r.step_no, coalesce(s.title,'') step_title, r.result, r.error_code, r.comment, au.login user_login, st.hostname station,
            r.tool_sn, r.started_at, r.finished_at, r.is_buffered
            from step_result r join unit u on u.id=r.unit_id left join order_step s on s.order_id=u.order_id and s.step_no=r.step_no
            join app_user au on au.id=r.user_id join station st on st.id=r.station_id where r.unit_id=@unitId order by r.id desc", new { unitId }).ToList();
    }

    /// <summary>Szczegoly jednego wyniku jako pary nazwa–wartosc (tabela w module jakosci).</summary>
    public List<KeyValueRow> ResultDetails(long resultId)
    {
        using var c = _db.Open();
        var rows = new List<KeyValueRow>();
        foreach (var v in c.Query("select slot_no, raw_value, in_limits from step_result_value where result_id=@resultId order by slot_no", new { resultId }))
            rows.Add(new KeyValueRow(L.T("Wynik ", "Result ") + v.slot_no, v.raw_value + ((bool)v.in_limits ? "  ✓" : "  ✗ " + L.T("poza granicami", "out of limits"))));
        foreach (var m in c.Query("select mu.slot_no, mt.name, mu.qty, mt.unit, mu.batch_no from material_usage mu join material mt on mt.id=mu.material_id where result_id=@resultId order by slot_no", new { resultId }))
            rows.Add(new KeyValueRow(L.T("Materiał ", "Material ") + m.slot_no, $"{m.name}: {((decimal)m.qty).ToString("0.####", CultureInfo.InvariantCulture)} {m.unit}, {L.T("partia", "batch")} {m.batch_no}"));
        var part = c.ExecuteScalar<string?>("select part_serial from consumed_part where result_id=@resultId", new { resultId });
        if (part != null) rows.Add(new KeyValueRow("Consumes", part));
        return rows;
    }

    UnitInfo LockForDecision(NpgsqlConnection c, NpgsqlTransaction tx, long unitId, string comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) throw new MesException("Decyzja jakości wymaga komentarza.", "A quality decision requires a comment.");
        if (comment.Length > 500) throw new MesException("Komentarz może mieć najwyżej 500 znaków.", "Comment may have at most 500 characters.");
        var u = c.QuerySingle<UnitInfo>(UnitSelect + " where u.id=@unitId for update of u", new { unitId }, tx);
        if (u.OrderStatus != OrderStatus.Active)
            throw new MesException("Zlecenie nie jest aktywne – decyzje jakości dla jego sztuk są zablokowane.", "The order is not active – quality decisions for its units are blocked.");
        if (u.Status == UnitStatus.Scrapped)
            throw new MesException("Sztuka jest zezłomowana – to stan końcowy.", "The unit is scrapped – this is a final state.");
        return u;
    }

    /// <summary>Odblokowanie: ponowienie na zatrzymanym kroku.</summary>
    public void Unblock(long unitId, string comment, long byUser)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var u = LockForDecision(c, tx, unitId, comment);
        if (u.Status != UnitStatus.Blocked) throw new MesException("Sztuka nie jest zablokowana.", "The unit is not blocked.");
        c.Execute("update unit set status='IN_PROGRESS', version=version+1, locked_station_id=null, locked_until=null where id=@unitId", new { unitId }, tx);
        Db.Audit(c, tx, byUser, null, "QUALITY_UNBLOCK", "unit", unitId, new { u.SerialNo, step = u.CurrentStepNo, comment });
        tx.Commit();
    }

    /// <summary>Cofniecie do istniejacego kroku o nizszym numerze; historia wynikow pozostaje.</summary>
    public void MoveTo(long unitId, int stepNo, string comment, long byUser)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var u = LockForDecision(c, tx, unitId, comment);
        if (!c.ExecuteScalar<bool>("select exists(select 1 from order_step where order_id=@OrderId and step_no=@stepNo)", new { u.OrderId, stepNo }, tx))
            throw new MesException($"Krok {stepNo} nie istnieje w marszrucie zlecenia.", $"Step {stepNo} does not exist in the order routing.");
        if (stepNo >= u.CurrentStepNo && u.Status is UnitStatus.InProgress or UnitStatus.Blocked)
            throw new MesException("Sztukę można cofnąć tylko do kroku o niższym numerze.", "A unit can only be moved back to a step with a lower number.");
        if (stepNo > u.CurrentStepNo)
            throw new MesException("Sztukę można cofnąć tylko do kroku o niższym numerze.", "A unit can only be moved back to a step with a lower number.");
        c.Execute("update unit set status='IN_PROGRESS', current_step_no=@stepNo, version=version+1, locked_station_id=null, locked_until=null where id=@unitId", new { stepNo, unitId }, tx);
        Db.Audit(c, tx, byUser, null, "QUALITY_MOVE", "unit", unitId, new { u.SerialNo, from = u.CurrentStepNo, to = stepNo, fromStatus = u.Status, comment });
        tx.Commit();
    }

    public void Scrap(long unitId, string comment, long byUser)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var u = LockForDecision(c, tx, unitId, comment);
        c.Execute("update unit set status='SCRAPPED', version=version+1, locked_station_id=null, locked_until=null where id=@unitId", new { unitId }, tx);
        Db.Audit(c, tx, byUser, null, "QUALITY_SCRAP", "unit", unitId, new { u.SerialNo, step = u.CurrentStepNo, fromStatus = u.Status, comment });
        tx.Commit();
    }

    /// <summary>Reczne zdjecie znacznika „w pracy” (Z25).</summary>
    public void ReleaseLock(long unitId, long byUser)
    {
        using var c = _db.Open();
        c.Execute("update unit set locked_station_id=null, locked_until=null where id=@unitId", new { unitId });
        Db.Audit(c, null, byUser, null, "QUALITY_RELEASE_LOCK", "unit", unitId);
    }
}

/// <summary>KPI (R2.15, Z35) oraz eksport CSV / Excel (Z36).</summary>
public class ReportRepo
{
    readonly Db _db;
    public ReportRepo(Db db) { _db = db; }

    public OrderKpi Kpi(long orderId)
    {
        using var c = _db.Open();
        var k = c.QuerySingle<OrderKpi>(@"select count(*) total,
              count(*) filter (where status='FINISHED_OK') ok, count(*) filter (where status='FINISHED_NOK') nok,
              count(*) filter (where status='SCRAPPED') scrapped, count(*) filter (where status='BLOCKED') blocked,
              count(*) filter (where status='IN_PROGRESS') in_progress from unit where order_id=@orderId", new { orderId });

        // takt = sredni czas miedzy kolejnymi przesunieciami dowolnej sztuki zlecenia do nastepnego etapu
        k.TaktS = c.ExecuteScalar<double?>(@"select extract(epoch from avg(d)) from (
              select r.finished_at - lag(r.finished_at) over (order by r.finished_at, r.id) d
              from step_result r join unit u on u.id=r.unit_id where u.order_id=@orderId and r.result='OK') x where d is not null", new { orderId });
        // czas otwarcia = od pierwszej operacji do zamkniecia przez planiste (lub do teraz)
        k.OpenS = c.ExecuteScalar<double?>(@"select extract(epoch from coalesce(o.closed_at, now()) - (select min(r.started_at) from step_result r join unit u on u.id=r.unit_id where u.order_id=o.id))
              from work_order o where o.id=@orderId", new { orderId });
        // czas produkcji = od pierwszej do ostatniej operacji, tylko sztuki zakonczone
        k.AvgProductionS = c.ExecuteScalar<double?>(@"select extract(epoch from avg(t)) from (
              select max(r.finished_at) - min(r.started_at) t from step_result r join unit u on u.id=r.unit_id
              where u.order_id=@orderId and u.status in ('FINISHED_OK','FINISHED_NOK') group by u.id) x", new { orderId });
        // suma ponowien = wyniki ponad pierwszy dla pary (sztuka, krok)
        k.Retries = c.ExecuteScalar<int>(@"select coalesce(sum(n - 1), 0)::int from (
              select count(*) n from step_result r join unit u on u.id=r.unit_id where u.order_id=@orderId group by r.unit_id, r.step_no) x", new { orderId });
        // usterkowosc indeksu (wyrobu) z ostatniego statusu, wszystkie zlecenia
        var pd = c.QuerySingle<(int Ok, int Nok)>(@"select count(*) filter (where u.status='FINISHED_OK')::int, count(*) filter (where u.status in ('FINISHED_NOK','SCRAPPED','BLOCKED'))::int
              from unit u join work_order o on o.id=u.order_id where o.product_id=(select product_id from work_order where id=@orderId)", new { orderId });
        k.ProductOk = pd.Ok; k.ProductNok = pd.Nok;

        const string trans = @"select r.unit_id, r.step_no, r.started_at - lag(r.finished_at) over w as d, lag(r.result) over w as prev_result, lag(r.step_no) over w as prev_step
              from step_result r join unit u on u.id=r.unit_id where u.order_id=@orderId window w as (partition by r.unit_id order by r.id)";
        k.AvgTransitionS = c.ExecuteScalar<double?>($"select extract(epoch from avg(d)) from ({trans}) x where prev_result='OK' and step_no > prev_step", new { orderId });
        k.Steps = c.Query<StepKpi>($@"select s.step_no, s.title, s.duration_s planned_s,
              (select extract(epoch from avg(r.finished_at - r.started_at)) from step_result r join unit u on u.id=r.unit_id where u.order_id=s.order_id and r.step_no=s.step_no) avg_actual_s,
              (select count(*) from step_result r join unit u on u.id=r.unit_id where u.order_id=s.order_id and r.step_no=s.step_no and r.result='OK')::int ok_count,
              (select count(*) from step_result r join unit u on u.id=r.unit_id where u.order_id=s.order_id and r.step_no=s.step_no and r.result='NOK')::int nok_count,
              (select extract(epoch from avg(d)) from ({trans}) x where prev_result='OK' and x.step_no=s.step_no and x.step_no > prev_step) avg_transition_s
              from order_step s where s.order_id=@orderId order by s.step_no", new { orderId }).ToList();
        return k;
    }

    public static string KpiText(WorkOrder o, OrderKpi k)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{L.T("Zlecenie", "Order")}: {o.OrderNo}   [{OrderStatus.Name(o.Status)}]   BOM: {o.BomVersion}");
        sb.AppendLine($"{L.T("Numery", "Serials")}: {o.FirstSerial} … {o.LastSerial}");
        sb.AppendLine($"{L.T("Utworzono", "Created")}: {o.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}   {L.T("Start", "Started")}: {o.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—"}   {L.T("Zamknięto", "Closed")}: {o.ClosedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—"}");
        sb.AppendLine();
        sb.AppendLine($"{L.T("Sztuk razem", "Units total")}: {k.Total}");
        sb.AppendLine($"OK: {k.Ok} ({k.OkPct:0.0}%)    NOK: {k.Nok + k.Scrapped} ({k.NokPct:0.0}%)  [{L.T("w tym SCRAP", "incl. SCRAP")}: {k.Scrapped}]");
        sb.AppendLine($"{L.T("W toku", "In progress")}: {k.InProgress}    {L.T("Zablokowane", "Blocked")}: {k.Blocked}");
        sb.AppendLine();
        sb.AppendLine($"{L.T("Czas taktu", "Takt time")}: {OrderKpi.Dur(k.TaktS)}");
        sb.AppendLine($"{L.T("Czas zlecenia w stanie otwartym", "Order open time")}: {OrderKpi.Dur(k.OpenS)}");
        sb.AppendLine($"{L.T("Średni czas produkcji sztuki (pierwsza–ostatnia operacja)", "Avg unit production time (first–last operation)")}: {OrderKpi.Dur(k.AvgProductionS)}");
        sb.AppendLine($"{L.T("Średni czas przejścia między operacjami", "Avg transition time between operations")}: {OrderKpi.Dur(k.AvgTransitionS)}");
        sb.AppendLine($"{L.T("Suma ponowień w zleceniu", "Total retries in order")}: {k.Retries}");
        sb.AppendLine($"{L.T("Usterkowość wyrobu (wszystkie zlecenia)", "Product defect rate (all orders)")}: {k.ProductDefectPct:0.0}%  (OK {k.ProductOk} / NOK {k.ProductNok})");
        return sb.ToString();
    }

    // ---------- eksport: jeden wiersz na wynik kroku
    static string ExportSql(string where)
    {
        var v = string.Join(", ", Enumerable.Range(1, 10).Select(i => $"(select raw_value from step_result_value x where x.result_id=r.id and x.slot_no={i}) as value_{i}"));
        return $@"select o.order_no, o.bom_version, o.status as order_status, u.serial_no, u.status as unit_status, u.current_step_no,
            r.step_no, s.title as step_title, r.result, r.error_code, r.comment, au.login as user_login, st.hostname as station, r.tool_sn,
            r.started_at, r.finished_at, round(extract(epoch from r.finished_at - r.started_at)::numeric, 1) as duration_s, s.duration_s as planned_duration_s, r.is_buffered,
            (s.lsl::text || ' .. ' || s.usl::text || ' ' || s.unit) as manual_limits, {v},
            (select string_agg(mt.name || '=' || mu.qty::text || ' ' || mt.unit || ' (plan ' || coalesce(om.qty::text,'?') || ') partia/batch ' || mu.batch_no, ' | ' order by mu.slot_no)
               from material_usage mu join material mt on mt.id=mu.material_id left join order_step_material om on om.step_id=s.id and om.slot_no=mu.slot_no where mu.result_id=r.id) as materials,
            (select part_serial from consumed_part cp where cp.result_id=r.id) as consumed_part
            from unit u join work_order o on o.id=u.order_id
            left join step_result r on r.unit_id=u.id
            left join order_step s on s.order_id=o.id and s.step_no=r.step_no
            left join app_user au on au.id=r.user_id left join station st on st.id=r.station_id
            where {where} order by u.serial_no, r.id";
    }

    public List<IDictionary<string, object?>> OrderRows(string orderNo)
    {
        using var c = _db.Open();
        if (!c.ExecuteScalar<bool>("select exists(select 1 from work_order where order_no=@orderNo)", new { orderNo }))
            throw new MesException($"Zlecenie „{orderNo}” nie istnieje.", $"Order \"{orderNo}\" does not exist.");
        return c.Query(ExportSql("o.order_no=@orderNo"), new { orderNo }).Select(r => (IDictionary<string, object?>)r).ToList();
    }

    public List<IDictionary<string, object?>> SerialRows(string serial)
    {
        using var c = _db.Open();
        if (!c.ExecuteScalar<bool>("select exists(select 1 from unit where serial_no=@serial)", new { serial }))
            throw new MesException($"Numer seryjny „{serial}” nie istnieje.", $"Serial number \"{serial}\" does not exist.");
        return c.Query(ExportSql("u.serial_no=@serial"), new { serial }).Select(r => (IDictionary<string, object?>)r).ToList();
    }

    /// <summary>Decyzje jakosci dla sztuki (dziennik audytu) – dolaczane do eksportu numeru seryjnego.</summary>
    public List<IDictionary<string, object?>> SerialAudit(string serial)
    {
        using var c = _db.Open();
        return c.Query(@"select a.logged_at, a.action, au.login as user_login, a.details::text as details from audit_log a
            join unit u on u.id=a.entity_id and a.entity='unit' left join app_user au on au.id=a.user_id where u.serial_no=@serial order by a.id", new { serial })
            .Select(r => (IDictionary<string, object?>)r).ToList();
    }

    static string Fmt(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    static string CsvCell(string s) => s.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Z36: UTF-8 z BOM, separator ;, kropka dziesietna.</summary>
    public static void WriteCsv(string path, params List<IDictionary<string, object?>>[] tables)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var rows in tables)
        {
            if (rows.Count == 0) continue;
            if (!first) sb.Append("\r\n");
            first = false;
            sb.Append(string.Join(";", rows[0].Keys)).Append("\r\n");
            foreach (var r in rows) sb.Append(string.Join(";", r.Values.Select(v => CsvCell(Fmt(v))))).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Raport Excel zlecenia: arkusz KPI, kroki, dane szczegolowe (ClosedXML).</summary>
    public void WriteExcel(string path, WorkOrder o)
    {
        var k = Kpi(o.Id);
        var rows = OrderRows(o.OrderNo);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("KPI");
        int r = 1;
        foreach (var line in KpiText(o, k).Split('\n'))
            ws.Cell(r++, 1).Value = line.TrimEnd('\r');
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Column(1).Width = 90;

        var st = wb.AddWorksheet(L.T("Kroki", "Steps"));
        string[] head = { L.T("Krok", "Step"), L.T("Opis", "Title"), L.T("Czas planowany [s]", "Planned [s]"), L.T("Czas średni [s]", "Avg actual [s]"), "OK", "NOK", L.T("Śr. przejście [s]", "Avg transition [s]") };
        for (int i = 0; i < head.Length; i++) st.Cell(1, i + 1).Value = head[i];
        st.Row(1).Style.Font.Bold = true;
        r = 2;
        foreach (var s in k.Steps)
        {
            st.Cell(r, 1).Value = s.StepNo; st.Cell(r, 2).Value = s.Title; st.Cell(r, 3).Value = s.PlannedS;
            if (s.AvgActualS != null) st.Cell(r, 4).Value = Math.Round(s.AvgActualS.Value, 1);
            st.Cell(r, 5).Value = s.OkCount; st.Cell(r, 6).Value = s.NokCount;
            if (s.AvgTransitionS != null) st.Cell(r, 7).Value = Math.Round(s.AvgTransitionS.Value, 1);
            r++;
        }
        st.Columns().AdjustToContents();

        var d = wb.AddWorksheet(L.T("Dane", "Data"));
        if (rows.Count > 0)
        {
            int col = 1;
            foreach (var key in rows[0].Keys) d.Cell(1, col++).Value = key;
            d.Row(1).Style.Font.Bold = true;
            r = 2;
            foreach (var row in rows)
            {
                col = 1;
                foreach (var v in row.Values)
                {
                    var cell = d.Cell(r, col++);
                    switch (v)
                    {
                        case null: break;
                        case DateTime dt: cell.Value = dt.ToLocalTime(); cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss"; break;
                        case bool b: cell.Value = b; break;
                        case int or long or decimal or double or float: cell.Value = Convert.ToDouble(v, CultureInfo.InvariantCulture); break;
                        default: cell.Value = v.ToString(); break;
                    }
                }
                r++;
            }
            d.SheetView.FreezeRows(1);
            d.Columns().AdjustToContents(1, 200);
        }
        wb.SaveAs(path);
    }
}
