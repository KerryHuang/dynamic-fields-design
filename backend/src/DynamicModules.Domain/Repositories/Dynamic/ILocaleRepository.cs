using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 語系快照的版本戳記：語系表的筆數與最大修改時間（語系包設計 §4.2；i4a 計畫判斷 3），加上覆寫層版本表 <c>Dynamic_LocalePack</c> 的筆數與最大 <c>Id</c>
/// （i4d1 計畫判斷 9；<c>Id</c> 只增不重用，每次覆寫層寫入都會變）。空表是 <c>(0, null, 0, null)</c>。
/// </summary>
public sealed record LocaleVersionStamp(int Count, DateTime? MaxModifiedUtc, int PackCount, long? PackMaxId);

/// <summary>已安裝語系（<c>Dynamic_Locale</c>）。</summary>
public interface ILocaleRepository
{
    /// <summary>不追蹤；內建在前，再依建立時間、代碼排序。</summary>
    Task<List<InstalledLocale>> ListAsync(CancellationToken ct);

    Task<LocaleVersionStamp> StampAsync(CancellationToken ct);

    /// <summary>依代碼取一列（追蹤）。</summary>
    Task<InstalledLocale?> FindAsync(string code, CancellationToken ct);

    /// <summary>只改 tracking，呼叫端接著 <see cref="IUnitOfWork.CommitAsync"/>。</summary>
    void Add(InstalledLocale row);

    /// <summary>只改 tracking，呼叫端接著 <see cref="IUnitOfWork.CommitAsync"/>。</summary>
    void Remove(InstalledLocale row);
}
