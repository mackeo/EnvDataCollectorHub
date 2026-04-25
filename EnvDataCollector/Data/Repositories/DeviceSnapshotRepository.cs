using System;
using System.Collections.Generic;
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

        /// <summary>RunRecordBuilder 用：按 id 升序拿增量快照（多设备一次取齐）。</summary>
        public IEnumerable<DeviceSnapshotEntity> QueryAfter(long lastId, int batchSize = 1000)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Query<DeviceSnapshotEntity>(@"
                SELECT * FROM device_snapshot
                WHERE id > @lastId
                ORDER BY id ASC
                LIMIT @batchSize",
                new { lastId, batchSize });
        }

        /// <summary>程序重启时恢复"进行中"状态：取该设备 time &lt;= 给定时间的最新一条。</summary>
        public DeviceSnapshotEntity GetLastBefore(int deviceId, DateTime time)
        {
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<DeviceSnapshotEntity>(@"
                SELECT * FROM device_snapshot
                WHERE device_id=@deviceId AND time <= @t
                ORDER BY time DESC, id DESC
                LIMIT 1",
                new { deviceId, t = time.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        /// <summary>用于关闭 RunRecord 时累积窗口内的 currents/water/flow 求 max/min/median。</summary>
        public IEnumerable<DeviceSnapshotEntity> QueryRange(int deviceId, DateTime from, DateTime to)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Query<DeviceSnapshotEntity>(@"
                SELECT * FROM device_snapshot
                WHERE device_id=@deviceId AND time BETWEEN @f AND @t
                ORDER BY time ASC, id ASC",
                new {
                    deviceId,
                    f = from.ToString("yyyy-MM-dd HH:mm:ss"),
                    t = to.ToString("yyyy-MM-dd HH:mm:ss")
                });
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
