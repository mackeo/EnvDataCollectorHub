using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using Dapper.Contrib.Extensions;
using EnvDataCollector.Models;

namespace EnvDataCollector.Data.Repositories
{
    public class OpcUaServerRepository
    {
        private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // ── 读取：Contrib GetAll / Get，SELECT * 由 MatchNamesWithUnderscores 映射 ──

        public IEnumerable<OpcUaServerEntity> GetAll()
        {
            using IDbConnection db = DbHelper.Open();
            return db.GetAll<OpcUaServerEntity>();
        }

        public OpcUaServerEntity GetById(int id)
        {
            using IDbConnection db = DbHelper.Open();
            return db.Get<OpcUaServerEntity>(id);
        }

        // ── 写入：显式 SQL，@ParamName 与属性名匹配，Dapper 参数绑定不依赖列名 ──

        public int Insert(OpcUaServerEntity e)
        {
            e.CreatedAt = e.UpdatedAt = Now;
            using IDbConnection db = DbHelper.Open();
            return db.QuerySingle<int>(@"
                INSERT INTO opcua_server
                    (name, endpoint_url, security_mode, security_policy,
                     auth_type, username, password_enc, enabled, created_at, updated_at)
                VALUES
                    (@Name, @EndpointUrl, @SecurityMode, @SecurityPolicy,
                     @AuthType, @Username, @PasswordEnc, @Enabled, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();", e);
        }

        public void Update(OpcUaServerEntity e)
        {
            e.UpdatedAt = Now;
            using IDbConnection db = DbHelper.Open();
            db.Execute(@"
                UPDATE opcua_server SET
                    name=@Name, endpoint_url=@EndpointUrl,
                    security_mode=@SecurityMode, security_policy=@SecurityPolicy,
                    auth_type=@AuthType, username=@Username, password_enc=@PasswordEnc,
                    enabled=@Enabled, updated_at=@UpdatedAt
                WHERE id=@Id", e);
        }

        // ── 删除：Contrib Delete，只需主键 WHERE id=@Id ──

        public void Delete(int id)
        {
            using IDbConnection db = DbHelper.Open();
            db.Delete(new OpcUaServerEntity { Id = id });
        }
    }
}
