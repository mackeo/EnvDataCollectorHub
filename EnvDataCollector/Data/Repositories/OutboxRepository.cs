using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class OutboxRepository
    {
        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        public void Enqueue(string messageType, string url, string payloadJson,
            string relatedTable = null, long? relatedId = null, int maxRetry = 10)
        {
            string now = Now;
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                INSERT INTO push_outbox
                    (message_type, target_url, payload_json, status,
                     retry_count, max_retry, next_retry_time,
                     related_table, related_id, created_at, updated_at)
                VALUES
                    (@messageType, @url, @payloadJson, 'Pending',
                     0, @maxRetry, @now,
                     @relatedTable, @relatedId, @now, @now)",
                new { messageType, url, payloadJson, maxRetry, now, relatedTable, relatedId });
        }

        public IEnumerable<OutboxMessageEntity> DequeueDue(int take = 20)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Query<OutboxMessageEntity>(@"
                SELECT * FROM push_outbox
                WHERE  status IN ('Pending','Failed')
                  AND  retry_count < max_retry
                  AND  (next_retry_time IS NULL OR next_retry_time <= @now)
                ORDER  BY created_at ASC
                LIMIT  @take", new { now = Now, take });
        }

        /// <summary>分页查询（带可选时间范围）</summary>
        public IEnumerable<OutboxMessageEntity> GetPage(
            string status = null, DateTime? from = null, DateTime? to = null, int limit = 500)
        {
            using IDbConnection db = DbHelper.Open();
            string sql = "SELECT * FROM push_outbox WHERE 1=1";
            if (status != null) sql += " AND status=@status";
            if (from.HasValue)  sql += " AND created_at >= @f";
            if (to.HasValue)    sql += " AND created_at <= @t";
            sql += " ORDER BY created_at DESC LIMIT @limit";
            return db.Query<OutboxMessageEntity>(sql, new {
                status, limit,
                f = from?.ToString("yyyy-MM-dd HH:mm:ss"),
                t = to?.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }

        public void MarkSuccess(long id)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("UPDATE push_outbox SET status='Success', updated_at=@now WHERE id=@id",
                new { id, now = Now });
        }

        public void MarkFailed(long id, int? httpCode, string error, int retryCount, DateTime nextRetry)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                UPDATE push_outbox SET
                    status='Failed', retry_count=@retryCount,
                    last_http_code=@httpCode, last_error=@error,
                    next_retry_time=@nrt, updated_at=@now
                WHERE id=@id",
                new {
                    id, retryCount, httpCode, error,
                    nrt = nextRetry.ToString("yyyy-MM-dd HH:mm:ss"),
                    now = Now
                });
        }

        public void ResetToPending(long id)
        {
            string now = Now;
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                UPDATE push_outbox SET
                    status='Pending', retry_count=0,
                    next_retry_time=@now, updated_at=@now
                WHERE id=@id", new { id, now });
        }

        /// <summary>更新 payload JSON（补推图片 URL 后刷新）</summary>
        public void UpdatePayload(long id, string payloadJson)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("UPDATE push_outbox SET payload_json=@p, updated_at=@now WHERE id=@id",
                new { id, p = payloadJson, now = Now });
        }

        public (long failed, long pending) GetCounts()
        {
            using IDbConnection db = DbHelper.Open();
            long f = db.QuerySingle<long>("SELECT COUNT(*) FROM push_outbox WHERE status='Failed'");
            long p = db.QuerySingle<long>("SELECT COUNT(*) FROM push_outbox WHERE status='Pending'");
            return (f, p);
        }

        public DateTime? GetOldestPendingTime()
        {
            using IDbConnection db = DbHelper.Open();
            string v = db.QueryFirstOrDefault<string>(
                "SELECT MIN(created_at) FROM push_outbox WHERE status='Pending'");
            return v != null && DateTime.TryParse(v, out var dt) ? dt : (DateTime?)null;
        }

        public void DeleteSuccessOlderThan(DateTime cutoff)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("DELETE FROM push_outbox WHERE status='Success' AND updated_at < @c",
                new { c = cutoff.ToString("yyyy-MM-dd HH:mm:ss") });
        }
    }
}
