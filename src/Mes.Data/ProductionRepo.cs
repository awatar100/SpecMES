using System.Text.Json;
using Dapper;
using Mes.Core;
using Npgsql;

namespace Mes.Data;

/// <summary>Operacje stanowiska produkcyjnego (rola bazodanowa o minimalnych uprawnieniach).</summary>
public class ProductionRepo
{
    readonly Db _db;
    public ProductionRepo(Db db) { _db = db; }

    const string UnitSelect = @"select u.*, o.order_no, o.status as order_status, o.bom_version, st.hostname as locked_host,
        (u.locked_until is not null and u.locked_until > now()) as lock_active
        from unit u join work_order o on o.id=u.order_id left join station st on st.id=u.locked_station_id";

    /// <summary>Z29: stanowisko = nazwa komputera, rejestrowane automatycznie.</summary>
    public long RegisterStation(string hostname)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("insert into station(hostname) values (@hostname) on conflict (hostname) do update set last_seen=now() returning id", new { hostname });
    }

    /// <summary>Skan numeru: sprawdzenie stanu sztuki, zalozenie znacznika „w pracy” (Z25), pobranie konfiguracji kroku z migawki.</summary>
    public UnitContext OpenUnit(string serial, long stationId)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var u = c.QuerySingleOrDefault<UnitInfo>(UnitSelect + " where u.serial_no=@serial for update of u", new { serial }, tx)
            ?? throw new MesException($"Numer seryjny „{serial}” nie istnieje w systemie.", $"Serial number \"{serial}\" does not exist in the system.", "UNKNOWN");
        if (u.OrderStatus == OrderStatus.Created)
            throw new MesException($"Zlecenie {u.OrderNo} nie zostało jeszcze wystartowane przez planistę.", $"Order {u.OrderNo} has not been started by the planner yet.", "ORDER_NOT_STARTED");
        if (u.OrderStatus != OrderStatus.Active)
            throw new MesException($"Zlecenie {u.OrderNo} jest zamknięte – praca nad sztuką jest zablokowana.", $"Order {u.OrderNo} is closed – work on this unit is blocked.", "ORDER_CLOSED");
        switch (u.Status)
        {
            case UnitStatus.Blocked:
                throw new MesException($"Sztuka ZABLOKOWANA po wyniku NOK na kroku {u.CurrentStepNo}. Wymagana decyzja działu jakości.", $"Unit BLOCKED after NOK at step {u.CurrentStepNo}. A quality department decision is required.", "BLOCKED");
            case UnitStatus.FinishedOk:
                throw new MesException("Sztuka zakończyła produkcję z wynikiem OK.", "The unit has finished production with result OK.", "FINISHED");
            case UnitStatus.FinishedNok:
                throw new MesException("Sztuka zakończyła produkcję z wynikiem NOK.", "The unit has finished production with result NOK.", "FINISHED");
            case UnitStatus.Scrapped:
                throw new MesException("Sztuka została zezłomowana (SCRAP).", "The unit has been scrapped (SCRAP).", "SCRAPPED");
        }
        if (u.LockActive && u.LockedStationId != stationId)
            throw new MesException($"Sztuka jest w pracy na stanowisku „{u.LockedHost}”.", $"The unit is in work at station \"{u.LockedHost}\".", "LOCKED");

        var step = RoutingRepo.LoadSteps(c, tx, true, u.OrderId, u.CurrentStepNo).FirstOrDefault()
            ?? throw new MesException($"Brak kroku {u.CurrentStepNo} w migawce marszruty zlecenia.", $"Step {u.CurrentStepNo} is missing in the order routing snapshot.");
        var nos = c.Query<int>("select step_no from order_step where order_id=@OrderId order by step_no", new { u.OrderId }, tx).ToList();
        c.Execute("update unit set locked_station_id=@stationId, locked_until=now() + interval '30 minutes' where id=@Id", new { stationId, u.Id }, tx);
        tx.Commit();
        return new UnitContext { Unit = u, Step = step, StepIndex = nos.IndexOf(step.StepNo) + 1, StepCount = nos.Count };
    }

    /// <summary>Zdjecie znacznika „w pracy” (Esc na stanowisku).</summary>
    public void ReleaseUnit(long unitId, long stationId)
    {
        using var c = _db.Open();
        c.Execute("update unit set locked_station_id=null, locked_until=null where id=@unitId and locked_station_id=@stationId", new { unitId, stationId });
    }

    /// <summary>Z17/Z18: narzedzie istnieje, jest aktywne i w datach waznosci (data serwera).</summary>
    public (bool Exists, bool Valid) CheckTool(string toolSn)
    {
        using var c = _db.Open();
        var v = c.ExecuteScalar<bool?>(ToolValidSql + " where tool_sn=@toolSn", new { toolSn });
        return (v != null, v == true);
    }

    const string ToolValidSql = "select (is_active and (valid_from is null or valid_from <= current_date) and (valid_to is null or valid_to >= current_date)) from tool";

    /// <summary>
    /// Zapis wyniku kroku i przesuniecie sztuki – jedna transakcja, idempotentna po client_guid (Z26, Z30).
    /// </summary>
    public SaveOutcome SaveResult(StepResultRecord r, long stationId)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var done = c.QueryFirstOrDefault<SaveOutcome>(@"select true as already_saved, sr.result, sr.error_code, u.status as unit_status, u.current_step_no as next_step_no
            from step_result sr join unit u on u.id=sr.unit_id where sr.client_guid=@ClientGuid", new { r.ClientGuid }, tx);
        if (done != null) return done;

        var u = c.QuerySingle<UnitInfo>(UnitSelect + " where u.id=@UnitId for update of u", new { r.UnitId }, tx);
        if (u.OrderStatus != OrderStatus.Active)
            throw new MesException($"Zlecenie {u.OrderNo} jest zamknięte – wynik nie został zapisany.", $"Order {u.OrderNo} is closed – the result was not saved.", "ORDER_CLOSED");
        if (u.Version != r.UnitVersion || u.Status != UnitStatus.InProgress || u.CurrentStepNo != r.StepNo)
            throw new MesException("Konflikt: stan sztuki zmienił się od momentu skanu. Zeskanuj numer ponownie.", "Conflict: the unit state has changed since the scan. Scan the number again.", "CONFLICT");

        var step = c.QuerySingle<Step>("select * from order_step where order_id=@OrderId and step_no=@StepNo", new { u.OrderId, r.StepNo }, tx);
        string result = r.Result == "OK" ? "OK" : "NOK";
        string? err = r.ErrorCode;
        if (step.ToolId != null && err is null or ErrorCodes.OutOfLimits)
        {
            // Z17: waznosc narzedzia sprawdzana w chwili zapisu; nieważne narzedzie = NOK mimo wynikow w granicach
            bool valid = c.ExecuteScalar<bool?>(ToolValidSql + " where id=@ToolId", new { step.ToolId }, tx) == true;
            if (!valid) { result = "NOK"; err = ErrorCodes.ToolInvalid; }
        }

        long rid = c.ExecuteScalar<long>(@"insert into step_result(client_guid, unit_id, step_no, result, comment, user_id, station_id, tool_id, tool_sn, started_at, finished_at, is_buffered, error_code)
            select @ClientGuid, @UnitId, @StepNo, @result, @Comment, @UserId, @stationId, @ToolId, @ToolSn, f.t - make_interval(secs => @DurationS), f.t, @IsBuffered, @err
            from (select coalesce(@fin::timestamptz, now()) as t) f returning id",
            new { r.ClientGuid, r.UnitId, r.StepNo, result, Comment = r.Comment ?? "", r.UserId, stationId, step.ToolId, r.ToolSn, r.DurationS, fin = r.IsBuffered ? r.FinishedAtUtc : null, r.IsBuffered, err }, tx);
        foreach (var v in r.Values)
            c.Execute("insert into step_result_value(result_id, slot_no, raw_value, num_value, in_limits) values (@rid,@SlotNo,@RawValue,@NumValue,@InLimits)", new { rid, v.SlotNo, v.RawValue, v.NumValue, v.InLimits }, tx);
        foreach (var m in r.Materials)
            c.Execute("insert into material_usage(result_id, slot_no, material_id, qty, batch_no) values (@rid,@SlotNo,@MaterialId,@Qty,@BatchNo)", new { rid, m.SlotNo, m.MaterialId, m.Qty, m.BatchNo }, tx);
        if (!string.IsNullOrEmpty(r.ConsumedPart))
            c.Execute("insert into consumed_part(result_id, part_serial) values (@rid,@p)", new { rid, p = r.ConsumedPart }, tx);

        // Przejscie stanu sztuki
        string status = UnitStatus.InProgress;
        int? next = r.StepNo;
        if (result == "OK")
        {
            if (step.IsFinal) status = UnitStatus.FinishedOk;
            else
            {
                next = c.ExecuteScalar<int?>("select min(step_no) from order_step where order_id=@OrderId and step_no>@StepNo", new { u.OrderId, r.StepNo }, tx);
                if (next == null) { status = UnitStatus.FinishedOk; next = r.StepNo; }
            }
        }
        else if (!step.IsRepeatable)
            status = step.IsFinal ? UnitStatus.FinishedNok : UnitStatus.Blocked;

        c.Execute("update unit set current_step_no=@next, status=@status, version=version+1, locked_station_id=null, locked_until=null where id=@UnitId", new { next, status, r.UnitId }, tx);
        tx.Commit();
        return new SaveOutcome { Result = result, ErrorCode = err, UnitStatus = status, NextStepNo = next };
    }

    /// <summary>Z32: bledy krytyczne dodatkowo do tabeli zdarzen (gdy jest polaczenie).</summary>
    public void LogEvent(long? userId, long? stationId, string action, object details)
    {
        try { using var c = _db.Open(); Db.Audit(c, null, userId, stationId, action, "prodapp", null, details); }
        catch { /* brak polaczenia – zostaje log lokalny */ }
    }
}

/// <summary>Bufor offline: pliki buffer/*.json (Z30). Kolejny skan zablokowany do oproznienia (R2.23).</summary>
public class OfflineBuffer
{
    readonly string _dir;
    public OfflineBuffer(string dir) { _dir = dir; Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.Combine(dir, "failed")); }

    public class Entry
    {
        public StepResultRecord Record { get; set; } = new();
        public long StationId { get; set; }
    }

    public void Add(StepResultRecord r, long stationId)
    {
        r.IsBuffered = true;
        r.FinishedAtUtc ??= DateTime.UtcNow;
        string path = Path.Combine(_dir, r.ClientGuid + ".json");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Entry { Record = r, StationId = stationId }));
        File.Move(tmp, path, true);
    }

    public int Count => Directory.GetFiles(_dir, "*.json").Length;

    /// <summary>Proba wyslania bufora. Zwraca liczbe pozostalych wpisow. Bledy danych (konflikt) trafiaja do buffer/failed.</summary>
    public int Flush(ProductionRepo repo, FileLogger log, Action<StepResultRecord, Exception>? onRejected = null)
    {
        foreach (var f in Directory.GetFiles(_dir, "*.json").OrderBy(File.GetCreationTimeUtc))
        {
            Entry? e;
            try { e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(f)); }
            catch (Exception ex) { log.Error("Buffer file unreadable: " + f, ex); MoveFailed(f); continue; }
            if (e == null) { MoveFailed(f); continue; }
            try
            {
                repo.SaveResult(e.Record, e.StationId);
                File.Delete(f);
                log.Info($"Buffered result sent: {e.Record.SerialNo} step {e.Record.StepNo} {e.Record.Result} guid {e.Record.ClientGuid}");
            }
            catch (Exception ex) when (Db.IsConnectionError(ex))
            {
                log.Warn("Buffer flush: still no connection (" + ex.Message + ")");
                break;
            }
            catch (Exception ex)
            {
                log.Error($"Buffered result REJECTED by database: {e.Record.SerialNo} step {e.Record.StepNo} guid {e.Record.ClientGuid}", ex);
                MoveFailed(f);
                onRejected?.Invoke(e.Record, ex);
            }
        }
        return Count;
    }

    void MoveFailed(string f)
    {
        try { File.Move(f, Path.Combine(_dir, "failed", Path.GetFileName(f)), true); } catch { }
    }
}
