using System.Text.RegularExpressions;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.Dynamic.Query;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 報表資料庫端聚合設計 §6 保證方式第 2 項的 SQL 文字測試：報表測試裡每一次走到 SQL 路徑的計畫，
/// 先用 <see cref="ReportQueryBuilder"/> 產生所有可能的 SQL（每個候選驅動條件、分組與總計、縮小的主記錄與子記錄、探測），
/// 斷言每個 Dynamic_Record 別名都帶 ModuleId、DeleteFlag = 0 與該身分的範圍，值全在參數裡；通過才交給真正的 repository。
/// 掛在 <see cref="ReportTestBase"/> 上，所以權限矩陣的每個身分、每個情境都會檢查到。
/// </summary>
public sealed class ScopeCheckingReportQueries(ReportQueryRepository inner, ISqlDialect dialect) : IReportQueryRepository
{
    /// <summary>SQL 裡允許出現的字串常數：空字串、是否欄位的鍵、日期分桶的格式與修飾詞。條件值、擁有者 id 都必須是參數。</summary>
    private static readonly HashSet<string> AllowedLiterals = ["", "true", "false", "-3 days", "weekday 4", "%Y", "%Y-%m", "%j", "%m", "-W", "-Q", "%02d"];

    private static readonly Regex RecordAlias = new(@"\bDynamic_Record\b\s+(?:INDEXED BY \w+\s+)?(\w+)", RegexOptions.Compiled);
    private static readonly Regex Literal = new("'([^']*)'", RegexOptions.Compiled);

    /// <summary>本次測試有幾次計畫通過檢查並送進資料庫（聚合、縮小）。</summary>
    public int Checked { get; private set; }

    public Task<ReportSqlOutcome> AggregateAsync(ReportSqlQuery query, CancellationToken ct)
    {
        foreach (var driver in Drivers(query.Filters))
            foreach (var total in new[] { false, true })
                ShouldBeScoped(ReportQueryBuilder.Aggregate(query, driver, total, dialect), new() { ["r"] = query.Main });
        ProbesShouldBeScoped(query.Checks, query.Main);
        Checked++;
        return inner.AggregateAsync(query, ct);
    }

    public Task<ReportNarrowedRecords> ListNarrowedAsync(
        ReportSqlScope main, ReportSqlNarrowing narrowing, ReportSqlScope? child, long linkFieldId, int limit, CancellationToken ct)
    {
        foreach (var driver in Drivers(narrowing.Filters))
        {
            ShouldBeScoped(ReportQueryBuilder.Narrow(main, narrowing.Filters, driver, limit), new() { ["r"] = main });
            if (child is not null)
                ShouldBeScoped(ReportQueryBuilder.NarrowChildren(main, narrowing.Filters, driver, limit, child, linkFieldId),
                    new() { ["r"] = main, ["c"] = child });
        }
        ProbesShouldBeScoped(narrowing.Checks, main);
        Checked++;
        return inner.ListNarrowedAsync(main, narrowing, child, linkFieldId, limit, ct);
    }

    /// <summary>不用驅動（掃模組）加上每個可當驅動的條件：repository 執行時挑哪一個都要符合。</summary>
    private static IEnumerable<ReportSqlCondition?> Drivers(IReadOnlyList<ReportSqlCondition> filters) =>
        new ReportSqlCondition?[] { null }.Concat(filters.Where(ReportQueryBuilder.CanDrive));

    private static void ProbesShouldBeScoped(IReadOnlyList<ReportSqlCheck> checks, ReportSqlScope scope)
    {
        foreach (var check in checks)
            ShouldBeScoped(ReportQueryBuilder.Probe(check, scope), new() { ["r"] = scope });
    }

    private static void ShouldBeScoped(ReportQueryBuilder.Built built, Dictionary<string, ReportSqlScope> scopes)
    {
        var sql = built.Sql;
        var aliases = RecordAlias.Matches(sql).Select(m => m.Groups[1].Value).ToList();
        aliases.Should().NotBeEmpty(sql);
        foreach (var alias in aliases)
        {
            scopes.Should().ContainKey(alias, $"每個 Dynamic_Record 別名都要有已知的範圍：{sql}");
            var scope = scopes[alias];
            // 範圍片段必須連續出現、名單參數個數與範圍相同；後面不能接任何放寬的條件
            var owners = scope.OwnerUserIds switch
            {
                null => "",
                { Count: 0 } => " AND 1 = 0",
                { Count: var n } => $@" AND {alias}\.OwnerUserId IN \(@p\d+{string.Concat(Enumerable.Repeat(@", @p\d+", n - 1))}\)",
            };
            Regex.IsMatch(sql, $@"\b{alias}\.ModuleId = @p\d+ AND {alias}\.DeleteFlag = 0{owners}(?! OR )").Should().BeTrue($"{alias} 的範圍不完整（owners {scope.OwnerUserIds?.Count.ToString() ?? "null"}）：{sql}");
            if (scope.OwnerUserIds is null)
                sql.Should().NotContain($"{alias}.OwnerUserId", "範圍為全部時不加擁有者條件：" + sql);
        }
        sql.Should().NotContain(" OR ", "產生器不用 OR，出現就可能放寬範圍：" + sql);
        foreach (Match m in Literal.Matches(sql))
            AllowedLiterals.Should().Contain(m.Groups[1].Value, $"值必須走參數，不能寫進 SQL：{sql}");
        foreach (var name in built.Parameters.ParameterNames)
            sql.Should().Contain("@" + name, "每個參數都要被用到");
    }
}
