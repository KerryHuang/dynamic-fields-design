using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>語系快照的版本戳記：筆數與最大修改時間（語系包設計 §4.2；i4a 計畫判斷 3）。空表是 <c>(0, null)</c>。</summary>
public sealed record LocaleVersionStamp(int Count, DateTime? MaxModifiedUtc);

/// <summary>已安裝語系（<c>Dynamic_Locale</c>）。</summary>
public interface ILocaleRepository
{
    /// <summary>不追蹤；內建在前，再依建立時間、代碼排序。</summary>
    Task<List<InstalledLocale>> ListAsync(CancellationToken ct);

    Task<LocaleVersionStamp> StampAsync(CancellationToken ct);
}
