using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Dapper;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class VariableTrendStats
    {
        public double? Max;
        public double? Min;
        public double? Median;
        public double? Last;
    }

    public class VariableTrendRepository
    {
        public long Insert(VariableTrendEntity e)
        {
            e.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            using IDbConnection db = DbHelper.Open();
            return db.QuerySingle<long>(@"
                INSERT INTO variable_trend
                    (device_id, variable_id, node_id, var_role, value_str, source_time, created_at)
                VALUES
                    (@DeviceId, @VariableId, @NodeId, @VarRole, @ValueStr, @SourceTime, @CreatedAt);
                SELECT last_insert_rowid();", e);
        }

        /// <summary>批量 INSERT，单事务包多条 VALUES，性能比循环单插高十倍以上。</summary>
        public int InsertBatch(IList<VariableTrendEntity> items)
        {
            if (items == null || items.Count == 0) return 0;
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var e in items)
                if (string.IsNullOrEmpty(e.CreatedAt)) e.CreatedAt = now;

            using IDbConnection db = DbHelper.Open();
            using var tx = db.BeginTransaction();
            try
            {
                int affected = db.Execute(@"
                    INSERT INTO variable_trend
                        (device_id, variable_id, node_id, var_role, value_str, source_time, created_at)
                    VALUES
                        (@DeviceId, @VariableId, @NodeId, @VarRole, @ValueStr, @SourceTime, @CreatedAt)",
                    items, transaction: tx);
                tx.Commit();
                return affected;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        /// <summary>RunRecordBuilder 用：增量取所有 var_role=Startup 的事件，按 id 升序。</summary>
        public IEnumerable<VariableTrendEntity> QueryStartupAfter(long lastId, int batchSize = 1000)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Query<VariableTrendEntity>(@"
                SELECT * FROM variable_trend
                WHERE id > @lastId AND var_role = @role
                ORDER BY id ASC
                LIMIT @batchSize",
                new { lastId, role = nameof(VarRole.Startup), batchSize });
        }

        /// <summary>程序重启时恢复"进行中"状态：取该设备最后一条 Startup 事件。</summary>
        public VariableTrendEntity GetLastStartup(int deviceId)
        {
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<VariableTrendEntity>(@"
                SELECT * FROM variable_trend
                WHERE device_id=@deviceId AND var_role=@role
                ORDER BY id DESC
                LIMIT 1",
                new { deviceId, role = nameof(VarRole.Startup) });
        }

        public VariableTrendStats GetStats(int deviceId, string varRole, DateTime from, DateTime to)
        {
            var p = new
            {
                deviceId,
                varRole,
                f = from.ToString("yyyy-MM-dd HH:mm:ss"),
                t = to.ToString("yyyy-MM-dd HH:mm:ss")
            };

            using IDbConnection db = DbHelper.Open();

            var row = db.QueryFirstOrDefault(@"
                SELECT
                    MAX(CAST(value_str AS REAL)) AS Max,
                    MIN(CAST(value_str AS REAL)) AS Min,
                    (SELECT CAST(value_str AS REAL) FROM variable_trend
                     WHERE device_id=@deviceId AND var_role=@varRole
                       AND source_time BETWEEN @f AND @t
                     ORDER BY source_time DESC, id DESC LIMIT 1) AS Last
                FROM variable_trend
                WHERE device_id=@deviceId AND var_role=@varRole
                  AND source_time BETWEEN @f AND @t", p);

            if (row == null || row.Max == null) return new VariableTrendStats();

            double? median = QueryMedian(db, deviceId, varRole, p.f, p.t);

            return new VariableTrendStats
            {
                Max    = row.Max,
                Min    = row.Min,
                Median = median,
                Last   = row.Last
            };
        }

        /// <summary>取该设备/角色的最新一条值（任意时间，给状态心跳取"当前值"用）。</summary>
        public VariableTrendEntity GetLatest(int deviceId, string varRole)
        {
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<VariableTrendEntity>(@"
                SELECT * FROM variable_trend
                WHERE device_id=@deviceId AND var_role=@varRole
                ORDER BY id DESC
                LIMIT 1",
                new { deviceId, varRole });
        }

        private static double? QueryMedian(IDbConnection db, int deviceId, string varRole, string f, string t)
        {
            var rows = db.Query<double?>(@"
                SELECT CAST(value_str AS REAL) FROM variable_trend
                WHERE device_id=@deviceId AND var_role=@varRole
                  AND source_time BETWEEN @f AND @t
                ORDER BY CAST(value_str AS REAL)",
                new { deviceId, varRole, f, t }).AsList();

            if (rows.Count == 0) return null;
            int n = rows.Count;
            int mid = n / 2;
            if (n % 2 == 1)
                return rows[mid];
            return (rows[mid - 1] + rows[mid]) / 2.0;
        }

        public IEnumerable<VariableTrendEntity> QueryRange(int deviceId, DateTime from, DateTime to)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Query<VariableTrendEntity>(@"
                SELECT * FROM variable_trend
                WHERE device_id=@deviceId AND source_time BETWEEN @f AND @t
                ORDER BY source_time ASC, id ASC",
                new {
                    deviceId,
                    f = from.ToString("yyyy-MM-dd HH:mm:ss"),
                    t = to.ToString("yyyy-MM-dd HH:mm:ss")
                });
        }

        public void DeleteOlderThan(DateTime cutoff)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(
                "DELETE FROM variable_trend WHERE created_at < @c",
                new { c = cutoff.ToString("yyyy-MM-dd HH:mm:ss") });
        }
    }
}
