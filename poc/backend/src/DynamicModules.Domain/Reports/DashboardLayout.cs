using System.Text.Json;

namespace DynamicModules.Domain.Reports;

/// <summary>
/// 儀表板版面（存進 Dashboard.LayoutJson 與 DashboardPersonalVersion.LayoutJson，camelCase）。指派版、個人版、自己的儀表板共用這份格式。
/// 格子座標 X、Y、W、H 的單位是版面的欄與列（寬 12 欄）；Viz 是 table|indicator|line|bar|donut，屬於卡片，與報表預設的呈現方式無關。
/// </summary>
public record DashboardLayout(List<DashboardCard> Cards)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static DashboardLayout Empty => new([]);

    public static string Serialize(DashboardLayout layout) => JsonSerializer.Serialize(layout, Options);

    /// <summary>缺 cards 當空版面，卡片缺 options 補預設值。</summary>
    public static DashboardLayout Parse(string json)
    {
        var layout = JsonSerializer.Deserialize<DashboardLayout>(json, Options) ?? throw new JsonException("儀表板版面不可為 null。");
        return layout.Cards is null
            ? Empty
            : new DashboardLayout(layout.Cards.Select(c => (object?)c.Options is null ? c with { Options = CardOptions.None } : c).ToList());
    }

    public IReadOnlySet<long> ReportIds() => Cards.Select(c => c.ReportId).ToHashSet();

    public DashboardLayout WithoutReport(long reportId) => new(Cards.Where(c => c.ReportId != reportId).ToList());

    /// <summary>重設每張卡片的 <see cref="DashboardCard.FromAssigned"/>；client 送來的值一律不採用，由伺服器依來源決定。</summary>
    public DashboardLayout WithFromAssigned(Func<DashboardCard, bool> decide) => new(Cards.Select(c => c with { FromAssigned = decide(c) }).ToList());

    public DashboardLayout WithoutFromAssigned() => WithFromAssigned(_ => false);
}

/// <summary>
/// FromAssigned 只由伺服器決定（spec §7）：個人版上這張卡片的報表來自指派版（放上時在指派版面內，或之前已帶此旗標），
/// 所以指派版後來移除這張報表，個人版保留的卡片仍可載入，不需要另外分享。使用者自己靠分享加入的卡片永遠是 false。
/// 存檔時一律由伺服器覆寫，client 送的值無效。
/// </summary>
public record DashboardCard(string Id, long ReportId, string? Title, string Viz, int X, int Y, int W, int H, CardOptions Options, bool FromAssigned = false);

/// <summary>
/// 卡片自己的選項（spec §4）。Range 是卡片自己的日期區間；TopN 不併「其他」；Cumulative 只限長條；ComparePeriods 只限指標卡且報表要有主要日期欄位；
/// LinkDashboardId 是點擊後前往的另一張儀表板。SortKey 是量值 Id 或 <see cref="ReportCalculator.KeyOf"/> 格式的欄位鍵。
/// </summary>
public record CardOptions(
    ReportDateRange? Range, string? SortKey, bool SortDesc, int? TopN, bool Cumulative, int? ComparePeriods, long? LinkDashboardId)
{
    public static readonly CardOptions None = new(null, null, false, null, false, null, null);
}
