using System.Diagnostics;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Postgres;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// PostgreSQL 的全庫寫入鎖（PostgreSQL provider 設計 §5.6）：另一條連線（模擬另一個實例）占住 advisory lock 時，
/// 只發一個指令的 SaveChanges 也要等鎖（AutoTransactionBehavior=Always），逾時轉成 DatabaseBusyException、交易回滾、連線可再用；
/// 同步與非同步兩條路徑。需要 PostgreSQL 伺服器（FORMWRIGHT_TEST_PG）；pg0c 接上測試容器後補閘門與池的案例。
/// </summary>
[Trait("Category", "Postgres")]
public class PostgresWriteLockTests : IAsyncLifetime
{
    private const int LockTimeoutSeconds = 1;
    private readonly string _database = $"fw_lock_{Guid.NewGuid():N}";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        if (PostgresFactAttribute.ConnectionString is not { } server) return;
        await using (var admin = new NpgsqlConnection(server))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        _connectionString = new NpgsqlConnectionStringBuilder(server) { Database = _database }.ConnectionString;
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (PostgresFactAttribute.ConnectionString is not { } server || _connectionString == "") return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(server);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private DynamicDbContext NewContext() =>
        new(PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(), _connectionString,
            new PostgresWriteSettings(LockTimeoutSeconds)).Options as DbContextOptions<DynamicDbContext> ?? throw new InvalidOperationException());

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
}
