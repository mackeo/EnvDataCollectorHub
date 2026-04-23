using System;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class PlateEventRepository
    {
        public long Insert(PlateEventEntity e)
        {
            e.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            using IDbConnection db = DbHelper.Open();
            return db.QuerySingle<long>(@"
                INSERT INTO plate_event
                    (device_id, plate_no, event_time, confidence,
                     vehicle_pic_local, plate_pic_local,
                     vehicle_pic_url, plate_pic_url, raw_json, created_at)
                VALUES
                    (@DeviceId, @PlateNo, @EventTime, @Confidence,
                     @VehiclePicLocal, @PlatePicLocal,
                     @VehiclePicUrl, @PlatePicUrl, @RawJson, @CreatedAt);
                SELECT last_insert_rowid();", e);
        }

        public PlateEventEntity GetById(long id)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Get<PlateEventEntity>(id);
        }

        /// <summary>时间窗内按置信度最高、时间最近匹配</summary>
        public PlateEventEntity FindBestMatch(int deviceId, DateTime baseTime, int preSec, int postSec)
        {
            string from = baseTime.AddSeconds(-preSec).ToString("yyyy-MM-dd HH:mm:ss");
            string to   = baseTime.AddSeconds(postSec).ToString("yyyy-MM-dd HH:mm:ss");
            string bas  = baseTime.ToString("yyyy-MM-dd HH:mm:ss");
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<PlateEventEntity>(@"
                SELECT * FROM plate_event
                WHERE device_id=@deviceId AND event_time BETWEEN @from AND @to
                ORDER BY confidence DESC,
                         ABS(strftime('%s', event_time) - strftime('%s', @bas)) ASC
                LIMIT 1", new { deviceId, from, to, bas });
        }

        /// <summary>更新远程图片 URL（图片上传接口返回后回填）</summary>
        public void UpdateRemoteUrls(long id, string vehiclePicUrl, string platePicUrl)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"UPDATE plate_event SET vehicle_pic_url=@v, plate_pic_url=@p WHERE id=@id",
                new { id, v = vehiclePicUrl, p = platePicUrl });
        }

        public void DeleteOlderThan(DateTime cutoff)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("DELETE FROM plate_event WHERE created_at < @c",
                new { c = cutoff.ToString("yyyy-MM-dd HH:mm:ss") });
        }
    }
}
