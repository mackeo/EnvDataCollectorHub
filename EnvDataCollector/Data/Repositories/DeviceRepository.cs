using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class DeviceRepository
    {
        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        public IEnumerable<DeviceEntity> GetAll(bool enabledOnly = false)
        {
            using IDbConnection db = DbHelper.Open();
            // enabledOnly 需要 WHERE 条件，Contrib GetAll 不支持过滤，保留显式 SQL
            if (!enabledOnly)
                return db.GetAll<DeviceEntity>();

            return db.Query<DeviceEntity>("SELECT * FROM device WHERE enabled=1 ORDER BY id");
        }

        public DeviceEntity GetById(int id)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Get<DeviceEntity>(id);
        }

        public DeviceEntity GetByCode(string code)
        {
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<DeviceEntity>(
                "SELECT * FROM device WHERE device_code=@code", new { code });
        }

        public int CountVariables(int deviceId)
        {
            using IDbConnection db = DbHelper.Open();
            return db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM device_variable WHERE device_id=@deviceId",
                new { deviceId });
        }

        public int CountCameras(int deviceId)
        {
            using IDbConnection db = DbHelper.Open();
            return db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM camera_config WHERE device_id=@deviceId",
                new { deviceId });
        }

        public int Insert(DeviceEntity e)
        {
            e.CreatedAt = e.UpdatedAt = Now;
            using IDbConnection db = DbHelper.Open();
            return db.QuerySingle<int>(@"
                INSERT INTO device
                    (device_type, device_code, device_name, server_id, enabled, created_at, updated_at)
                VALUES
                    (@DeviceType, @DeviceCode, @DeviceName, @ServerId, @Enabled, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();", e);
        }

        public void Update(DeviceEntity e)
        {
            e.UpdatedAt = Now;
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                UPDATE device SET
                    device_type=@DeviceType, device_code=@DeviceCode,
                    device_name=@DeviceName, server_id=@ServerId,
                    enabled=@Enabled, updated_at=@UpdatedAt
                WHERE id=@Id", e);
        }

        public void Delete(int id)
        {
            using IDbConnection db = DbHelper.Open();
            db.Delete(new DeviceEntity { Id = id });
        }

        // 级联删除：device + device_variable + camera_config（同事务，保证一致性）
        // 不删 run_record / device_snapshot / plate_event 等历史数据，避免破坏审计留痕
        public void DeleteCascade(int id)
        {
            using IDbConnection db = DbHelper.Open();
            using IDbTransaction tx = db.BeginTransaction();
            db.Execute("DELETE FROM device_variable WHERE device_id=@id", new { id }, tx);
            db.Execute("DELETE FROM camera_config   WHERE device_id=@id", new { id }, tx);
            db.Execute("DELETE FROM device          WHERE id=@id",        new { id }, tx);
            tx.Commit();
        }
    }
}
