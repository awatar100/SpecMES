using Dapper;
using Mes.Core;
using Npgsql;

namespace Mes.Data;

/// <summary>Kroki marszruty biezacej (routing_*) i migawki zlecenia (order_*).</summary>
public class RoutingRepo
{
    readonly Db _db;
    public RoutingRepo(Db db) { _db = db; }

    const string StepCols = "step_no, title, body, manual_meas, lsl, usl, unit, auto_meas, exe_path, timeout_s, tool_id, duration_s, work_instr, consumes, is_final, is_repeatable";

    public static List<Step> LoadSteps(NpgsqlConnection c, NpgsqlTransaction? tx, bool orderSnapshot, long ownerId, int? onlyStepNo = null)
    {
        var (t, o) = orderSnapshot ? ("order_step", "order_id") : ("routing_step", "routing_id");
        string filter = $"s.{o}=@ownerId" + (onlyStepNo != null ? " and s.step_no=@onlyStepNo" : "");
        var p = new { ownerId, onlyStepNo };
        var steps = c.Query<Step>($"select s.*, t.tool_sn from {t} s left join tool t on t.id=s.tool_id where {filter} order by s.step_no", p, tx).ToList();
        var slots = c.Query<StepSlot>($"select sl.* from {t}_slot sl join {t} s on s.id=sl.step_id where {filter} order by sl.slot_no", p, tx).ToLookup(x => x.StepId);
        var mats = c.Query<StepMaterial>($@"select m.*, mt.name material_name, mt.unit material_unit, mt.batch_pattern
            from {t}_material m join {t} s on s.id=m.step_id join material mt on mt.id=m.material_id where {filter} order by m.slot_no", p, tx).ToLookup(x => x.StepId);
        foreach (var s in steps) { s.Slots = slots[s.Id].ToList(); s.Materials = mats[s.Id].ToList(); }
        return steps;
    }

    public List<Step> RoutingSteps(long productId)
    {
        using var c = _db.Open();
        long? rid = c.ExecuteScalar<long?>("select id from routing where product_id=@productId", new { productId });
        return rid == null ? new List<Step>() : LoadSteps(c, null, false, rid.Value);
    }

    public List<Step> OrderSteps(long orderId)
    {
        using var c = _db.Open();
        return LoadSteps(c, null, true, orderId);
    }

    /// <summary>Add (isNew) albo Update kroku w marszrucie biezacej. Nie wplywa na migawki aktywnych zlecen.</summary>
    public void SaveStep(long productId, Step s, bool isNew, long byUser)
    {
        StepValidator.Validate(s);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("insert into routing(product_id, updated_by) values (@productId, @byUser) on conflict (product_id) do update set updated_at=now(), updated_by=@byUser", new { productId, byUser }, tx);
        long rid = c.ExecuteScalar<long>("select id from routing where product_id=@productId", new { productId }, tx);
        long? existing = c.ExecuteScalar<long?>("select id from routing_step where routing_id=@rid and step_no=@StepNo", new { rid, s.StepNo }, tx);
        if (isNew && existing != null)
            throw new MesException($"Krok {s.StepNo} już istnieje – użyj „Aktualizuj”.", $"Step {s.StepNo} already exists – use \"Update\".");
        if (!isNew && existing == null)
            throw new MesException($"Krok {s.StepNo} nie istnieje – użyj „Dodaj”.", $"Step {s.StepNo} does not exist – use \"Add\".");
        var p = new
        {
            rid, id = existing, s.StepNo, s.Title, Body = s.Body ?? "", s.ManualMeas, Lsl = s.ManualMeas ? s.Lsl : null, Usl = s.ManualMeas ? s.Usl : null,
            Unit = s.Unit ?? "", s.AutoMeas, ExePath = s.AutoMeas ? s.ExePath : "", TimeoutS = s.AutoMeas ? s.TimeoutS : 0,
            ToolId = s.AutoMeas || s.ManualMeas ? s.ToolId : null, s.DurationS, WorkInstr = s.WorkInstr ?? "", s.Consumes, s.IsFinal, s.IsRepeatable
        };
        long stepId;
        if (isNew)
            stepId = c.ExecuteScalar<long>($@"insert into routing_step(routing_id, {StepCols}) values
                (@rid,@StepNo,@Title,@Body,@ManualMeas,@Lsl,@Usl,@Unit,@AutoMeas,@ExePath,@TimeoutS,@ToolId,@DurationS,@WorkInstr,@Consumes,@IsFinal,@IsRepeatable) returning id", p, tx);
        else
        {
            stepId = existing!.Value;
            c.Execute(@"update routing_step set title=@Title, body=@Body, manual_meas=@ManualMeas, lsl=@Lsl, usl=@Usl, unit=@Unit, auto_meas=@AutoMeas, exe_path=@ExePath,
                timeout_s=@TimeoutS, tool_id=@ToolId, duration_s=@DurationS, work_instr=@WorkInstr, consumes=@Consumes, is_final=@IsFinal, is_repeatable=@IsRepeatable where id=@id", p, tx);
            c.Execute("delete from routing_step_slot where step_id=@stepId", new { stepId }, tx);
            c.Execute("delete from routing_step_material where step_id=@stepId", new { stepId }, tx);
        }
        foreach (var sl in s.Slots)
            c.Execute("insert into routing_step_slot(step_id, slot_no, kind, lsl, usl, expected_text, unit) values (@stepId,@SlotNo,@Kind,@Lsl,@Usl,@ExpectedText,@Unit)",
                new { stepId, sl.SlotNo, sl.Kind, Lsl = sl.Kind == "NUM" ? sl.Lsl : null, Usl = sl.Kind == "NUM" ? sl.Usl : null, ExpectedText = sl.Kind == "TEXT" ? sl.ExpectedText : "", Unit = sl.Unit ?? "" }, tx);
        foreach (var m in s.Materials)
            c.Execute("insert into routing_step_material(step_id, slot_no, material_id, qty) values (@stepId,@SlotNo,@MaterialId,@Qty)", new { stepId, m.SlotNo, m.MaterialId, m.Qty }, tx);
        Db.Audit(c, tx, byUser, null, isNew ? "STEP_ADD" : "STEP_UPDATE", "routing_step", stepId, new { productId, s.StepNo, s.Title });
        tx.Commit();
    }

    public void DeleteStep(long productId, int stepNo, long byUser)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var row = c.QueryFirstOrDefault<Step>("select s.id, s.is_final from routing_step s join routing r on r.id=s.routing_id where r.product_id=@productId and s.step_no=@stepNo", new { productId, stepNo }, tx);
        if (row == null) throw new MesException($"Krok {stepNo} nie istnieje.", $"Step {stepNo} does not exist.");
        if (row.IsFinal && c.ExecuteScalar<int>("select count(*) from routing_step s join routing r on r.id=s.routing_id where r.product_id=@productId", new { productId }, tx) > 1)
            throw new MesException("Krok finalny można usunąć dopiero po oznaczeniu innego kroku jako finalny.", "The final step can be deleted only after marking another step as final.");
        c.Execute("delete from routing_step where id=@Id", new { row.Id }, tx);
        Db.Audit(c, tx, byUser, null, "STEP_DELETE", "routing_step", row.Id, new { productId, stepNo });
        tx.Commit();
    }
}

/// <summary>Zlecenia produkcyjne: tworzenie z generowaniem numerow, start (migawka), zamkniecie.</summary>
public class OrderRepo
{
    readonly Db _db;
    public OrderRepo(Db db) { _db = db; }

    const string OrderSelect = @"select o.*, p.prefix || p.erp_value || '[' || repeat('n', p.num_length) || ']' || p.suffix as product_pattern,
        (select min(serial_no) from unit u where u.order_id=o.id) first_serial, (select max(serial_no) from unit u where u.order_id=o.id) last_serial
        from work_order o join product p on p.id=o.product_id";

    public List<WorkOrder> Orders(bool? open = null)
    {
        using var c = _db.Open();
        string where = open == null ? "" : open.Value ? " where o.status in ('CREATED','ACTIVE')" : " where o.status in ('CLOSED','CANCELLED')";
        return c.Query<WorkOrder>(OrderSelect + where + " order by o.id desc").ToList();
    }

    public WorkOrder? ByNo(string orderNo)
    {
        using var c = _db.Open();
        return c.QuerySingleOrDefault<WorkOrder>(OrderSelect + " where o.order_no=@orderNo", new { orderNo });
    }

    public WorkOrder Create(long productId, string bomVersion, int qty, DateTime? plannedStart, DateTime? plannedEnd, string notes, long byUser)
    {
        TextRules.Require(bomVersion, "Wersja BOM", "BOM version", 25);
        if (qty <= 0) throw new MesException("Ilość do produkcji musi być większa od 0.", "Quantity must be greater than 0.");
        if ((notes ?? "").Length > 1000) throw new MesException("Notatki mogą mieć najwyżej 1000 znaków.", "Notes may have at most 1000 characters.");
        if (plannedStart != null && plannedEnd != null && plannedStart > plannedEnd)
            throw new MesException("Przewidywana data startu jest późniejsza niż data ukończenia.", "Planned start is later than planned end.");

        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var p = c.QuerySingleOrDefault<Product>("select * from product where id=@productId", new { productId }, tx)
            ?? throw new MesException("Nie znaleziono wyrobu.", "Product not found.");
        if (!c.ExecuteScalar<bool>("select exists(select 1 from routing r join routing_step s on s.routing_id=r.id where r.product_id=@productId)", new { productId }, tx))
            throw new MesException("Zlecenie można utworzyć tylko dla wyrobu ze zdefiniowaną marszrutą.", "An order can only be created for a product with a defined routing.");

        // Z2: blokada wiersza licznika – dwoch planistow nie dostanie tych samych numerow
        c.Execute("insert into serial_counter(product_id, last_value) values (@productId, 0) on conflict do nothing", new { productId }, tx);
        int last = c.ExecuteScalar<int>("select last_value from serial_counter where product_id=@productId for update", new { productId }, tx);
        int max = SerialNumber.MaxValue(p.NumLength);
        if ((long)last + qty > max)   // Z4 / R2.11
            throw new MesException($"Zakres numerów seryjnych tego wyrobu jest wyczerpany: pozostało {max - last} z {max}. Zdefiniuj inne oznaczenie wyrobu.",
                $"The serial number range of this product is exhausted: {max - last} of {max} left. Define another product designation.", "COUNTER_OVERFLOW");

        // Z7: numer zlecenia ZP-RRRRMMDD-NNN
        c.Execute("select pg_advisory_xact_lock(hashtext('mes_order_no'))", transaction: tx);
        string day = c.ExecuteScalar<string>("select to_char(now(), 'YYYYMMDD')", transaction: tx)!;
        int seq = c.ExecuteScalar<int>("select coalesce(max(substring(order_no from 13)::int), 0) + 1 from work_order where order_no like @pat", new { pat = $"ZP-{day}-%" }, tx);
        string orderNo = $"ZP-{day}-{seq:D3}";

        long id = c.ExecuteScalar<long>(@"insert into work_order(order_no, product_id, bom_version, qty, planned_start, planned_end, notes, created_by)
            values (@orderNo,@productId,@bomVersion,@qty,@ps::date,@pe::date,@notes,@byUser) returning id",
            new { orderNo, productId, bomVersion, qty, ps = plannedStart?.ToString("yyyy-MM-dd"), pe = plannedEnd?.ToString("yyyy-MM-dd"), notes = notes ?? "", byUser }, tx);
        try
        {
            c.Execute(@"insert into unit(serial_no, order_id) select @pre || lpad(g::text, @len, '0') || @suf, @id from generate_series(@from, @to) g",
                new { pre = p.Prefix + p.ErpValue, suf = p.Suffix, len = p.NumLength, id, from = last + 1, to = last + qty }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new MesException("Kolizja numerów seryjnych: wygenerowany numer już istnieje w systemie dla innego wyrobu. Zlecenie nie zostało utworzone.",
                "Serial number collision: a generated number already exists in the system for another product. The order was not created.");
        }
        c.Execute("update serial_counter set last_value=@v where product_id=@productId", new { v = last + qty, productId }, tx);
        Db.Audit(c, tx, byUser, null, "ORDER_CREATE", "work_order", id, new { orderNo, qty, from = last + 1, to = last + qty });
        tx.Commit();
        return ByNo(orderNo)!;
    }

    /// <summary>„Start produkcji”: zamrozenie marszruty w tabelach migawki (R2.20, Z8).</summary>
    public void Start(long orderId, long byUser)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var o = c.QuerySingle<WorkOrder>("select * from work_order where id=@orderId for update", new { orderId }, tx);
        if (o.Status != OrderStatus.Created)
            throw new MesException("Wystartować można tylko zlecenie w stanie „Utworzone”.", "Only an order in the \"Created\" state can be started.");
        long? rid = c.ExecuteScalar<long?>("select id from routing where product_id=@ProductId", new { o.ProductId }, tx);
        var steps = rid == null ? new List<Step>() : RoutingRepo.LoadSteps(c, tx, false, rid.Value);
        StepValidator.ValidateRoutingForStart(steps);

        const string cols = "step_no, title, body, manual_meas, lsl, usl, unit, auto_meas, exe_path, timeout_s, tool_id, duration_s, work_instr, consumes, is_final, is_repeatable";
        c.Execute($"insert into order_step(order_id, {cols}) select @orderId, {cols} from routing_step where routing_id=@rid", new { orderId, rid }, tx);
        c.Execute(@"insert into order_step_slot(step_id, slot_no, kind, lsl, usl, expected_text, unit)
            select os.id, sl.slot_no, sl.kind, sl.lsl, sl.usl, sl.expected_text, sl.unit from routing_step_slot sl
            join routing_step rs on rs.id=sl.step_id join order_step os on os.order_id=@orderId and os.step_no=rs.step_no where rs.routing_id=@rid", new { orderId, rid }, tx);
        c.Execute(@"insert into order_step_material(step_id, slot_no, material_id, qty)
            select os.id, m.slot_no, m.material_id, m.qty from routing_step_material m
            join routing_step rs on rs.id=m.step_id join order_step os on os.order_id=@orderId and os.step_no=rs.step_no where rs.routing_id=@rid", new { orderId, rid }, tx);
        c.Execute("update unit set current_step_no=@first, version=version+1 where order_id=@orderId", new { first = steps.Min(s => s.StepNo), orderId }, tx);
        c.Execute("update work_order set status='ACTIVE', started_at=now() where id=@orderId", new { orderId }, tx);
        Db.Audit(c, tx, byUser, null, "ORDER_START", "work_order", orderId, new { steps = steps.Count });
        tx.Commit();
    }

    /// <summary>Zamkniecie (nieodwracalne, Z9). Zlecenie niewystartowane -> Anulowane.</summary>
    public string Close(long orderId, long byUser)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var o = c.QuerySingle<WorkOrder>("select * from work_order where id=@orderId for update", new { orderId }, tx);
        if (o.Status is OrderStatus.Closed or OrderStatus.Cancelled)
            throw new MesException("Zlecenie jest już zamknięte.", "The order is already closed.");
        string st = o.Status == OrderStatus.Created ? OrderStatus.Cancelled : OrderStatus.Closed;
        c.Execute("update work_order set status=@st, closed_at=now() where id=@orderId", new { st, orderId }, tx);
        int open = c.ExecuteScalar<int>("select count(*) from unit where order_id=@orderId and status in ('IN_PROGRESS','BLOCKED')", new { orderId }, tx);
        Db.Audit(c, tx, byUser, null, st == OrderStatus.Closed ? "ORDER_CLOSE" : "ORDER_CANCEL", "work_order", orderId, new { unfinishedUnits = open });
        tx.Commit();
        return st;
    }

    public int UnfinishedUnits(long orderId)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<int>("select count(*) from unit where order_id=@orderId and status in ('IN_PROGRESS','BLOCKED')", new { orderId });
    }
}
