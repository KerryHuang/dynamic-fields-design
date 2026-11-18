using DynamicModules.Persistence;
using DynamicModules.TestSupport;
using FluentAssertions;
using Npgsql;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// Npgsql 8 取不到池連線時的例外形狀（PostgreSQL provider 設計 §5.7）：池上限設 1、占住後再取，
/// 錯誤分類要把它當成忙碌（API 回 503 而不是 500）。只在 PostgreSQL 模式跑（FORMWRIGHT_TEST_DB=postgres）。
/// </summary>
[Trait("Category", "Postgres")]
public class PostgresPoolExhaustionTests
{
    [PostgresFact]
    public async Task PoolExhausted_IsClassifiedAsBusy()
    {
        var builder = new NpgsqlConnectionStringBuilder(await PostgresTestServer.AdminConnectionStringAsync())
        {
            MaxPoolSize = 1,
            Timeout = 1,
            // 每個測試自己的池：連線字串不同就是不同的池
            ApplicationName = $"pool-exhaustion-{Guid.NewGuid():N}",
        };
        await using var holder = new NpgsqlConnection(builder.ConnectionString);
        await holder.OpenAsync();

        await using var second = new NpgsqlConnection(builder.ConnectionString);
        var act = () => second.OpenAsync();

        var thrown = (await act.Should().ThrowAsync<NpgsqlException>()).Which;
        thrown.SqlState.Should().BeNull("池耗盡不是伺服器錯誤，沒有 SQLSTATE");
        thrown.InnerException.Should().BeOfType<TimeoutException>();
        DatabaseErrors.Instance.IsBusyOrConflict(thrown).Should().BeTrue();
    }
}
