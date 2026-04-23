using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class RunRecordRepository
    {
        public long Insert(RunRecordEntity e)
        {
            e.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            using IDbConnection db = DbHelper.Open();
            return db.QuerySingle<long>(@"
                INSERT INTO run_record
                    (device_id, device_type, device_code, start_time, end_time,
                     run_time_sec, currents, water_pressure, flow_quantity,
                     currents_max, currents_min, currents_median,
                     water_pressure_max, water_pressure_min, water_pressure_median,
                     flow_quantity_max, flow_quantity_min, flow_quantity_median,
                     vehicle_no,
                     vehicle_pic_local, vehicle_no_pic_local,
                     vehicle_pic, vehicle_no_pic,
                     close_reason, push_status, push_error, created_at)
                VALUES
                    (@DeviceId, @DeviceType, @DeviceCode, @StartTime, @EndTime,
                     @RunTimeSec, @Currents, @WaterPressure, @FlowQuantity,
                     @CurrentsMax, @CurrentsMin, @CurrentsMedian,
                     @WaterPressureMax, @WaterPressureMin, @WaterPressureMedian,
                     @FlowQuantityMax, @FlowQuantityMin, @FlowQuantityMedian,
                     @VehicleNo,
                     @VehiclePicLocal, @VehicleNoPicLocal,
                     @VehiclePic, @VehicleNoPic,
                     @CloseReason, @PushStatus, @PushError, @CreatedAt);
                SELECT last_insert_rowid();", e);
        }

        public RunRecordEntity GetById(long id)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Get<RunRecordEntity>(id);
        }

        public IEnumerable<RunRecordEntity> Query(DateTime from, DateTime to,
            string deviceCode = null, string pushStatus = null, string vehicleNo = null)
        {
            using IDbConnection db = DbHelper.Open();
            var sql = "SELECT * FROM run_record WHERE start_time BETWEEN @f AND @t";
            if (!string.IsNullOrEmpty(deviceCode)) sql += " AND device_code=@deviceCode";
            if (!string.IsNullOrEmpty(pushStatus))  sql += " AND push_status=@pushStatus";
            if (!string.IsNullOrEmpty(vehicleNo))   sql += " AND vehicle_no LIKE @vehicleNo";
            sql += " ORDER BY start_time DESC LIMIT 500";
            return db.Query<RunRecordEntity>(sql, new {
                f          = from.ToString("yyyy-MM-dd HH:mm:ss"),
                t          = to.ToString("yyyy-MM-dd HH:mm:ss"),
                deviceCode,
                pushStatus,
                vehicleNo  = string.IsNullOrEmpty(vehicleNo) ? null : $"%{vehicleNo}%"
            });
        }

        /// <summary>回填远程图片 URL（图片上传接口返回后）</summary>
        public void UpdateImageUrls(long id, string vehiclePic, string vehicleNoPic)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"UPDATE run_record SET vehicle_pic=@vp, vehicle_no_pic=@np WHERE id=@id",
                new { id, vp = vehiclePic, np = vehicleNoPic });
        }

        public void MarkSuccess(long id)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("UPDATE run_record SET push_status='Success', push_error=NULL WHERE id=@id",
                new { id });
        }

        public void MarkFailed(long id, string error)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("UPDATE run_record SET push_status='Failed', push_error=@error WHERE id=@id",
                new { id, error });
        }

        public void MarkSuccessAdmin(long id, string note)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("UPDATE run_record SET push_status='Success', push_error=@note WHERE id=@id",
                new { id, note = "[管理员] " + note });
        }

        public void DeleteSuccessOlderThan(DateTime cutoff)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("DELETE FROM run_record WHERE push_status='Success' AND created_at < @c",
                new { c = cutoff.ToString("yyyy-MM-dd HH:mm:ss") });
        }
    }
}
