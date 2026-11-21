using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Common.Audit;

/// <summary>
/// 稽核寫入介面（維運設計 §17.2.1、§17.8 A09、A10）：三個寫法的失敗語意、交易守衛在建 scope 之前、行為者覆寫的守衛。
/// 交易與另開 scope 寫入在 PostgreSQL 上行為不同（同一請求另開寫入交易會等自己的鎖），所以雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public class AuditLogTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private ServiceProvider _sp = null!;
    private ServiceProvider _failing = null!;
    private readonly FakeUser _user = new();
    private readonly ListLogger _log = new();

    private sealed class FakeUser : ICurrentUser { public string UserId { get; set; } = ""; }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _sp = Build(failing: false);
        _failing = Build(failing: true);
    }

    private ServiceProvider Build(bool failing)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        TestDb.AddProviderServices(services);
        var options = (DbContextOptions<DynamicDbContext>)_db.Context.GetService<IDbContextOptions>();
        services.AddScoped(_ => new DynamicDbContext(options));
        services.AddSingleton<ICurrentUser>(_user);
        if (failing) services.AddScoped<IAuditLogRepository, ThrowingRepository>();
        return services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _failing.DisposeAsync();
        await _db.DisposeAsync();
    }

    private sealed class ThrowingRepository : IAuditLogRepository
    {
        public void Add(AuditLogEntry entry) => throw new InvalidOperationException("稽核表寫不進去（測試注入）");
        public Task<List<AuditLogEntry>> PageAsync(AuditLogQuery query, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<AuditLogEntry?> GetAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountSinceAsync(string action, DateTime sinceUtc, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountForTokenSinceAsync(long tokenId, DateTime sinceUtc, IReadOnlyCollection<string> actions, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>計數的 scope factory：驗證交易守衛在建立新 scope 之前（A09）。</summary>
    private sealed class CountingScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created;
        public IServiceScope CreateScope() { Created++; return inner.CreateScope(); }
    }

    private sealed class ListLogger : ILogger<AuditLog>
    {
        public List<(LogLevel Level, Exception? Error)> Records { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Records.Add((logLevel, exception));
    }

    /// <summary>在 provider 的一個 scope 裡組 AuditLog，scope factory 換成計數的。</summary>
    private (AuditLog Log, CountingScopes Scopes, ITransactionManager Tx, IServiceScope Scope) NewLog(ServiceProvider sp)
    {
        var scope = sp.CreateScope();
        var p = scope.ServiceProvider;
        var counting = new CountingScopes(p.GetRequiredService<IServiceScopeFactory>());
        var tx = p.GetRequiredService<ITransactionManager>();
        return (new AuditLog(p.GetRequiredService<IAuditLogRepository>(), _user, new NoRequestOrigin(), TimeProvider.System, tx, counting, _log),
            counting, tx, scope);
    }

    private static AuditEntry Entry(ActorOverride? actor = null) =>
        new("login.failed", AuditResult.Denied, AuditTargets.User, "sales01", ReasonCode: "wrongPassword", ActorOverride: actor);

    private async Task<List<AuditLogEntry>> Rows()
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task WriteNow_OutsideTransaction_WritesInOwnScope()
    {
        var (log, scopes, _, scope) = NewLog(_sp);
        using (scope) await log.WriteNowAsync(Entry(), CancellationToken.None);
        scopes.Created.Should().Be(1);
        var row = (await Rows()).Should().ContainSingle().Subject;
        row.Action.Should().Be("login.failed");
        row.ActorUserId.Should().BeNull();
        row.ActorKind.Should().Be(AuditActorKind.System, "沒有身分的寫入記「系統」（B3）");
        row.Channel.Should().Be(AuditChannel.Ui);
        row.RiskLevel.Should().Be(AuditActions.Find("login.failed")!.Risk);
        row.OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task WriteNow_WhenWriteFails_ThrowsAuditWriteException_TryWriteNowLogsAndReturnsFalse()
    {
        var (log, _, _, scope) = NewLog(_failing);
        using (scope)
        {
            await log.Invoking(l => l.WriteNowAsync(Entry(), CancellationToken.None)).Should().ThrowAsync<AuditWriteException>();
            (await log.TryWriteNowAsync(Entry(), CancellationToken.None)).Should().BeFalse();
        }
        _log.Records.Should().ContainSingle(r => r.Level == LogLevel.Error).Which.Error.Should().BeOfType<InvalidOperationException>();
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task InsideTransaction_BothWriteNowMethodsThrow_BeforeCreatingAScope()
    {
        var (log, scopes, tx, scope) = NewLog(_sp);
        using (scope)
        {
            await using var handle = await tx.BeginAsync(CancellationToken.None);
            await log.Invoking(l => l.WriteNowAsync(Entry(), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
            // TryWriteNowAsync 也丟、不回 false（呼叫端的程式錯誤，不是寫不進去）
            await log.Invoking(l => l.TryWriteNowAsync(Entry(), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
            await handle.RollbackAsync(CancellationToken.None);
        }
        scopes.Created.Should().Be(0, "守衛在建立新 scope 之前");
        _log.Records.Should().BeEmpty();
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task ActorOverride_WithIdentityOutsideSignIn_Throws_AndWritesNothing()
    {
        _user.UserId = "admin";
        var (log, scopes, _, scope) = NewLog(_sp);
        using (scope)
        {
            log.Invoking(l => l.Add(Entry(new ActorOverride("sales01")))).Should().Throw<InvalidOperationException>();
            await log.Invoking(l => l.WriteNowAsync(Entry(new ActorOverride("sales01")), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
        }
        scopes.Created.Should().Be(0);
        (await Rows()).Should().BeEmpty();
    }

    [Fact]
    public async Task ActorOverride_WithoutIdentity_Allowed_SignInScopeClosedToOtherCallers()
    {
        var (log, _, _, scope) = NewLog(_sp);
        using (scope)
        {
            // 沒有身分：可以覆寫
            await log.WriteNowAsync(Entry(new ActorOverride("sales01")), CancellationToken.None);
            // 登入範圍內（環境有身分也不取）只有兩個登入 handler 開得了，由 WebAPITests 的 A11 經真的登入驗證；這裡只驗測試碼開不了
            _user.UserId = "admin";
            FluentActions.Invoking(AuditOrigin.BeginSignIn).Should().Throw<InvalidOperationException>();
            AuditOrigin.InSignIn.Should().BeFalse();
            // 範圍外、有身分：記環境的身分
            await log.WriteNowAsync(Entry(), CancellationToken.None);
        }
        (await Rows()).Select(r => (r.ActorUserId, r.ActorKind)).Should().Equal(
            ("sales01", AuditActorKind.User), ("admin", AuditActorKind.User));
    }

    [Fact]
    public async Task Origin_SuppliesChannelTokenAndActorKind()
    {
        _user.UserId = "svc01";
        var (log, _, _, scope) = NewLog(_sp);
        using (scope)
        using (AuditOrigin.Begin(AuditChannel.Mcp, tokenId: 42, actorKind: AuditActorKind.OpsService))
            await log.WriteNowAsync(Entry(), CancellationToken.None);
        var row = (await Rows()).Single();
        (row.Channel, row.TokenId, row.ActorKind, row.ActorUserId).Should().Be((AuditChannel.Mcp, 42L, AuditActorKind.OpsService, "svc01"));
        AuditOrigin.Channel.Should().Be(AuditChannel.Ui, "Dispose 還原外層");
    }

    [Fact]
    public void UnregisteredAction_Throws()
    {
        var (log, _, _, scope) = NewLog(_sp);
        using (scope) log.Invoking(l => l.Add(new AuditEntry("nope.never", AuditResult.Success))).Should().Throw<InvalidOperationException>();
    }
}
