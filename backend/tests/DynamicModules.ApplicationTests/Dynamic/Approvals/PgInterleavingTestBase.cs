using System.Data;
using System.Reflection;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Postgres;
using DynamicModules.Persistence.Repositories.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 簽核的 PostgreSQL 真交錯（票 #24、#39）。既有的併發測試共用同一個 DbContext、同一條連線，「別人剛提交」是用 ExecuteUpdate 在同一個交易內模擬，
/// 到了 PostgreSQL 並不是真的兩條連線在爭同一列。這裡每個請求自己一個 DbContext（各自的連線與交易），用屏障或「卡在列鎖上」的偵測控制先後，不靠 sleep。
/// 只在 PostgreSQL 跑（FORMWRIGHT_TEST_DB=postgres），SQLite 略過。
/// </summary>
public abstract class PgInterleavingTestBase : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    /// <summary>true 是無鎖模式（不排隊、不取全庫鎖），讓請求真的在資料庫層交錯；false 是正式設定（全庫寫入鎖，請求逐個提交）。</summary>
    protected abstract bool Unserialized { get; }

    /// <summary>請求的使用者隨非同步流程走（同時有兩個請求在跑，不能共用一個全域的目前使用者）。</summary>
    private static readonly AsyncLocal<string?> Actor = new();

    private sealed class ActorUser : ICurrentUser { public string UserId => Actor.Value ?? "admin"; }

    /// <summary>以 <paramref name="user"/> 的身分跑 <paramref name="action"/>（新的非同步流程，設定不會漏到呼叫端）。</summary>
    protected static Task<T> By<T>(string user, Func<Task<T>> action) => Task.Run(() =>
    {
        Actor.Value = user;
        return action();
    });

    private PostgresWriteSettings Settings => Unserialized
        ? PostgresWriteSettings.Unserialized()
        : new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds);

    protected DynamicDbContext NewContext() => new((DbContextOptions<DynamicDbContext>)PostgresDatabase
        .Configure(new DbContextOptionsBuilder<DynamicDbContext>(), Db.PostgresConnectionString!, Settings)
        .AddInterceptors(WriteGuardInterceptor.Instance).Options);

    protected override void ConfigureServices(IServiceCollection services)
    {
        if (!TestDatabase.IsPostgres) return;
        services.AddSingleton(Settings);
        services.AddSingleton<ICurrentUser, ActorUser>();
        // 每個請求自己的 DbContext（取代基底註冊的共用單例）：各自的連線，才有真的交錯
        services.AddScoped(_ => NewContext());
        Extra(services);
    }

    protected virtual void Extra(IServiceCollection services) { }

    /// <summary>輪詢到有一條連線正在等列鎖或交易鎖為止（有逾時，不靠固定睡眠）。</summary>
    protected async Task WaitUntilSomeoneBlocksOnALockAsync()
    {
        await using var probe = new NpgsqlConnection(Db.PostgresConnectionString);
        await probe.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", probe);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("沒有任何連線卡在鎖上：兩個交易沒有真的交錯");
    }

    protected static async Task<T> Within<T>(Task<T> task, int seconds = 30) => await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    protected static Task Within(Task task, int seconds = 30) => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    protected async Task<PurchaseRig> Rig(ApprovalDefinition def)
    {
        var r = await BuildPurchase();
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, def);
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module, r.Extra);
        return r;
    }

    protected static ApprovalDefinition Linear(string approver) =>
        G([Start(), Approval("a", Users(approver)), End()], [E("e0", "start", "a"), E("e1", "a", "end")]);

    protected Task<DynamicModules.Application.Dynamic.RecordService.Data.DynamicRecordResponse> NewPoBy(string user, PurchaseRig r) =>
        By(user, () => NewPo(r, price: 5000));

    protected Task<ServiceResult> SubmitBy(string user, long recordId, long to) => By(user, () => Submit(recordId, to));
}
