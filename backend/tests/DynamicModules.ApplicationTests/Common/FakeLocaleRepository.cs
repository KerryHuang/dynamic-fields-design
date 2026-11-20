using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>
/// 記憶體裡的已安裝語系表：<see cref="Rows"/> 照寫照讀（戳記由它算），計次、可丟例外、<see cref="ListGate"/> 讓下一次 <see cref="ListAsync"/>
/// 先抓好當下的列、卡住到放行才回傳。<see cref="Packs"/> 是同一個 registry 用的覆寫層假表，戳記一起算它的筆數與最大 Id；
/// <see cref="PinStamp"/> 釘住戳記（測「戳記沒變但收到 Invalidate」）。
/// </summary>
public sealed class FakeLocaleRepository : ILocaleRepository
{
    private static readonly DateTime T0 = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    private int _clock;

    public List<InstalledLocale> Rows { get; } = [];
    public int StampCalls, ListCalls;
    public Exception? Throw;
    public TaskCompletionSource? ListGate;
    public FakeLocalePackRepository Packs { get; private set; } = new();
    public LocaleVersionStamp? PinStamp;

    /// <summary>加一列（修改時間每次往後一分鐘，戳記一定變）。</summary>
    public FakeLocaleRepository Add(string code, string nativeName, LocaleOrigin origin = LocaleOrigin.Installed, string? fallback = null)
    {
        var at = T0.AddMinutes(++_clock);
        lock (Rows) Rows.Add(new InstalledLocale { Code = code, NativeName = nativeName, Origin = origin, FallbackCode = fallback, CreatedUtc = at, ModifiedUtc = at });
        return this;
    }

    public void Remove(string code) { lock (Rows) Rows.RemoveAll(r => r.Code == code); }

    public Task<InstalledLocale?> FindAsync(string code, CancellationToken ct)
    {
        lock (Rows) return Task.FromResult(Rows.FirstOrDefault(r => r.Code == code));
    }

    void ILocaleRepository.Add(InstalledLocale row) { lock (Rows) Rows.Add(row); }

    void ILocaleRepository.Remove(InstalledLocale row) { lock (Rows) Rows.Remove(row); }

    public FakeLocaleRepository WithBuiltin()
    {
        foreach (var b in Domain.Localization.BuiltinLocales.All) Add(b.Code, b.NativeName, LocaleOrigin.Builtin);
        return this;
    }

    public async Task<List<InstalledLocale>> ListAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref ListCalls);
        List<InstalledLocale> snapshot;
        lock (Rows) snapshot = Rows.ToList();
        if (Interlocked.Exchange(ref ListGate, null) is { } gate) await gate.Task;
        if (Throw is not null) throw Throw;
        return snapshot;
    }

    public Task<LocaleVersionStamp> StampAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref StampCalls);
        if (Throw is not null) throw Throw;
        if (PinStamp is { } pinned) return Task.FromResult(pinned);
        var (packCount, packMaxId) = Packs.Stamp();
        lock (Rows) return Task.FromResult(new LocaleVersionStamp(Rows.Count, Rows.Count == 0 ? null : Rows.Max(r => r.ModifiedUtc), packCount, packMaxId));
    }

    /// <summary>用這個假表（與覆寫層假表 <paramref name="packs"/>，沒給就用 <see cref="Packs"/>）建一個 registry（scope 工廠只解析得到這兩個）。</summary>
    public LocaleRegistry Registry(TimeProvider time, ILogger<LocaleRegistry>? logger = null, FakeLocalePackRepository? packs = null)
    {
        if (packs is not null) Packs = packs;
        var services = new ServiceCollection();
        services.AddScoped<ILocaleRepository>(_ => this);
        services.AddScoped<ILocalePackRepository>(_ => Packs);
        return new LocaleRegistry(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), time,
            logger ?? NullLogger<LocaleRegistry>.Instance);
    }
}
