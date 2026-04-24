using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class CameraConfigRepository
    {
        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        public IEnumerable<CameraConfigEntity> GetAll(bool enabledOnly = false)
        {
            using IDbConnection db = DbHelper.Open();
            return enabledOnly
                ? db.Query<CameraConfigEntity>(
                    "SELECT * FROM camera_config WHERE enabled=1 ORDER BY id")
                : db.Query<CameraConfigEntity>(
                    "SELECT * FROM camera_config ORDER BY id");
        }

        public CameraConfigEntity GetById(int id)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Get<CameraConfigEntity>(id);
        }

        public CameraConfigEntity GetByDevice(int deviceId)
        {
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<CameraConfigEntity>(
                "SELECT * FROM camera_config WHERE device_id=@deviceId", new { deviceId });
        }

        public void Delete(int id)
        {
            using IDbConnection db = DbHelper.Open();
            db.Execute("DELETE FROM camera_config WHERE id=@id", new { id });
        }

        // Upsert（ON CONFLICT）：无 Contrib 等效，保留显式 SQL
        public void Upsert(CameraConfigEntity e)
        {
            string now = Now;
            if (e.CreatedAt == null) e.CreatedAt = now;
            e.UpdatedAt = now;
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                INSERT INTO camera_config
                    (device_id, ip, port, username, password_enc, channel, enabled,
                     match_pre_sec, match_post_sec, image_store_path, image_base_url,
                     created_at, updated_at)
                VALUES
                    (@DeviceId, @Ip, @Port, @Username, @PasswordEnc, @Channel, @Enabled,
                     @MatchPreSec, @MatchPostSec, @ImageStorePath, @ImageBaseUrl,
                     @CreatedAt, @UpdatedAt)
                ON CONFLICT(device_id) DO UPDATE SET
                    ip=excluded.ip, port=excluded.port,
                    username=excluded.username, password_enc=excluded.password_enc,
                    channel=excluded.channel, enabled=excluded.enabled,
                    match_pre_sec=excluded.match_pre_sec, match_post_sec=excluded.match_post_sec,
                    image_store_path=excluded.image_store_path,
                    image_base_url=excluded.image_base_url,
                    updated_at=excluded.updated_at", e);
        }
    }
}
