using System.Data.Common;
using Dapper;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DynamicModules.Persistence.Dynamic.Query;

/// <summary>
/// 報表 SQL 路徑的讀取快照（設計 §8.4）：在同一個讀取交易內先重驗規劃時的欄位設定，再做資料狀態探測，最後才跑 body。
/// WAL 模式下延遲開始的交易在第一次讀取時固定快照，且不取寫入鎖；欄位更新與索引重建在同一個交易提交，
/// 所以快照內看到的欄位設定與索引必定同版。已有外層交易（例如寫入流程裡呼叫）時直接沿用。
/// </summary>
internal static class ReportSqlSnapshot
{
    /// <returns>放棄 SQL 的原因；成功跑完 body 回 null。</returns>
    public static async Task<string?> RunAsync(
        DynamicDbContext db, IReadOnlyList<ReportSqlConfigStamp> stamps, IReadOnlyList<ReportSqlCheck> checks, ReportSqlScope scope,
        Func<SqliteConnection, DbTransaction, Task> body, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        SqliteTransaction? own = null;
        try
        {
            var tx = db.Database.CurrentTransaction?.GetDbTransaction();
            if (tx is null)
            {
                own = connection.BeginTransaction(deferred: true);
                tx = own;
                await db.Database.UseTransactionAsync(own, ct);
            }

            // 第一個語句：重讀欄位設定，同時固定快照
            var ids = stamps.Select(s => s.FieldId).ToList();
            var fields = await db.Set<Field>().AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(ct);
            foreach (var s in stamps)
                if (fields.FirstOrDefault(f => f.Id == s.FieldId) is not { } f || ReportFieldStamp.Of(f) != s.Stamp)
                    return $"configChanged:{s.FieldId}";

            foreach (var check in checks)
            {
                var probe = ReportQueryBuilder.Probe(check, scope);
                if (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(probe.Sql, probe.Parameters, tx, cancellationToken: ct)) is not null)
                    return check.Kind == ReportSqlCheckKind.Number ? $"scaledUnavailable:{check.FieldId}" : $"textTruncated:{check.FieldId}";
            }

            await body(connection, tx);
            return null;
        }
        finally
        {
            if (own is not null)
            {
                await db.Database.UseTransactionAsync(null, ct);
                await own.DisposeAsync();
            }
            await db.Database.CloseConnectionAsync();
        }
    }
}
