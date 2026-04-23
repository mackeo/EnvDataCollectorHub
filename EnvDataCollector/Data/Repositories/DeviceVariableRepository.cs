using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class DeviceVariableRepository
    {
        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // 按设备查询：带 WHERE 条件，使用显式 SQL
        public IEnumerable<DeviceVariableEntity> GetByDevice(int deviceId)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Query<DeviceVariableEntity>(
                "SELECT * FROM device_variable WHERE device_id=@deviceId ORDER BY var_role",
                new { deviceId });
        }

        public DeviceVariableEntity GetByRole(int deviceId, string varRole)
        {
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<DeviceVariableEntity>(
                "SELECT * FROM device_variable WHERE device_id=@deviceId AND var_role=@varRole",
                new { deviceId, varRole });
        }

        // Upsert：无标准 Contrib 支持，保留显式 SQL
        public void Upsert(DeviceVariableEntity e)
        {
            string now = Now;
            if (e.CreatedAt == null) e.CreatedAt = now;
            e.UpdatedAt = now;
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                INSERT INTO device_variable
                    (device_id, var_role, node_id, display_name, data_type,
                     sampling_ms, enabled, created_at, updated_at)
                VALUES
                    (@DeviceId, @VarRole, @NodeId, @DisplayName, @DataType,
                     @SamplingMs, @Enabled, @CreatedAt, @UpdatedAt)
                ON CONFLICT(device_id, var_role) DO UPDATE SET
                    node_id=excluded.node_id, display_name=excluded.display_name,
                    data_type=excluded.data_type, sampling_ms=excluded.sampling_ms,
                    enabled=excluded.enabled, updated_at=excluded.updated_at", e);
        }

        public void DeleteByDevice(int deviceId)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("DELETE FROM device_variable WHERE device_id=@deviceId", new { deviceId });
        }
    }
}
