using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>
/// 記憶體裡的覆寫層版本與預覽暫存：<see cref="Rows"/> 照寫照讀（<c>Id</c> 自增不重用）、計次。<see cref="AddVersion"/> 直接改列表（沒有 tracking），
/// <see cref="Set"/> 是測試造資料的捷徑：把該語系的目前版換成新的一列。
/// </summary>
public sealed class FakeLocalePackRepository : ILocalePackRepository
{
    private static readonly DateTime T0 = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
    private long _nextId;

    public List<LocalePack> Rows { get; } = [];
    public List<LocalePackPreview> Previews { get; } = [];
    public int ListCurrentCalls;
    public Exception? Throw;

    /// <summary>把 <paramref name="code"/> 的目前版換成新的一列（舊的成為上一版），回新列。</summary>
    public LocalePack Set(string code, string uiJson = "{}", string serverJson = "{}")
    {
        var next = new LocalePack { Code = code, UiJson = uiJson, ServerJson = serverJson, ImportedUtc = T0, ModifiedUtc = T0 };
        lock (Rows) AddVersionLocked(next, Rows.FirstOrDefault(r => r.Code == code && r.IsCurrent), Rows.FirstOrDefault(r => r.Code == code && !r.IsCurrent), T0);
        return next;
    }

    public Task<List<LocalePack>> ListCurrentAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ListCurrentCalls);
        if (Throw is not null) throw Throw;
        lock (Rows) return Task.FromResult(Rows.Where(r => r.IsCurrent).OrderBy(r => r.Code, StringComparer.Ordinal).ToList());
    }

    public Task<LocalePack?> CurrentAsync(string code, CancellationToken ct)
    {
        lock (Rows) return Task.FromResult(Rows.FirstOrDefault(r => r.Code == code && r.IsCurrent));
    }

    public Task<LocalePack?> PreviousAsync(string code, CancellationToken ct)
    {
        lock (Rows) return Task.FromResult(Rows.FirstOrDefault(r => r.Code == code && !r.IsCurrent));
    }

    public void AddVersion(LocalePack next, LocalePack? current, LocalePack? previous, DateTime now)
    {
        lock (Rows) AddVersionLocked(next, current, previous, now);
    }

    private void AddVersionLocked(LocalePack next, LocalePack? current, LocalePack? previous, DateTime now)
    {
        if (previous is not null) Rows.Remove(previous);
        if (current is not null) { current.IsCurrent = false; current.ModifiedUtc = now; }
        next.Id = ++_nextId;
        next.IsCurrent = true;
        Rows.Add(next);
    }

    public Task DeleteForLocaleAsync(string code, CancellationToken ct)
    {
        lock (Rows) Rows.RemoveAll(r => r.Code == code);
        lock (Previews) Previews.RemoveAll(p => p.Code == code);
        return Task.CompletedTask;
    }

    public void AddPreview(LocalePackPreview p) { lock (Previews) Previews.Add(p); }

    public Task<LocalePackPreview?> FindPreviewAsync(string id, CancellationToken ct)
    {
        lock (Previews) return Task.FromResult(Previews.FirstOrDefault(p => p.Id == id));
    }

    public void RemovePreview(LocalePackPreview p) { lock (Previews) Previews.Remove(p); }

    public Task PrunePreviewsAsync(DateTime now, int keep, CancellationToken ct)
    {
        lock (Previews)
        {
            Previews.RemoveAll(p => p.ExpiresUtc <= now);
            foreach (var p in Previews.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id, StringComparer.Ordinal).Skip(Math.Max(keep, 0)).ToList())
                Previews.Remove(p);
        }
        return Task.CompletedTask;
    }

    /// <summary>給 <see cref="FakeLocaleRepository.StampAsync"/>：筆數與最大 Id。</summary>
    internal (int Count, long? MaxId) Stamp()
    {
        lock (Rows) return (Rows.Count, Rows.Count == 0 ? null : Rows.Max(r => r.Id));
    }
}
