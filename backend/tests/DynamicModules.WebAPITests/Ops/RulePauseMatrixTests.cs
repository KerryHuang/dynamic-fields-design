using System.Net;
using System.Net.Sockets;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OpsService.RulePause;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// E13 例外矩陣（維運設計 §17.8）：每列由任務規則丟出真的例外型別、經 WebAPI 測試主機的處理器鏈，斷言回應碼；連存 5 次，
/// 「不計」的不寫健康列、「計」的第 5 次暫停；同一列另直接呼叫 <see cref="RuleFailureClassifier.IsAttributable"/>。
/// 「用戶端已中斷的取消」測不到回應碼（連線已斷），只驗歸屬（與「沒中斷」同型別，計數路徑相同）。
/// </summary>
[Trait("Category", "Postgres")]
public class RulePauseMatrixTests
{
    private const string Rule = "r-matrix";

    private static SqliteException Sqlite(int code, int? extended = null) =>
        extended is { } ext ? new SqliteException($"sqlite {code}", code, ext) : new SqliteException($"sqlite {code}", code);

    private static Npgsql.PostgresException Pg(string state) => new($"pg {state}", "ERROR", "ERROR", state);

    private static FrozenFieldException Frozen() =>
        new(new Field { Id = 1, Key = "f_1" }, new FieldLock("approval", new DynamicModules.Domain.ValueObjects.I18nText([new("zh-TW", "核准")])));

    /// <summary>列：名稱、例外、回應碼（null＝回應不驗）、是否計數。</summary>
    public static TheoryData<string, Func<Exception>, HttpStatusCode?, bool> Rows()
    {
        var rows = new TheoryData<string, Func<Exception>, HttpStatusCode?, bool>
        {
            { "sqlite busy 5", () => Sqlite(5), HttpStatusCode.ServiceUnavailable, false },
            { "sqlite locked 6", () => Sqlite(6), HttpStatusCode.ServiceUnavailable, false },
            { "DatabaseBusyException", () => new DatabaseBusyException("排隊逾時"), HttpStatusCode.ServiceUnavailable, false },
            { "pg 40001", () => Pg("40001"), HttpStatusCode.ServiceUnavailable, false },
            { "pg 40P01", () => Pg("40P01"), HttpStatusCode.ServiceUnavailable, false },
            { "pg 55P03", () => Pg("55P03"), HttpStatusCode.ServiceUnavailable, false },
            { "pg pool exhausted", () => new Npgsql.NpgsqlException("pool", new TimeoutException()), HttpStatusCode.ServiceUnavailable, false },
            { "sqlite readonly 8", () => Sqlite(8), HttpStatusCode.ServiceUnavailable, false },
            { "pg 25006", () => Pg("25006"), HttpStatusCode.ServiceUnavailable, false },
            { "sqlite full 13", () => Sqlite(13), HttpStatusCode.ServiceUnavailable, false },
            { "pg 53100", () => Pg("53100"), HttpStatusCode.ServiceUnavailable, false },
            { "sqlite cantopen 14", () => Sqlite(14), HttpStatusCode.ServiceUnavailable, false },
            { "sqlite ioerr 10", () => Sqlite(10), HttpStatusCode.ServiceUnavailable, false },
            { "pg 08006", () => Pg("08006"), HttpStatusCode.ServiceUnavailable, false },
            { "pg 57P01", () => Pg("57P01"), HttpStatusCode.ServiceUnavailable, false },
            { "pg socket", () => new Npgsql.NpgsqlException("socket", new SocketException()), HttpStatusCode.ServiceUnavailable, false },
            { "concurrency", () => new DbUpdateConcurrencyException("stale"), HttpStatusCode.Conflict, false },
            { "frozen field", Frozen, HttpStatusCode.Conflict, false },
            { "canceled, client aborted", () => new OperationCanceledException(), null, false },
            { "canceled, not aborted", () => new OperationCanceledException(), HttpStatusCode.InternalServerError, false },
            { "task canceled, not aborted", () => new TaskCanceledException(), HttpStatusCode.InternalServerError, false },
            { "sqlite interrupt 9", () => Sqlite(9), HttpStatusCode.InternalServerError, false },
            { "pg 57014", () => Pg("57014"), HttpStatusCode.InternalServerError, false },
            { "sqlite unique 19/2067", () => Sqlite(19, 2067), HttpStatusCode.InternalServerError, false },
            { "sqlite primary key 19/1555", () => Sqlite(19, 1555), HttpStatusCode.InternalServerError, false },
            { "pg 23505", () => Pg("23505"), HttpStatusCode.InternalServerError, false },
            // 同內層：資料庫類包在 DbUpdateException 裡，處理器與分類都看得到內層
            { "wrapped sqlite busy", () => new DbUpdateException("save", Sqlite(5)), HttpStatusCode.ServiceUnavailable, false },
            { "wrapped pg 25006", () => new DbUpdateException("save", Pg("25006")), HttpStatusCode.ServiceUnavailable, false },
            { "wrapped pg 08006", () => new DbUpdateException("save", Pg("08006")), HttpStatusCode.ServiceUnavailable, false },
            { "wrapped pg 57014", () => new InvalidOperationException("outer", Pg("57014")), HttpStatusCode.InternalServerError, false },
            { "wrapped pg 23505", () => new DbUpdateException("save", Pg("23505")), HttpStatusCode.InternalServerError, false },
            { "wrapped canceled", () => new InvalidOperationException("outer", new OperationCanceledException()), HttpStatusCode.InternalServerError, false },
            // 型別類的包在別的例外裡：處理器只看最外層（500），分類沿內層找（不計）——偏離「同內層」，見 §17.14
            { "wrapped concurrency", () => new InvalidOperationException("outer", new DbUpdateConcurrencyException("stale")),
                HttpStatusCode.InternalServerError, false },
            { "InvalidOperationException", () => new InvalidOperationException("boom"), HttpStatusCode.InternalServerError, true },
            { "NullReferenceException", () => new NullReferenceException(), HttpStatusCode.InternalServerError, true },
            { "InjectedFaultException", () => new InjectedFaultException(Rule), HttpStatusCode.InternalServerError, true },
            // 數值溢位：任務規則的路徑沒有轉成 400／409，照 500、計（§17.14）
            { "pg 22003 overflow", () => Pg("22003"), HttpStatusCode.InternalServerError, true },
            { "sqlite integer overflow", () => new SqliteException("integer overflow", 1), HttpStatusCode.InternalServerError, true },
        };
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task E13_Row(string row, Func<Exception> make, HttpStatusCode? status, bool counts)
    {
        using var rig = new RulePauseRig();
        await rig.BuildAsync();
        var id = await rig.CreateRuleAsync(Rule);

        rig.App.Services.GetRequiredService<RuleFailureClassifier>().IsAttributable(make()).Should().Be(counts, row);

        rig.Faults.Fail(Rule, make);
        for (var i = 0; i < 5; i++)
        {
            rig.Tick();
            var res = await rig.PostRecordAsync();
            if (status is { } expected) res.StatusCode.Should().Be(expected, row);
        }
        var h = await rig.HealthAsync(id);
        if (counts)
        {
            h.Should().NotBeNull(row);
            h!.PausedUtc.Should().NotBeNull(row);
            h.ConsecutiveFailures.Should().Be(5);
        }
        else h.Should().BeNull(row);
    }
}
