using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DashboardService;

/// <summary>
/// 卡片查詢的共同流程：先驗觀看者看得到儀表板、卡片在他生效的版面內，再交給 <see cref="ReportRunner"/> 以觀看者的權限計算。
/// 不讓人拿任意報表 id 來跑；報表定義的可見性規則見 spec §7（指派版的卡片不需要另外分享，自己的儀表板要看得到定義）。
/// </summary>
public class DashboardCardRunner(DashboardAccess access, ReportAccess reportAccess, IReportRepository reports, ReportRunner runner)
{
    private const string UnsharedState = "unshared", ReportGoneState = "reportGone";

    public async Task<ServiceResult> RunAsync(
        long dashboardId, string cardId, ReportDateRange? filter, IReadOnlyList<string>? drill, string locale, CancellationToken ct)
    {
        if (ReportRules.CheckRange(filter) is { } rangeError) return ServiceResult.Invalid(ReportRules.ToErrors("filterRange", [rangeError]));
        var viewer = await reportAccess.ViewerAsync(ct);
        var scope = await access.FindAsync(dashboardId, viewer, ct);
        if (scope is null) return ServiceResult.NotFound(DashboardRules.NotFound);
        var card = scope.EffectiveLayout.Cards.FirstOrDefault(c => c.Id == cardId);
        if (card is null) return ServiceResult.NotFound("卡片不存在");

        var basis = await runner.BasisDateAsync(ct);
        ReportResult Fail(string state, string key) => new(state, ReportText.Get(locale, key), "", [], null, null, null, false, basis, null);
        var report = await reports.GetAsync(card.ReportId, ct);
        if (report is null) return Result(drill, Fail(ReportGoneState, ReportText.ReportGone));
        // 指派版給的卡片（在目前指派版面內，或個人版上伺服器標過來源）不看分享；其他卡片要這個人現在還看得到定義
        var fromAssigned = scope.IsFromAssigned(card, scope.AssignedReportIds());
        if (!fromAssigned && !ReportAccess.CanView(report, viewer)) return Result(drill, Fail(UnsharedState, ReportText.Unshared));

        return Result(drill, await ExecuteAsync(report, card, filter, drill, locale, ct));
    }

    /// <summary>
    /// 指派版編輯時的卡片預覽：卡片可能還沒存檔，所以不查儀表板；報表可見性與資料權限都用管理員自己的視角
    /// （和他自己開報表一樣，看不到定義回 unshared、沒有資料權限回 restricted），不因為是管理員就放行。
    /// </summary>
    public async Task<ServiceResult> PreviewAsync(DashboardCard card, ReportDateRange? filter, string locale, CancellationToken ct)
    {
        if (ReportRules.CheckRange(filter) is { } rangeError) return ServiceResult.Invalid(ReportRules.ToErrors("filterRange", [rangeError]));
        var viewer = await reportAccess.ViewerAsync(ct);
        var report = await reports.GetAsync(card.ReportId, ct);
        if (report is null) return Result(null, await FailAsync(ReportGoneState, ReportText.ReportGone, locale, ct));
        if (!ReportAccess.CanView(report, viewer)) return Result(null, await FailAsync(UnsharedState, ReportText.Unshared, locale, ct));
        return Result(null, await ExecuteAsync(report, card, filter, null, locale, ct));
    }

    private async Task<ReportResult> FailAsync(string state, string key, string locale, CancellationToken ct) =>
        new(state, ReportText.Get(locale, key), "", [], null, null, null, false, await runner.BasisDateAsync(ct), null);

    private async Task<ReportResult> ExecuteAsync(
        Domain.Entities.Dynamic.Report report, DashboardCard card, ReportDateRange? filter, IReadOnlyList<string>? drill, string locale, CancellationToken ct)
    {
        var definition = ReportDefinition.Parse(report.DefinitionJson);
        var o = card.Options;
        // 區間優先順序：卡片自己的 > 儀表板暫時篩選 > 報表的（不取交集）
        var range = o.Range ?? filter;
        ReportResult result;
        if (drill is not null)
            result = await runner.RunAsync(definition, new ReportRunOptions(range, drill, locale), ct);
        else
            result = await runner.RunCardAsync(definition, new ReportRunOptions(
                range, null, locale, o.TopN, o.SortKey, o.SortDesc, card.Viz == "bar" && o.Cumulative,
                card.Viz == "indicator" ? o.ComparePeriods : null), ct);

        // 畫面送了篩選但沒採用：卡片有自己的區間，或報表沒有主要日期欄位（篩選無從作用）
        var ignored = filter is not null && (o.Range is not null || definition.PrimaryDateField is null);
        return result with { IgnoresDateFilter = ignored };
    }

    private static ServiceResult Result(IReadOnlyList<string>? drill, ReportResult result)
    {
        if (drill is null) return ServiceResult.Success(ReportRules.CapDetail(result));
        if (result.DetailRows is not { Count: > ReportRules.DrillLimit } rows) return ServiceResult.Success(new DrillResult(result, false));
        return ServiceResult.Success(new DrillResult(result with { DetailRows = rows.Take(ReportRules.DrillLimit).ToList() }, true));
    }
}
