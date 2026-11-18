using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Dapper;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Postgres;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// PostgreSQL 的報表讀取快照（PostgreSQL provider 設計 §5.9）：<c>REPEATABLE READ READ ONLY</c> 交易加 <c>statement_timeout</c>，
/// 逾時是 SQLSTATE 57014（<see cref="DatabaseErrors.IsQueryCanceled"/>，報表回「執行逾時」的既有路徑見 <c>ReportSqlEquivalenceTests</c>），
/// CancellationToken 直接中斷；不經寫入閘門與全庫鎖（§5.6 預留的判斷點）；在外層交易裡跑時不弄壞外層交易。
/// 只在 PostgreSQL 模式跑（FORMWRIGHT_TEST_DB=postgres）。
/// </summary>
[Trait("Category", "Postgres")]
public class PostgresReportSqlSnapshotTests : IAsyncLifetime
{
    private const int LockTimeoutSeconds = 1;
    private string _connectionString = "";
    private readonly PostgresReportSqlSnapshot _snapshot = new();

    public async Task InitializeAsync()
    {
        if (TestDatabase.IsPostgres) _connectionString = await PostgresTestServer.CreateDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connectionString != "") await PostgresTestServer.DropDatabaseAsync(_connectionString);
    }

    private DynamicDbContext NewContext() =>
        new(PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(), _connectionString,
            new PostgresWriteSettings(LockTimeoutSeconds)).Options as DbContextOptions<DynamicDbContext> ?? throw new InvalidOperationException());

    /// <summary>另一條連線（模擬另一個實例）占住全庫鎖，Dispose 時放開（不進池）。</summary>
    private async Task<NpgsqlConnection> HoldWriteLockAsync()
    {
        var other = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString);
        await other.OpenAsync();
        await other.ExecuteAsync($"SELECT pg_advisory_lock({PostgresLockKeys.Write})");
        return other;
    }

    private static async Task<string?> Scalar(DbConnection connection, DbTransaction tx, string sql) =>
        (await connection.ExecuteScalarAsync(sql, transaction: tx))?.ToString();

    [PostgresFact]
    public async Task Snapshot_IsRepeatableReadReadOnly_WithStatementTimeout()
    {
        await using var db = NewContext();
        string? isolation = null, readOnly = null, timeout = null;

        await _snapshot.RunAsync(db, TimeSpan.FromMilliseconds(2500), async (connection, tx) =>
        {
            isolation = await Scalar(connection, tx, "SHOW transaction_isolation");
            readOnly = await Scalar(connection, tx, "SHOW transaction_read_only");
            timeout = await Scalar(connection, tx, "SHOW statement_timeout");
            return null;
        }, CancellationToken.None);

        isolation.Should().Be("repeatable read");
        readOnly.Should().Be("on");
        timeout.Should().Be("2500ms");
        // 快照交易結束後設定不留在池連線上
        await db.Database.OpenConnectionAsync();
        (await db.Database.GetDbConnection().ExecuteScalarAsync<string>("SHOW statement_timeout")).Should().Be("0");
    }

    [PostgresFact]
    public async Task Timeout_EndsWithQueryCanceled57014_AndTheConnectionIsReusable()
    {
        await using var db = NewContext();
        var watch = Stopwatch.StartNew();

        var act = () => _snapshot.RunAsync(db, TimeSpan.FromMilliseconds(200), async (connection, tx) =>
        {
            await connection.ExecuteAsync("SELECT pg_sleep(10)", transaction: tx);
            return null;
        }, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<DbException>()).Which;
        thrown.SqlState.Should().Be("57014");
        DatabaseErrors.Instance.IsQueryCanceled(thrown).Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        var again = await _snapshot.RunAsync(db, TimeSpan.FromSeconds(5),
            async (connection, tx) => await Scalar(connection, tx, "SELECT 'ok'"), CancellationToken.None);
        again.Should().Be("ok");
    }

    [PostgresFact]
    public async Task Cancellation_InterruptsTheRunningStatement()
    {
        await using var db = NewContext();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        var act = () => _snapshot.RunAsync(db, TimeSpan.FromSeconds(30), async (connection, tx) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_sleep(10)", transaction: tx, cancellationToken: cts.Token));
            return null;
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        // 取消後同一個 context 照常查詢，連線回到關閉狀態（沒有留下開著的交易）
        db.Database.CurrentTransaction.Should().BeNull();
        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        (await db.Set<SystemSetting>().CountAsync()).Should().BeGreaterThanOrEqualTo(0);

        // 快照結束後的寫入仍經寫入閘門與全庫鎖：任一被占住時等滿上限回忙碌，都放開後照常寫入
        await using (await HoldWriteLockAsync())
        {
            db.Add(new SystemSetting { Key = "snapshot.after.lock", Value = "v" });
            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<DatabaseBusyException>();
        }
        using (await WriteGate.ForKey(_connectionString).EnterAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            var watchGate = Stopwatch.StartNew();
            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<DatabaseBusyException>();
            watchGate.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(LockTimeoutSeconds * 0.8), "要在閘門排隊等到上限");
        }
        await db.SaveChangesAsync();
        (await db.Set<SystemSetting>().CountAsync(s => s.Key == "snapshot.after.lock")).Should().Be(1);
    }

    [PostgresFact]
    public async Task InsideAnOuterTransaction_CancellationDoesNotAbortIt()
    {
        await using var db = NewContext();
        await using var outer = await db.Database.BeginTransactionAsync();
        var connection = db.Database.GetDbConnection();
        var tx = outer.GetDbTransaction();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => _snapshot.RunAsync(db, TimeSpan.FromSeconds(30), async (c, t) =>
        {
            await c.ExecuteAsync(new CommandDefinition("SELECT pg_sleep(10)", transaction: t, cancellationToken: cts.Token));
            return null;
        }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // 外層交易可繼續：寫入後提交，資料留下
        (await Scalar(connection, tx, "SHOW statement_timeout")).Should().Be("0");
        db.Add(new SystemSetting { Key = "snapshot.outer.cancel", Value = "v" });
        await db.SaveChangesAsync();
        await outer.CommitAsync();
        await using var check = NewContext();
        (await check.Set<SystemSetting>().CountAsync(s => s.Key == "snapshot.outer.cancel")).Should().Be(1);
    }

    [PostgresFact]
    public async Task Snapshot_ReleasesTheConnection_AndRestoresTheCommandTimeout()
    {
        await using var db = NewContext();
        db.Database.SetCommandTimeout(7);

        await _snapshot.RunAsync(db, TimeSpan.FromSeconds(2), async (connection, tx) => await Scalar(connection, tx, "SELECT 1"), CancellationToken.None);

        db.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed, "快照自己開的連線要關回去，不留到 context dispose");
        db.Database.CurrentTransaction.Should().BeNull();
        db.Database.GetCommandTimeout().Should().Be(7);
    }

    /// <summary>
    /// 用戶端的 Command Timeout 比報表上限短時（審查必修）：EF 與 Dapper 的命令都要放寬到比 statement_timeout 長，
    /// 讓伺服器端的 57014 先觸發；否則 Npgsql 先在用戶端逾時，丟不帶 SQLSTATE 的例外，被當成忙碌回 503。
    /// Dapper 端的放寬經報表 repository 驗（<c>ReportSqlEquivalenceTests</c>），這裡驗 EF 端與快照設定的值。
    /// </summary>
    [PostgresFact]
    public async Task ShortClientTimeout_StatementTimeoutStillDecides()
    {
        await using var db = NewContext();
        db.Database.SetCommandTimeout(1);

        // 跑 1.5 秒的 EF 查詢：比用戶端 1 秒長、比報表上限 3 秒短，要正常完成
        var slept = await _snapshot.RunAsync(db, TimeSpan.FromSeconds(3), async (_, _) =>
        {
            db.Database.GetCommandTimeout().Should().BeGreaterThan(3, "快照內的用戶端逾時要比 statement_timeout 寬");
            await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM pg_sleep(1.5)").ToListAsync();
            return "done";
        }, CancellationToken.None);
        slept.Should().Be("done");

        // 超過報表上限：由伺服器以 57014 中止，不是用戶端逾時
        var act = () => _snapshot.RunAsync(db, TimeSpan.FromSeconds(2), async (_, _) =>
        {
            await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM pg_sleep(10)").ToListAsync();
            return null;
        }, CancellationToken.None);
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        DatabaseErrors.Instance.IsQueryCanceled(thrown).Should().BeTrue($"應該是 57014，實際是 {thrown}");
        db.Database.GetCommandTimeout().Should().Be(1, "快照結束後還原");
    }

    [PostgresFact]
    public async Task Snapshot_DoesNotQueueOnTheWriteGate_NorTakeTheWriteLock()
    {
        await using var db = NewContext();
        // 同一實例的寫入占著閘門、另一個實例占著全庫鎖：寫入交易在這裡要等滿 1 秒後回忙碌，唯讀快照要照常跑完
        using var permit = await WriteGate.ForKey(_connectionString).EnterAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await using var other = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString);
        await other.OpenAsync();
        await other.ExecuteAsync($"SELECT pg_advisory_lock({PostgresLockKeys.Write})");
        var watch = Stopwatch.StartNew();

        var result = await _snapshot.RunAsync(db, TimeSpan.FromSeconds(30),
            async (connection, tx) => await Scalar(connection, tx, "SELECT count(*) FROM \"Dynamic_Module\""), CancellationToken.None);

        result.Should().NotBeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(LockTimeoutSeconds));
    }

    [PostgresFact]
    public async Task InsideAnOuterTransaction_TimeoutDoesNotAbortIt_AndTheSettingIsRestored()
    {
        await using var db = NewContext();
        await using var outer = await db.Database.BeginTransactionAsync();
        var connection = db.Database.GetDbConnection();
        var tx = outer.GetDbTransaction();

        var act = () => _snapshot.RunAsync(db, TimeSpan.FromMilliseconds(200), async (c, t) =>
        {
            t.Should().BeSameAs(tx, "已有外層交易時沿用它");
            await c.ExecuteAsync("SELECT pg_sleep(10)", transaction: t);
            return null;
        }, CancellationToken.None);
        (await act.Should().ThrowAsync<DbException>()).Which.SqlState.Should().Be("57014");

        // 外層交易沒有變成 aborted，statement_timeout 回到原值
        (await Scalar(connection, tx, "SHOW statement_timeout")).Should().Be("0");
        (await Scalar(connection, tx, "SELECT 'still usable'")).Should().Be("still usable");
        await outer.CommitAsync();
    }
}
