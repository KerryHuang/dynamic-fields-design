using System.Data.Common;
using System.Diagnostics;
using Dapper;
using DynamicModules.Domain.Data;
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
