using DynamicModules.Domain.Data;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Postgres;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// SaveChanges 閘門的釋放（PostgreSQL provider 設計 §5.6、§9 pg0b 實作補記）：EF 的 <c>SavingChanges</c> 攔截器與 <c>DetectChanges</c>
/// 在 SaveChanges 的 try 之外，這兩處丟例外時 EF 不發任何結束事件；閘門仍要放開，否則同行程後續寫入全部等到逾時。
/// 例外發生在開連線之前，不需要 PostgreSQL 伺服器：連線字串指向不存在的主機，只當閘門的鍵。
/// </summary>
[Trait("Category", "Postgres")]
public class PostgresSaveChangesGateTests
{
    private readonly string _connectionString = $"Host=127.0.0.1;Port=1;Database=gate_{Guid.NewGuid():N}";

    private sealed class ThrowingSavingInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new InvalidOperationException("後面的攔截器失敗");

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("後面的攔截器失敗");
    }

    private DynamicDbContext NewContext(params IInterceptor[] after)
    {
        var builder = new DbContextOptionsBuilder<DynamicDbContext>();
        PostgresDatabase.Configure(builder, _connectionString, new PostgresWriteSettings(1));
        builder.AddInterceptors(after);
        return new DynamicDbContext(builder.Options);
    }

    private static Task Save(DynamicDbContext db, bool async)
    {
        if (async) return db.SaveChangesAsync();
        db.SaveChanges();
        return Task.CompletedTask;
    }

    /// <summary>閘門放開了：另一個寫入者立刻取得（逾時 1 秒內）。</summary>
    private async Task GateShouldBeFree()
    {
        var enter = () => WriteGate.ForKey(_connectionString).EnterAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        using var permit = (await enter.Should().NotThrowAsync("前一次 SaveChanges 的前置例外不能把閘門占住")).Subject;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSavingChangesInterceptorThrows_ReleasesGate(bool async)
    {
        await using var db = NewContext(new ThrowingSavingInterceptor());

        var act = () => Save(db, async);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("後面的攔截器失敗");
        await GateShouldBeFree();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectChangesThrows_ReleasesGate(bool async)
    {
        await using var db = NewContext();
        db.ChangeTracker.DetectingAllChanges += (_, _) => throw new InvalidOperationException("偵測變更失敗");

        var act = () => Save(db, async);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("偵測變更失敗");
        await GateShouldBeFree();
    }
}
