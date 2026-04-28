using System.Data;
using Dapper;

namespace EnvDataCollector.Data
{
    public static class DatabaseInitializer
    {
        public static void Initialize()
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(Ddl);
            try { db.Execute(MigrateSql); } catch { }
        }

        private const string Ddl = @"
PRAGMA foreign_keys = ON;
PRAGMA journal_mode  = WAL;

CREATE TABLE IF NOT EXISTS opcua_server (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL,
    endpoint_url    TEXT    NOT NULL,
    security_mode   TEXT    NOT NULL DEFAULT 'None',
    security_policy TEXT    NOT NULL DEFAULT 'None',
    auth_type       TEXT    NOT NULL DEFAULT 'Anonymous',
    username        TEXT,
    password_enc    TEXT,
    enabled         INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS device (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    device_type  TEXT    NOT NULL,
    device_code  TEXT    NOT NULL,
    device_name  TEXT    NOT NULL,
    server_id    INTEGER NOT NULL,
    enabled      INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL,
    UNIQUE(device_code)
    -- FOREIGN KEY(server_id) REFERENCES opcua_server(id)  -- 外键已注释
);

CREATE TABLE IF NOT EXISTS device_variable (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id    INTEGER NOT NULL,
    var_role     TEXT    NOT NULL,
    node_id      TEXT    NOT NULL,
    display_name TEXT,
    data_type    TEXT,
    sampling_ms  INTEGER NOT NULL DEFAULT 1000,
    enabled      INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL,
    UNIQUE(device_id, var_role)
    -- FOREIGN KEY(device_id) REFERENCES device(id)  -- 外键已注释
);

CREATE TABLE IF NOT EXISTS camera_config (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id        INTEGER NOT NULL UNIQUE,
    ip               TEXT    NOT NULL,
    port             INTEGER NOT NULL DEFAULT 8000,
    username         TEXT    NOT NULL,
    password_enc     TEXT    NOT NULL,
    channel          INTEGER NOT NULL DEFAULT 1,
    enabled          INTEGER NOT NULL DEFAULT 1,
    match_pre_sec    INTEGER NOT NULL DEFAULT 30,
    match_post_sec   INTEGER NOT NULL DEFAULT 120,
    image_store_path TEXT    NOT NULL,
    image_base_url   TEXT    NOT NULL,
    created_at       TEXT    NOT NULL,
    updated_at       TEXT    NOT NULL
    -- FOREIGN KEY(device_id) REFERENCES device(id)  -- 外键已注释
);

CREATE TABLE IF NOT EXISTS plate_event (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id       INTEGER NOT NULL,
    plate_no        TEXT,
    event_time      TEXT    NOT NULL,
    confidence      REAL,
    vehicle_pic_url TEXT,
    plate_pic_url   TEXT,
    vehicle_pic_local TEXT,
    plate_pic_local   TEXT,
    raw_json        TEXT,
    created_at      TEXT    NOT NULL
    -- FOREIGN KEY(device_id) REFERENCES device(id)  -- 外键已注释
);

CREATE TABLE IF NOT EXISTS variable_trend (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id   INTEGER NOT NULL,
    variable_id INTEGER NOT NULL,
    node_id     TEXT    NOT NULL,
    var_role    TEXT    NOT NULL,
    value_str   TEXT,
    source_time TEXT    NOT NULL,
    created_at  TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS device_snapshot (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id      INTEGER NOT NULL,
    time           TEXT    NOT NULL,
    online         INTEGER,
    startup        INTEGER,
    currents       REAL,
    water_pressure REAL,
    flow_quantity  REAL,
    push_status    TEXT    NOT NULL DEFAULT 'Pending',
    push_error     TEXT,
    created_at     TEXT    NOT NULL
    -- FOREIGN KEY(device_id) REFERENCES device(id)  -- 外键已注释
);

CREATE TABLE IF NOT EXISTS run_record (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id      INTEGER NOT NULL,
    device_type    TEXT    NOT NULL,
    device_code    TEXT    NOT NULL,
    start_time     TEXT    NOT NULL,
    end_time       TEXT    NOT NULL,
    run_time_sec   INTEGER NOT NULL,
    currents       REAL,
    water_pressure REAL,
    flow_quantity  REAL,
    currents_max           REAL,
    currents_min           REAL,
    currents_avg           REAL,
    currents_median        REAL,
    water_pressure_max     REAL,
    water_pressure_min     REAL,
    water_pressure_avg     REAL,
    water_pressure_median  REAL,
    flow_quantity_max      REAL,
    flow_quantity_min      REAL,
    flow_quantity_avg      REAL,
    flow_quantity_median   REAL,
    vehicle_no     TEXT,
    vehicle_pic    TEXT,
    vehicle_no_pic TEXT,
    vehicle_pic_local      TEXT,
    vehicle_no_pic_local   TEXT,
    close_reason   TEXT,
    push_status    TEXT    NOT NULL DEFAULT 'Pending',
    push_error     TEXT,
    created_at     TEXT    NOT NULL
    -- FOREIGN KEY(device_id) REFERENCES device(id)  -- 外键已注释
);

CREATE TABLE IF NOT EXISTS push_outbox (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    message_type    TEXT    NOT NULL,
    target_url      TEXT    NOT NULL,
    payload_json    TEXT    NOT NULL,
    status          TEXT    NOT NULL DEFAULT 'Pending',
    retry_count     INTEGER NOT NULL DEFAULT 0,
    max_retry       INTEGER NOT NULL DEFAULT 10,
    next_retry_time TEXT,
    last_http_code  INTEGER,
    last_error      TEXT,
    related_table   TEXT,
    related_id      INTEGER,
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS app_setting (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_plate_time   ON plate_event(device_id, event_time);
CREATE INDEX IF NOT EXISTS idx_snap_time    ON device_snapshot(time);
CREATE INDEX IF NOT EXISTS idx_trend_dev_time ON variable_trend(device_id, source_time);
CREATE INDEX IF NOT EXISTS idx_trend_var_time ON variable_trend(variable_id, source_time);
CREATE INDEX IF NOT EXISTS idx_run_time     ON run_record(start_time);
CREATE INDEX IF NOT EXISTS idx_outbox_retry ON push_outbox(status, next_retry_time);
CREATE INDEX IF NOT EXISTS idx_outbox_cre   ON push_outbox(created_at);
";

        private const string MigrateSql = @"
ALTER TABLE run_record ADD COLUMN currents_avg           REAL;
ALTER TABLE run_record ADD COLUMN water_pressure_avg     REAL;
ALTER TABLE run_record ADD COLUMN flow_quantity_avg      REAL;
";
    }
}