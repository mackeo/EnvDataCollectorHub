using System.Configuration;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using Dapper;
using Dapper.Contrib.Extensions;

namespace EnvDataCollector.Data
{
    public static class DbHelper
    {
        private static readonly string _cs;

        static DbHelper()
        {
            string file = ConfigurationManager.AppSettings["DbPath"] ?? "EnvDataCollector.db";
            string dir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            _cs = $"Data Source={Path.Combine(dir, file)};Version=3;Journal Mode=WAL;Foreign Keys=True;";

            // SELECT 侧：snake_case 列名 → PascalCase 属性自动映射
            // 例：device_code → DeviceCode，password_enc → PasswordEnc
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            // Dapper.Contrib 说明：
            //   GetAll<T>() / Get<T>(id) → 生成 SELECT *，MatchNamesWithUnderscores 负责映射 ✅
            //   Delete(entity)           → 只用主键 WHERE id=@Id，SQLite 大小写不敏感 ✅
            //   Insert(entity)           → 生成 INSERT INTO t (PropName,...) 用属性名作列名
            //                             SQLite 区分下划线，DeviceCode ≠ device_code ❌
            //   Update(entity)           → 同上 ❌
            // 因此 Insert/Update 保留显式 SQL，调用参数 @PropName 与属性名精确匹配，
            // Dapper 参数绑定不依赖列名，写入完全正常。
        }

        public static IDbConnection Open()
        {
            var c = new SQLiteConnection(_cs);
            c.Open();
            return c;
        }
    }
}
