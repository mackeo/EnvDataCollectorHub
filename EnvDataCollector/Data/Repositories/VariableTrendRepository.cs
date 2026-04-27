using System;
using System.Collections.Generic;
using System.Data;
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
