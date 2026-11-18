using System.Diagnostics;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Postgres;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// PostgreSQL 的全庫寫入鎖（PostgreSQL provider 設計 §5.6）：另一條連線（模擬另一個實例）占住 advisory lock 時，
/// 只發一個指令的 SaveChanges 也要等鎖（AutoTransactionBehavior=Always），逾時轉成 DatabaseBusyException、交易回滾、連線可再用；
/// 同步與非同步兩條路徑；同一實例同時進來的多個寫入在程序內排隊，等 advisory lock 的連線最多一條（設計 §6.4）。
/// 池耗盡回 503 的案例在 <see cref="PostgresPoolExhaustionTests"/>。只在 PostgreSQL 模式跑（FORMWRIGHT_TEST_DB=postgres）。
/// </summary>
[Trait("Category", "Postgres")]
public class PostgresWriteLockTests : IAsyncLifetime
{
    private const int LockTimeoutSeconds = 1;
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        if (TestDatabase.IsPostgres) _connectionString = await PostgresTestServer.CreateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connectionString != "") await PostgresTestServer.DropDatabaseAsync(_connectionString);
    }

    private DynamicDbContext NewContext(int lockTimeoutSeconds = LockTimeoutSeconds) =>
        new(PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(), _connectionString,
            new PostgresWriteSettings(lockTimeoutSeconds)).Options as DbContextOptions<DynamicDbContext> ?? throw new InvalidOperationException());

    /// <summary>另一條連線取同一把鎖（session 層級），Dispose 時放開（不進池：回到池裡的連線 session 鎖不會放開）。</summary>
    private async Task<NpgsqlConnection> HoldWriteLockAsync()
    {
        var other = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString);
        await other.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_lock({PostgresLockKeys.Write})", other);
        await command.ExecuteNonQueryAsync();
        return other;
    }

    private static SystemSetting Setting(string key) => new() { Key = key, Value = "v" };

    [PostgresFact]
    public async Task SingleCommandSaveChanges_WaitsForWriteLock_ThenBusy_AndConnectionIsReusable()
    {
        await using var db = NewContext();
        await using (await HoldWriteLockAsync())
        {
            db.Add(Setting("lock.async"));
            var watch = Stopwatch.StartNew();
            var act = () => db.SaveChangesAsync();

            await act.Should().ThrowAsync<DatabaseBusyException>();
            watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(LockTimeoutSeconds * 0.8), "要等到 lock_timeout 才放棄");
            db.Database.CurrentTransaction.Should().BeNull("取鎖失敗的交易要自己回滾、不留給 EF");
        }

        // 鎖放開後同一個 context（同一個池）可以照常寫入，前一次沒有留下開著的交易
        await db.SaveChangesAsync();
        await using var check = NewContext();
        (await check.Set<SystemSetting>().CountAsync(s => s.Key == "lock.async")).Should().Be(1);
    }

    [PostgresFact]
    public async Task SyncSaveChanges_AlsoTakesWriteLock()
    {
        using var db = NewContext();
        await using (await HoldWriteLockAsync())
        {
            db.Add(Setting("lock.sync"));
            var act = () => db.SaveChanges();

            act.Should().Throw<DatabaseBusyException>();
        }

        db.SaveChanges();
        db.Set<SystemSetting>().Count(s => s.Key == "lock.sync").Should().Be(1);
    }

    [PostgresFact]
    public async Task TransactionManager_WaitsForWriteLock_ThenBusy()
    {
        await using var db = NewContext();
        var transactions = new PostgresTransactionManager(db, new PostgresWriteSettings(LockTimeoutSeconds));
        await using (await HoldWriteLockAsync())
        {
            var act = () => transactions.BeginAsync(CancellationToken.None);

            await act.Should().ThrowAsync<DatabaseBusyException>();
        }

        await using var tx = await transactions.BeginAsync(CancellationToken.None);
        db.Add(Setting("lock.tx"));
        await db.SaveChangesAsync();
        await tx.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// 閘門的驗收（設計 §5.6、§6.4）：外部連線占住全庫鎖時，同一實例 N 個寫入同時進來，pg_stat_activity 裡等 advisory lock 的連線
    /// 最多一條，其餘在程序內的 WriteGate 排隊、不占池連線；鎖放開後全部依序完成。
    /// </summary>
    [PostgresFact]
    public async Task ConcurrentWrites_SameInstance_AtMostOneConnectionWaitsForAdvisoryLock()
    {
        const int writers = 8;
        var database = new NpgsqlConnectionStringBuilder(_connectionString).Database!;
        var holder = await HoldWriteLockAsync();
        var contexts = Enumerable.Range(0, writers).Select(_ => NewContext(lockTimeoutSeconds: 30)).ToArray();
        Task[] writes;
        var maxWaiting = 0;
        try
        {
            writes = contexts.Select((db, i) =>
            {
                db.Add(Setting($"lock.many.{i}"));
                return db.SaveChangesAsync();
            }).ToArray();

            await using var monitor = new NpgsqlConnection(await PostgresTestServer.AdminConnectionStringAsync());
            await monitor.OpenAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND wait_event_type = 'Lock' AND wait_event = 'advisory'", monitor);
            count.Parameters.AddWithValue("db", database);
            // 取樣一秒：寫入都已發出，第一個在等鎖、其餘應該在閘門排隊
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                maxWaiting = Math.Max(maxWaiting, (int)(long)(await count.ExecuteScalarAsync())!);
                await Task.Delay(20);
            }
            writes.Should().OnlyContain(t => !t.IsCompleted, "鎖還在外部連線手上");
        }
        finally
        {
            await holder.DisposeAsync();
        }

        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(30));
        maxWaiting.Should().Be(1, "同一實例的寫入先在 WriteGate 排隊，只有排到的那一個占著連線等 advisory lock");
        foreach (var db in contexts) await db.DisposeAsync();
        await using var check = NewContext();
        (await check.Set<SystemSetting>().CountAsync(s => s.Key.StartsWith("lock.many."))).Should().Be(writers);
    }
}
