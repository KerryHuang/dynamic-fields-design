using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>報表與儀表板的上限（政策 A24 的全系統生效值）：下鑽筆數、明細列數、日期區間與「近 N 天」的天數。</summary>
public sealed record ReportLimits(int DrillLimit, int DetailLimit, int MaxRangeDays);

/// <summary>讀 <see cref="ReportLimits"/>；靜態的 ReportRules、DashboardRules 不碰 IO，由呼叫端讀好傳進去。</summary>
public sealed class ReportLimitReader(IPolicyReader policies)
{
    public async Task<ReportLimits> GetAsync(CancellationToken ct) => new(
        await policies.IntAsync(PolicyCatalog.ReportDrillLimit, null, ct),
        await policies.IntAsync(PolicyCatalog.ReportDetailLimit, null, ct),
        await policies.IntAsync(PolicyCatalog.ReportMaxRangeDays, null, ct));

    /// <summary>每個儀表板的卡片數上限（政策 dashboard.maxCards）。</summary>
    public Task<int> MaxCardsAsync(CancellationToken ct) => policies.IntAsync(PolicyCatalog.DashboardMaxCards, null, ct);
}
