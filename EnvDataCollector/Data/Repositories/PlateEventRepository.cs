using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
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

        /// <summary>分页查询：按时间范围 + 可选设备 + 可选车牌模糊匹配</summary>
        public IEnumerable<PlateEventEntity> Query(
            DateTime from, DateTime to,
            int? deviceId, string plateLike,
            int limit = 500)
        {
            var sb = new StringBuilder(@"
                SELECT * FROM plate_event
                WHERE event_time BETWEEN @from AND @to");
            var p = new DynamicParameters();
            p.Add("from",  from.ToString("yyyy-MM-dd HH:mm:ss"));
            p.Add("to",    to.ToString("yyyy-MM-dd HH:mm:ss"));
            p.Add("limit", limit);

            if (deviceId.HasValue)
            {
                sb.Append(" AND device_id=@deviceId");
                p.Add("deviceId", deviceId.Value);
            }
            if (!string.IsNullOrEmpty(plateLike))
            {
                sb.Append(" AND plate_no LIKE @plate");
                p.Add("plate", "%" + plateLike + "%");
            }
            sb.Append(" ORDER BY id DESC LIMIT @limit");

            using IDbConnection db = DbHelper.Open();
            return db.Query<PlateEventEntity>(sb.ToString(), p);
        }

        public void Delete(long id)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("DELETE FROM plate_event WHERE id=@id", new { id });
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
