using System.Data.Common;
using System.Globalization;
using Dapper;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.Dynamic.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ReportQueryRepository(
    DynamicDbContext db, ISqlDialect dialect, IReportSqlSnapshot snapshot, IDatabaseErrors errors, ReportOptions options,
    ILogger<ReportQueryRepository> logger) : IReportQueryRepository
{
    private static readonly int[] Caps = [1_000, 100_000];

    private TimeSpan Timeout => TimeSpan.FromSeconds(options.SqlTimeoutSeconds);

    /// <summary>快照內 Dapper 命令的用戶端逾時：讀快照設定的 EF 值（<see cref="IReportSqlSnapshot"/> 的約定）。</summary>
    private int? CommandTimeout => db.Database.GetCommandTimeout();

    /// <summary>
    /// 在讀取快照裡先重驗與探測，再跑 body（<see cref="IReportSqlSnapshot"/>）。
    /// 超過時間上限被中斷時（SQLite 的 SQLITE_INTERRUPT、PostgreSQL 的 57014，PostgreSQL provider 設計 §5.7）回
    /// <see cref="ReportSqlOutcome.TimeoutReason"/>；請求本身被取消則照常丟 <see cref="OperationCanceledException"/>。
    /// 指定的索引不存在時的退回由 SQLite 的快照處理（<see cref="SqliteReportSqlSnapshot"/>）。
    /// </summary>
    private async Task<string?> RunInSnapshotAsync(
        IReadOnlyList<ReportSqlConfigStamp> stamps, IReadOnlyList<ReportSqlCheck> checks, ReportSqlScope scope,
        Func<DbConnection, DbTransaction, Task> body, CancellationToken ct)
    {
        try
        {
            return await snapshot.RunAsync(db, Timeout, async (connection, tx) =>
            {
                if (await ReportSqlSnapshot.VerifyAsync(db, connection, tx, stamps, checks, scope, dialect, ct) is { } reason) return reason;
                await body(connection, tx);
                return null;
            }, ct);
        }
        catch (Exception e) when (errors.IsQueryCanceled(e))
        {
            ct.ThrowIfCancellationRequested();
            logger.LogWarning("報表 SQL 超過時間上限 {Seconds} 秒，已中斷", options.SqlTimeoutSeconds);
            return ReportSqlOutcome.TimeoutReason;
        }
    }

    public async Task<ReportSqlOutcome> AggregateAsync(ReportSqlQuery q, CancellationToken ct)
    {
        IReadOnlyList<ReportSqlRow> groups = [];
        ReportSqlRow? total = null;
        var reason = await RunInSnapshotAsync(q.Stamps, q.Checks, q.Main, async (connection, tx) =>
        {
            var driver = await ChooseDriverAsync(connection, tx, q.Filters, ct, CommandTimeout);
            if (q.Groups.Count > 0)
            {
                var built = ReportQueryBuilder.Aggregate(q, driver, total: false, dialect);
                groups = await ReadAsync(connection, tx, built, q, hasKeys: true, CommandTimeout, ct);
            }
            var totalSql = ReportQueryBuilder.Aggregate(q, driver, total: true, dialect);
            total = (await ReadAsync(connection, tx, totalSql, q, hasKeys: false, CommandTimeout, ct)).Single();
        }, ct);
        return reason is null ? new ReportSqlOutcome(null, groups, total) : ReportSqlOutcome.Fallback(reason);
    }

    public async Task<ReportNarrowedRecords> ListNarrowedAsync(
        ReportSqlScope main, ReportSqlNarrowing n, ReportSqlScope? child, long linkFieldId, int limit, CancellationToken ct)
    {
        IReadOnlyList<DynamicModules.Domain.Entities.Dynamic.Record> mains = [], kids = [];
        var reason = await RunInSnapshotAsync(n.Stamps, n.Checks, main, async (connection, tx) =>
        {
            var driver = await ChooseDriverAsync(connection, tx, n.Filters, ct, CommandTimeout);
            var built = ReportQueryBuilder.Narrow(main, n.Filters, driver, limit, dialect);
            mains = (await connection.QueryAsync<DynamicModules.Domain.Entities.Dynamic.Record>(new CommandDefinition(built.Sql, built.Parameters, tx, CommandTimeout, cancellationToken: ct))).ToList();
            if (child is null) return;
            var childBuilt = ReportQueryBuilder.NarrowChildren(main, n.Filters, driver, limit, child, linkFieldId, dialect);
            kids = (await connection.QueryAsync<DynamicModules.Domain.Entities.Dynamic.Record>(new CommandDefinition(childBuilt.Sql, childBuilt.Parameters, tx, CommandTimeout, cancellationToken: ct))).ToList();
        }, ct);
        return reason is null ? new ReportNarrowedRecords(null, mains, kids) : ReportNarrowedRecords.Fallback(reason);
    }

    /// <summary>依估計的命中筆數挑驅動條件（設計 §5.2）：封頂的索引區間計數，全碰頂再放大一次；沒有候選回 null（掃模組）。</summary>
    /// <param name="commandTimeout">Dapper 命令的用戶端逾時（秒）；null 用連線預設。</param>
    public static async Task<ReportSqlCondition?> ChooseDriverAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction tx, IReadOnlyList<ReportSqlCondition> filters, CancellationToken ct,
        int? commandTimeout = null)
    {
        var candidates = filters.Where(ReportQueryBuilder.CanDrive).OrderBy(c => c.Field.Kind == ReportSqlKind.Date ? 0 : 1).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var counts = new int[candidates.Count];
        foreach (var cap in Caps)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var built = ReportQueryBuilder.CappedCount(candidates[i], cap);
                counts[i] = await connection.ExecuteScalarAsync<int>(new CommandDefinition(built.Sql, built.Parameters, tx, commandTimeout, cancellationToken: ct));
            }
            if (counts.Any(c => c < cap)) break;
        }
        return candidates[Array.IndexOf(counts, counts.Min())];
    }

    private static async Task<IReadOnlyList<ReportSqlRow>> ReadAsync(
        System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction tx, ReportQueryBuilder.Built built,
        ReportSqlQuery q, bool hasKeys, int? commandTimeout, CancellationToken ct)
    {
        var rows = new List<ReportSqlRow>();
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(built.Sql, built.Parameters, tx, commandTimeout, cancellationToken: ct));
        while (await reader.ReadAsync(ct))
        {
            var i = 0;
            var keys = new List<object?>();
            if (hasKeys)
                foreach (var g in q.Groups)
                {
                    keys.Add(g.Kind == ReportSqlKind.Number ? (reader.IsDBNull(i) ? null : reader.GetInt64(i)) : reader.GetString(i));
                    i++;
                }
            var rowCount = reader.GetInt64(i++);
            var measures = new List<ReportSqlMeasureRaw>();
            foreach (var m in q.Measures)
            {
                long count = 0, hi = 0, lo = 0, hit = 0;
                long? min = null, max = null;
                DateTime? minDate = null, maxDate = null;
                switch (m.Kind)
                {
                    case "sum" or "avg":
                        count = reader.GetInt64(i++);
                        hi = reader.IsDBNull(i) ? 0 : reader.GetInt64(i); i++;
                        lo = reader.IsDBNull(i) ? 0 : reader.GetInt64(i); i++;
                        break;
                    case "min" or "max":
                        if (m.Field!.Kind == ReportSqlKind.Date)
                        {
                            var d = reader.IsDBNull(i) ? (DateTime?)null : ParseDate(reader.GetValue(i));
                            if (m.Kind == "min") minDate = d; else maxDate = d;
                        }
                        else
                        {
                            var v = reader.IsDBNull(i) ? (long?)null : reader.GetInt64(i);
                            if (m.Kind == "min") min = v; else max = v;
                        }
                        i++;
                        break;
                    case "ratio" or "countIf":
                        hit = reader.IsDBNull(i) ? 0 : reader.GetInt64(i);
                        i++;
                        break;
                }
                measures.Add(new ReportSqlMeasureRaw(count, hi, lo, min, max, minDate, maxDate, hit));
            }
            rows.Add(new ReportSqlRow(keys, rowCount, measures));
        }
        return rows;
    }

    private static DateTime ParseDate(object value) =>
        value as DateTime? ?? DateTime.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.None);
}
