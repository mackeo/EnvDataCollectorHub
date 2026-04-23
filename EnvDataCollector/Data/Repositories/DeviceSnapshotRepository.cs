using System;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class DeviceSnapshotRepository
    {
        public long Insert(DeviceSnapshotEntity e)
        {
            e.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            using IDbConnection db = DbHelper.Open();
            return db.QuerySingle<long>(@"
                INSERT INTO device_snapshot
                    (device_id, time, online, startup, currents,
                     water_pressure, flow_quantity, push_status, push_error, created_at)
                VALUES
                    (@DeviceId, @Time, @Online, @Startup, @Currents,
                     @WaterPressure, @FlowQuantity, @PushStatus, @PushError, @CreatedAt);
                SELECT last_insert_rowid();", e);
        }

        public void MarkSuccess(long id)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(
                "UPDATE device_snapshot SET push_status='Success', push_error=NULL WHERE id=@id",
                new { id });
        }

        public void MarkFailed(long id, string error)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(
                "UPDATE device_snapshot SET push_status='Failed', push_error=@error WHERE id=@id",
                new { id, error });
        }

        public void DeleteSuccessOlderThan(DateTime cutoff)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(
                "DELETE FROM device_snapshot WHERE push_status='Success' AND created_at < @c",
                new { c = cutoff.ToString("yyyy-MM-dd HH:mm:ss") });
        }
    }
}
