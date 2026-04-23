using System;
using System.Data;
using Dapper;

namespace EnvDataCollector.Data.Repositories
{
    public class AppSettingRepository
    {
        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        public string Get(string key, string def = null)
        {
            using IDbConnection db = DbHelper.Open();
            return db.QueryFirstOrDefault<string>(
                "SELECT value FROM app_setting WHERE key=@key", new { key }) ?? def;
        }

        public T Get<T>(string key, T def = default)
        {
            string v = Get(key);
            if (v == null) return def;
            try { return (T)Convert.ChangeType(v, typeof(T)); } catch { return def; }
        }

        public void Set(string key, string value)
        {
            string now = Now;
            using IDbConnection db = DbHelper.Open();
            db.Execute(
                "INSERT INTO app_setting(key,value,updated_at) VALUES(@key,@value,@now) " +
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at",
                new { key, value, now });
        }

        public void Set<T>(string key, T value) => Set(key, Convert.ToString(value));
    }

    public static class SK
    {
        public const string StatusApiUrl            = "StatusApiUrl";
        public const string EventApiUrl             = "EventApiUrl";
        public const string StatusUploadIntervalSec = "StatusUploadIntervalSec";
        public const string TokenEnabled            = "TokenEnabled";
        public const string TokenApiUrl             = "TokenApiUrl";
        public const string TokenUsername           = "TokenUsername";
        public const string TokenPassword           = "TokenPassword";
        public const string HttpTimeoutSec          = "HttpTimeoutSec";
        public const string RetryIntervalSec        = "RetryIntervalSec";
        public const string MaxRetryCount           = "MaxRetryCount";
        public const string RetryBatchSize          = "RetryBatchSize";
        public const string RetryBackoff            = "RetryBackoff";
        public const string CleanRetentionDays      = "CleanRetentionDays";
        public const string CleanIntervalHours      = "CleanIntervalHours";
        public const string ModbusEnabled           = "ModbusEnabled";
        public const string ModbusListenIp          = "ModbusListenIp";
        public const string ModbusListenPort        = "ModbusListenPort";
        public const string ModbusUnitId            = "ModbusUnitId";
        public const string ModbusHeartbeat         = "ModbusHeartbeatMode";
        public const string StaticFilePort          = "StaticFilePort";
        public const string PlateWaitMaxSec         = "PlateWaitMaxSec";
        public const string EventStatMode           = "EventStatMode";
        public const string ImageUploadUrl          = "ImageUploadUrl";     // ★ 图片上传接口 URL
    }
}
