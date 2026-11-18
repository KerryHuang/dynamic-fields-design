using System.Data.Common;
using DynamicModules.Application.Common;
using DynamicModules.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>錯誤分類（PostgreSQL provider 設計 §5.7）：SQLite 看錯誤碼，PostgreSQL 看 SQLSTATE；會沿著 InnerException 往內找。</summary>
[Trait("Category", "Postgres")]
public class DatabaseErrorsTests
{
    private static readonly IDatabaseErrors Errors = DatabaseErrors.Instance;

    /// <summary>模擬 Npgsql 的 PostgresException：只帶 .NET 標準的 SqlState，不引用 Npgsql 型別。</summary>
    private sealed class FakePgException(string? sqlState, Exception? inner = null) : DbException("pg", inner)
    {
        public override string? SqlState => sqlState;
    }

    private static DbUpdateException Wrapped(Exception inner) => new("儲存失敗", inner);

    [Theory]
    [InlineData("23505", true)]
    [InlineData("23503", false)]   // 外鍵衝突不是唯一鍵衝突
    [InlineData(null, false)]
    public void Postgres_UniqueViolation(string? state, bool expected)
    {
        Errors.IsUniqueViolation(new FakePgException(state)).Should().Be(expected);
        Errors.IsUniqueViolation(Wrapped(new FakePgException(state))).Should().Be(expected);
    }

    [Theory]
    [InlineData("40001", true)]
    [InlineData("40P01", true)]
    [InlineData("55P03", true)]
    [InlineData("23505", false)]
    [InlineData("57014", false)]
    public void Postgres_BusyOrConflict(string state, bool expected)
    {
        Errors.IsBusyOrConflict(new FakePgException(state)).Should().Be(expected);
        Errors.IsBusyOrConflict(Wrapped(new FakePgException(state))).Should().Be(expected);
    }

    [Fact]
    public void Postgres_PoolExhaustionWithoutSqlStateIsBusy()
    {
        Errors.IsBusyOrConflict(new FakePgException(null, new TimeoutException())).Should().BeTrue();
        Errors.IsBusyOrConflict(new FakePgException(null, new InvalidOperationException())).Should().BeFalse();
        Errors.IsBusyOrConflict(new FakePgException(null)).Should().BeFalse();
    }

    [Theory]
    [InlineData("22003", true)]
    [InlineData("22012", false)]
    public void Postgres_NumericOverflow(string state, bool expected) =>
        Errors.IsNumericOverflow(new FakePgException(state)).Should().Be(expected);

    [Theory]
    [InlineData("57014", true)]
    [InlineData("55P03", false)]
    public void Postgres_QueryCanceled(string state, bool expected) =>
        Errors.IsQueryCanceled(new FakePgException(state)).Should().Be(expected);

    [Theory]
    [InlineData(19, 2067, true)]    // SQLITE_CONSTRAINT_UNIQUE
    [InlineData(19, 1555, true)]    // SQLITE_CONSTRAINT_PRIMARYKEY
    [InlineData(19, 2579, true)]    // SQLITE_CONSTRAINT_ROWID
    [InlineData(19, 1811, false)]   // SQLITE_CONSTRAINT_TRIGGER
    [InlineData(19, 787, false)]    // SQLITE_CONSTRAINT_FOREIGNKEY
    [InlineData(19, 1299, false)]   // SQLITE_CONSTRAINT_NOTNULL
    public void Sqlite_UniqueViolationByExtendedCode(int code, int extended, bool expected) =>
        Errors.IsUniqueViolation(Wrapped(new SqliteException("x", code, extended))).Should().Be(expected);

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(19, false)]
    [InlineData(9, false)]
    public void Sqlite_BusyOrLocked(int code, bool expected)
    {
        Errors.IsBusyOrConflict(new SqliteException("x", code)).Should().Be(expected);
        DatabaseBusy.Is(Wrapped(new SqliteException("x", code))).Should().Be(expected);
    }

    [Fact]
    public void AlreadyClassifiedBusyIsBusy()
    {
        Errors.IsBusyOrConflict(new DatabaseBusyException("忙")).Should().BeTrue();
        Errors.IsBusyOrConflict(new InvalidOperationException("x", new DatabaseBusyException("忙"))).Should().BeTrue();
        Errors.IsBusyOrConflict(new InvalidOperationException("x")).Should().BeFalse();
    }

    [Fact]
    public void Sqlite_QueryCanceledIsInterrupt()
    {
        Errors.IsQueryCanceled(new SqliteException("interrupted", 9)).Should().BeTrue();
        Errors.IsQueryCanceled(new SqliteException("x", 5)).Should().BeFalse();
    }

    // ---- 真的 SQLite 丟出的例外：確認錯誤碼與擴充碼的假設 ----

    private static async Task<SqliteException> Raise(string setup, string failing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = setup;
            await cmd.ExecuteNonQueryAsync();
        }
        await using var bad = connection.CreateCommand();
        bad.CommandText = failing;
        var act = () => bad.ExecuteNonQueryAsync();
        return (await act.Should().ThrowAsync<SqliteException>()).Which;
    }

    [Fact]
    public async Task RealSqlite_UniqueIndexAndPrimaryKey()
    {
        var unique = await Raise("CREATE TABLE t (k TEXT UNIQUE); INSERT INTO t VALUES ('a');", "INSERT INTO t VALUES ('a')");
        var primary = await Raise("CREATE TABLE t (id INTEGER, b INTEGER, PRIMARY KEY (id, b)); INSERT INTO t VALUES (1, 1);", "INSERT INTO t VALUES (1, 1)");
        var rowId = await Raise("CREATE TABLE t (k TEXT); INSERT INTO t (rowid, k) VALUES (1, 'a');", "INSERT INTO t (rowid, k) VALUES (1, 'b')");
        var integerKey = await Raise("CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1);", "INSERT INTO t VALUES (1)");
        var notNull = await Raise("CREATE TABLE t (k TEXT NOT NULL);", "INSERT INTO t VALUES (NULL)");

        // 改寫前以訊息含 UNIQUE 判斷；凡是那樣判成唯一鍵衝突的，錯誤碼判斷也要一致
        foreach (var ex in new[] { unique, primary, rowId, integerKey })
        {
            ex.Message.Should().Contain("UNIQUE");
            Errors.IsUniqueViolation(Wrapped(ex)).Should().BeTrue(ex.Message);
        }
        Errors.IsUniqueViolation(Wrapped(notNull)).Should().BeFalse();
    }

    [Fact]
    public async Task RealSqlite_SumOverflow()
    {
        var overflow = await Raise(
            "CREATE TABLE t (v INTEGER); INSERT INTO t VALUES (9223372036854775807), (1);",
            "SELECT CAST(SUM(v) AS BIGINT) FROM t");

        Errors.IsNumericOverflow(overflow).Should().BeTrue();
        Errors.IsNumericOverflow(new SqliteException("x", 1)).Should().BeFalse();
    }
}
