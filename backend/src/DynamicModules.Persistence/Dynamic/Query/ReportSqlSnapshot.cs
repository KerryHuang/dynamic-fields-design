using System.Data.Common;
using Dapper;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Persistence.Dynamic.Dialects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Dynamic.Query;

/// <summary>
/// 報表 SQL 路徑的讀取快照（報表資料庫端聚合設計 §8.4）：在同一個讀取交易內先重驗規劃時的欄位設定，再做資料狀態探測，最後才跑 body。
/// 欄位更新與索引重建在同一個交易提交，所以快照內看到的欄位設定與索引必定同版。已有外層交易（例如寫入流程裡呼叫）時直接沿用。
/// 交易怎麼開、時間上限怎麼執行是 provider 專屬（<see cref="SqliteReportSqlSnapshot"/>；PostgreSQL 版在 Persistence.Postgres，
/// PostgreSQL provider 設計 §5.9）：逾時要以 <see cref="Application.Common.IDatabaseErrors.IsQueryCanceled"/> 認得的例外結束，
/// <c>ct</c> 取消時照常丟 <see cref="OperationCanceledException"/>。
/// </summary>
public interface IReportSqlSnapshot
{
    /// <param name="timeout">時間上限（SQLite 是整段，PostgreSQL 是每個陳述式，見各實作）。</param>
    /// <param name="body">第一個陳述式固定快照；回傳放棄 SQL 的原因，成功回 null。</param>
    Task<string?> RunAsync(DynamicDbContext db, TimeSpan timeout, Func<DbConnection, DbTransaction, Task<string?>> body, CancellationToken ct);
}

/// <summary>兩種 provider 共用的快照內前置檢查。</summary>
public static class ReportSqlSnapshot
{
    /// <summary>重讀欄位設定（快照內的第一個陳述式，同時固定快照），再跑資料狀態探測；有問題回放棄原因，否則 null。</summary>
    public static async Task<string?> VerifyAsync(
        DynamicDbContext db, DbConnection connection, DbTransaction tx, IReadOnlyList<ReportSqlConfigStamp> stamps,
        IReadOnlyList<ReportSqlCheck> checks, ReportSqlScope scope, ISqlDialect dialect, CancellationToken ct)
    {
        var ids = stamps.Select(s => s.FieldId).ToList();
        var fields = await db.Set<Field>().AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        foreach (var s in stamps)
            if (fields.FirstOrDefault(f => f.Id == s.FieldId) is not { } f || ReportFieldStamp.Of(f) != s.Stamp)
                return $"configChanged:{s.FieldId}";

        foreach (var check in checks)
        {
            var probe = ReportQueryBuilder.Probe(check, scope, dialect);
            if (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(probe.Sql, probe.Parameters, tx, cancellationToken: ct)) is not null)
                return check.Kind == ReportSqlCheckKind.Number ? $"scaledUnavailable:{check.FieldId}" : $"textTruncated:{check.FieldId}";
        }
        return null;
    }
}
