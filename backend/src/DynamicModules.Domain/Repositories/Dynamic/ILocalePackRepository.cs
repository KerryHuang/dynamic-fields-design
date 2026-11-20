using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 覆寫層版本（<c>Dynamic_LocalePack</c>）與預覽暫存（<c>Dynamic_LocalePackPreview</c>）（語系包設計 §6.3、§6.4；i4d1 計畫判斷 9）。
/// 只改 tracking 的方法由呼叫端接著 <see cref="IUnitOfWork.CommitAsync"/>，在交易內。
/// </summary>
public interface ILocalePackRepository
{
    /// <summary>所有語系的目前版；不追蹤，依 <c>Code</c> 排序。</summary>
    Task<List<LocalePack>> ListCurrentAsync(CancellationToken ct);

    /// <summary>該語系的目前版（追蹤）。</summary>
    Task<LocalePack?> CurrentAsync(string code, CancellationToken ct);

    /// <summary>該語系的上一版（追蹤）。</summary>
    Task<LocalePack?> PreviousAsync(string code, CancellationToken ct);

    /// <summary>
    /// 寫入新的目前版：刪 <paramref name="previous"/>、把 <paramref name="current"/> 標成上一版、加入 <paramref name="next"/>。
    /// 匯入、移除覆寫（空版本）、回到上一版（上一版內容的複本）都走它；只改 tracking。不提供對調：對調不換 <c>Id</c>，版本 token 會重複。
    /// </summary>
    void AddVersion(LocalePack next, LocalePack? current, LocalePack? previous, DateTime now);

    /// <summary>刪該語系的所有版本與預覽（移除語系用，直接執行，在交易內呼叫）。</summary>
    Task DeleteForLocaleAsync(string code, CancellationToken ct);

    void AddPreview(LocalePackPreview p);

    /// <summary>依 Id 取預覽（追蹤；過期與擁有者由呼叫端判斷）。</summary>
    Task<LocalePackPreview?> FindPreviewAsync(string id, CancellationToken ct);

    void RemovePreview(LocalePackPreview p);

    /// <summary>刪掉過期的預覽，再把超過 <paramref name="keep"/> 筆的最舊的刪掉（直接執行，在交易內呼叫）。</summary>
    Task PrunePreviewsAsync(DateTime now, int keep, CancellationToken ct);
}
