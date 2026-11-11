-- MES - migracja 001: struktura schematu firmy.
-- Wykonywana przez Instalator w jednej transakcji, jako rola wlasciciela, z search_path = {S}.
-- {S} = nazwa schematu (5-znakowy prefiks, np. prod1).

CREATE TABLE schema_version (
    version     int PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now()
);
INSERT INTO schema_version(version) VALUES (1);

CREATE TABLE station (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    hostname    varchar(100) NOT NULL UNIQUE,
    first_seen  timestamptz NOT NULL DEFAULT now(),
    last_seen   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE app_user (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    login           varchar(50) NOT NULL UNIQUE,
    full_name       varchar(100) NOT NULL DEFAULT '',
    password_hash   text NOT NULL,
    role            varchar(20) NOT NULL CHECK (role IN ('PRODUCTION','PLANNER','TECHNOLOGIST','QUALITY','MASTERDATA')),
    is_active       boolean NOT NULL DEFAULT true,
    must_change_pwd boolean NOT NULL DEFAULT false,
    failed_logins   int NOT NULL DEFAULT 0,
    locked_until    timestamptz,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE attachment (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner_type  varchar(20) NOT NULL CHECK (owner_type IN ('PRODUCT','TOOL')),
    owner_id    bigint NOT NULL,
    file_name   varchar(255) NOT NULL,
    mime        varchar(100) NOT NULL DEFAULT 'application/octet-stream',
    sha256      char(64) NOT NULL,
    content     bytea NOT NULL CHECK (octet_length(content) <= 10485760),
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  bigint REFERENCES app_user(id)
);
CREATE INDEX attachment_owner_idx ON attachment(owner_type, owner_id);

-- Wyrob = PREFIX + WARTOSC ERP + SUFIX + dlugosc Num++ (Z1). Bez UPDATE/DELETE dla aplikacji.
CREATE TABLE product (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    prefix      varchar(50) NOT NULL CHECK (prefix <> '' AND prefix !~ '[$%^&;#]'),
    erp_value   varchar(50) NOT NULL DEFAULT '' CHECK (erp_value !~ '[$%^&;#]'),
    suffix      varchar(50) NOT NULL DEFAULT '' CHECK (suffix !~ '[$%^&;#]'),
    num_length  int NOT NULL CHECK (num_length BETWEEN 3 AND 6),
    notes       varchar(1000) NOT NULL DEFAULT '',
    created_by  bigint REFERENCES app_user(id),
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (prefix, erp_value, suffix, num_length)
);

CREATE TABLE serial_counter (
    product_id  bigint PRIMARY KEY REFERENCES product(id),
    last_value  int NOT NULL DEFAULT 0
);

CREATE TABLE material (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name          varchar(100) NOT NULL UNIQUE,
    unit          varchar(10) NOT NULL DEFAULT '',
    default_qty   numeric(14,4) NOT NULL DEFAULT 0,
    batch_pattern varchar(100) NOT NULL DEFAULT ''
);

CREATE TABLE tool (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tool_sn     varchar(100) NOT NULL UNIQUE,
    name        varchar(100) NOT NULL DEFAULT '',
    valid_from  date,
    valid_to    date,
    is_active   boolean NOT NULL DEFAULT true
);

-- Marszruta biezaca (jedna na wyrob, R2.19)
CREATE TABLE routing (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id  bigint NOT NULL UNIQUE REFERENCES product(id),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  bigint REFERENCES app_user(id)
);

CREATE TABLE routing_step (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    routing_id    bigint NOT NULL REFERENCES routing(id) ON DELETE CASCADE,
    step_no       int NOT NULL CHECK (step_no > 0),
    title         varchar(50) NOT NULL,
    body          varchar(500) NOT NULL DEFAULT '',
    manual_meas   boolean NOT NULL DEFAULT false,
    lsl           numeric,
    usl           numeric,
    unit          varchar(10) NOT NULL DEFAULT '',
    auto_meas     boolean NOT NULL DEFAULT false,
    exe_path      varchar(500) NOT NULL DEFAULT '',
    timeout_s     int NOT NULL DEFAULT 0,
    tool_id       bigint REFERENCES tool(id),
    duration_s    int NOT NULL DEFAULT 0,
    work_instr    varchar(500) NOT NULL DEFAULT '',
    consumes      boolean NOT NULL DEFAULT false,
    is_final      boolean NOT NULL DEFAULT false,
    is_repeatable boolean NOT NULL DEFAULT false,
    UNIQUE (routing_id, step_no),
    CHECK (NOT (manual_meas AND auto_meas))
);

CREATE TABLE routing_step_slot (
    step_id       bigint NOT NULL REFERENCES routing_step(id) ON DELETE CASCADE,
    slot_no       int NOT NULL CHECK (slot_no BETWEEN 1 AND 10),
    kind          varchar(4) NOT NULL CHECK (kind IN ('NUM','TEXT')),
    lsl           numeric,
    usl           numeric,
    expected_text varchar(100) NOT NULL DEFAULT '',
    unit          varchar(10) NOT NULL DEFAULT '',
    PRIMARY KEY (step_id, slot_no)
);

CREATE TABLE routing_step_material (
    step_id     bigint NOT NULL REFERENCES routing_step(id) ON DELETE CASCADE,
    slot_no     int NOT NULL CHECK (slot_no BETWEEN 1 AND 10),
    material_id bigint NOT NULL REFERENCES material(id),
    qty         numeric(14,4) NOT NULL,
    PRIMARY KEY (step_id, slot_no)
);

-- Zlecenie produkcyjne
CREATE TABLE work_order (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_no      varchar(30) NOT NULL UNIQUE,
    product_id    bigint NOT NULL REFERENCES product(id),
    bom_version   varchar(25) NOT NULL DEFAULT '',
    qty           int NOT NULL CHECK (qty > 0),
    planned_start date,
    planned_end   date,
    notes         varchar(1000) NOT NULL DEFAULT '',
    status        varchar(10) NOT NULL DEFAULT 'CREATED' CHECK (status IN ('CREATED','ACTIVE','CLOSED','CANCELLED')),
    created_at    timestamptz NOT NULL DEFAULT now(),
    created_by    bigint REFERENCES app_user(id),
    started_at    timestamptz,
    closed_at     timestamptz
);

-- Migawka marszruty zamrazana przy starcie zlecenia (R2.20)
CREATE TABLE order_step (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id      bigint NOT NULL REFERENCES work_order(id),
    step_no       int NOT NULL CHECK (step_no > 0),
    title         varchar(50) NOT NULL,
    body          varchar(500) NOT NULL DEFAULT '',
    manual_meas   boolean NOT NULL DEFAULT false,
    lsl           numeric,
    usl           numeric,
    unit          varchar(10) NOT NULL DEFAULT '',
    auto_meas     boolean NOT NULL DEFAULT false,
    exe_path      varchar(500) NOT NULL DEFAULT '',
    timeout_s     int NOT NULL DEFAULT 0,
    tool_id       bigint REFERENCES tool(id),
    duration_s    int NOT NULL DEFAULT 0,
    work_instr    varchar(500) NOT NULL DEFAULT '',
    consumes      boolean NOT NULL DEFAULT false,
    is_final      boolean NOT NULL DEFAULT false,
    is_repeatable boolean NOT NULL DEFAULT false,
    UNIQUE (order_id, step_no)
);

CREATE TABLE order_step_slot (
    step_id       bigint NOT NULL REFERENCES order_step(id),
    slot_no       int NOT NULL CHECK (slot_no BETWEEN 1 AND 10),
    kind          varchar(4) NOT NULL CHECK (kind IN ('NUM','TEXT')),
    lsl           numeric,
    usl           numeric,
    expected_text varchar(100) NOT NULL DEFAULT '',
    unit          varchar(10) NOT NULL DEFAULT '',
    PRIMARY KEY (step_id, slot_no)
);

CREATE TABLE order_step_material (
    step_id     bigint NOT NULL REFERENCES order_step(id),
    slot_no     int NOT NULL CHECK (slot_no BETWEEN 1 AND 10),
    material_id bigint NOT NULL REFERENCES material(id),
    qty         numeric(14,4) NOT NULL,
    PRIMARY KEY (step_id, slot_no)
);

-- Sztuka (numer seryjny unikalny w calym schemacie, Z3)
CREATE TABLE unit (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    serial_no         varchar(200) NOT NULL UNIQUE,
    order_id          bigint NOT NULL REFERENCES work_order(id),
    current_step_no   int,
    status            varchar(15) NOT NULL DEFAULT 'IN_PROGRESS'
                      CHECK (status IN ('IN_PROGRESS','BLOCKED','FINISHED_OK','FINISHED_NOK','SCRAPPED')),
    version           int NOT NULL DEFAULT 0,
    locked_station_id bigint REFERENCES station(id),
    locked_until      timestamptz
);
CREATE INDEX unit_order_idx ON unit(order_id);

-- Wyniki: tylko dopisywane (append-only)
CREATE TABLE step_result (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    client_guid uuid NOT NULL UNIQUE,
    unit_id     bigint NOT NULL REFERENCES unit(id),
    step_no     int NOT NULL,
    result      varchar(3) NOT NULL CHECK (result IN ('OK','NOK')),
    comment     varchar(500) NOT NULL DEFAULT '',
    user_id     bigint NOT NULL REFERENCES app_user(id),
    station_id  bigint NOT NULL REFERENCES station(id),
    tool_id     bigint REFERENCES tool(id),
    tool_sn     varchar(100),
    started_at  timestamptz NOT NULL,
    finished_at timestamptz NOT NULL,
    is_buffered boolean NOT NULL DEFAULT false,
    error_code  varchar(30)
);
CREATE INDEX step_result_unit_idx ON step_result(unit_id, id);

CREATE TABLE step_result_value (
    result_id bigint NOT NULL REFERENCES step_result(id),
    slot_no   int NOT NULL,
    raw_value varchar(200) NOT NULL,
    num_value numeric,
    in_limits boolean NOT NULL,
    PRIMARY KEY (result_id, slot_no)
);

CREATE TABLE material_usage (
    result_id   bigint NOT NULL REFERENCES step_result(id),
    slot_no     int NOT NULL,
    material_id bigint NOT NULL REFERENCES material(id),
    qty         numeric(14,4) NOT NULL,
    batch_no    varchar(100) NOT NULL,
    PRIMARY KEY (result_id, slot_no)
);

CREATE TABLE consumed_part (
    result_id   bigint PRIMARY KEY REFERENCES step_result(id),
    part_serial varchar(200) NOT NULL
);

CREATE TABLE audit_log (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    logged_at  timestamptz NOT NULL DEFAULT now(),
    user_id    bigint REFERENCES app_user(id),
    station_id bigint REFERENCES station(id),
    action     varchar(30) NOT NULL,
    entity     varchar(30) NOT NULL DEFAULT '',
    entity_id  bigint,
    details    jsonb
);
CREATE INDEX audit_entity_idx ON audit_log(entity, entity_id);

-- Logowanie przez funkcje SECURITY DEFINER: rola produkcyjna nie czyta skrotow hasel.
-- Format password_hash: pbkdf2$<iteracje>$<sol b64>$<skrot b64>
CREATE FUNCTION mes_login_salt(p_login text) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = {S}, pg_temp AS $$
    SELECT split_part(password_hash, '$', 2) || '$' || split_part(password_hash, '$', 3)
    FROM app_user WHERE login = p_login
$$;

CREATE FUNCTION mes_login(p_login text, p_hash text)
RETURNS TABLE(status text, user_id bigint, role text, full_name text, must_change boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = {S}, pg_temp AS $$
DECLARE u app_user%ROWTYPE;
BEGIN
    SELECT a.* INTO u FROM app_user a WHERE a.login = p_login FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'INVALID'::text, NULL::bigint, NULL::text, NULL::text, false; RETURN;
    END IF;
    IF NOT u.is_active THEN
        RETURN QUERY SELECT 'INACTIVE'::text, NULL::bigint, NULL::text, NULL::text, false; RETURN;
    END IF;
    IF u.locked_until IS NOT NULL AND u.locked_until > now() THEN
        RETURN QUERY SELECT 'LOCKED'::text, NULL::bigint, NULL::text, NULL::text, false; RETURN;
    END IF;
    IF split_part(u.password_hash, '$', 4) = p_hash THEN
        UPDATE app_user a SET failed_logins = 0, locked_until = NULL WHERE a.id = u.id;
        RETURN QUERY SELECT 'OK'::text, u.id, u.role::text, u.full_name::text, u.must_change_pwd; RETURN;
    END IF;
    -- Z28: 5 nieudanych prob blokuje konto na 5 minut
    UPDATE app_user a SET
        failed_logins = CASE WHEN a.failed_logins + 1 >= 5 THEN 0 ELSE a.failed_logins + 1 END,
        locked_until  = CASE WHEN a.failed_logins + 1 >= 5 THEN now() + interval '5 minutes' ELSE NULL END
    WHERE a.id = u.id;
    RETURN QUERY SELECT 'INVALID'::text, NULL::bigint, NULL::text, NULL::text, false;
END $$;

CREATE FUNCTION mes_change_password(p_login text, p_old_hash text, p_new_full_hash text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = {S}, pg_temp AS $$
DECLARE n int;
BEGIN
    UPDATE app_user a SET password_hash = p_new_full_hash, must_change_pwd = false
    WHERE a.login = p_login AND a.is_active AND split_part(a.password_hash, '$', 4) = p_old_hash;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n = 1;
END $$;
