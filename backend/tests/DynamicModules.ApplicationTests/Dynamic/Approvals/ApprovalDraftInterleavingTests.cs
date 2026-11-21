using System.Data;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 簽核流程草稿的條件更新與版本使用判斷，兩條連線在 PostgreSQL 上真的交錯（無鎖模式、READ COMMITTED，票 #39）：
/// 草稿條件更新（TryReplaceDraftAsync）遇到另一個還沒提交的交易改了同一列，要等對方提交、以新值重新比對、回 0 列，不是靜默覆蓋；
/// 另一個交易還沒提交的版本綁定，IsVersionUsedAsync 看不到——所以發布與送簽的競爭要靠全庫寫入鎖（下面的 <see cref="ApprovalPublishVersionRaceTests"/> 驗證有鎖時的結果）。
/// </summary>
[Trait("Category", "Postgres")]
public class ApprovalDraftInterleavingTests : PgInterleavingTestBase
{
    protected override bool Unserialized => true;

    private async Task<(PurchaseRig R, ApprovalFlow Flow, ApprovalInstance Inst)> Seed()
    {
        var r = await BuildPurchase();
        var flow = await SeedFlow(r, "po-s", r.Confirmed, 1, Linear("fin1"));
        return (r, flow, await SeedInstance(r, flow));
    }

    [PostgresFact]
    public async Task TryReplaceDraft_SecondWaitsForFirstCommit_ThenFalse_FirstDraftSurvives()
    {
        var (_, flow, _) = await Seed();
        var versionId = Flow("po-s").CurrentVersionId;
        await using var c1 = NewContext();
        await using var c2 = NewContext();
        await using var t1 = await c1.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var t2 = await c2.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var repo1 = new ApprovalFlowRepository(c1);
        var repo2 = new ApprovalFlowRepository(c2);

        // 兩邊讀到同一個基準（沒有草稿、同一個版本），先後想把草稿換掉
        (await repo1.TryReplaceDraftAsync(flow.Id, null, versionId, """{"rev":"first"}""", "fin1", SeedTime, default)).Should().BeTrue();
        var second = repo2.TryReplaceDraftAsync(flow.Id, null, versionId, """{"rev":"second"}""", "fin2", SeedTime, default);
        await WaitUntilSomeoneBlocksOnALockAsync();

        second.IsCompleted.Should().BeFalse();
        await t1.CommitAsync();
        (await Within(second)).Should().BeFalse("第一個提交後草稿已不是 null：null 對非 null 不相等，0 列（不是靜默覆蓋）");
        await t2.RollbackAsync();
        (Flow("po-s").DraftJson, Flow("po-s").DraftUpdatedBy).Should().Be(("""{"rev":"first"}""", "fin1"));
    }

    [PostgresFact]
    public async Task TryReplaceDraft_CurrentVersionChangedByOtherTransaction_ReturnsFalse()
    {
        var (_, flow, _) = await Seed();
        var v1 = Flow("po-s").CurrentVersionId;
        await using var c1 = NewContext();
        await using var c2 = NewContext();
        await using var t1 = await c1.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var t2 = await c2.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        // 另一個交易發布新版本（CurrentVersionId 換掉、草稿清成新權杖）；這一邊拿舊的 (草稿, 版本) 基準去換
        var v2 = new ApprovalFlowVersion { FlowId = flow.Id, Version = 2, Definition = Linear("fin2"), CreatedBy = "admin", CreatedTime = SeedTime };
        c1.Add(v2);
        await c1.SaveChangesAsync();
        await c1.Set<ApprovalFlow>().Where(f => f.Id == flow.Id).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.CurrentVersionId, v2.Id).SetProperty(f => f.DraftJson, """{"rev":"published"}"""));
        var stale = new ApprovalFlowRepository(c2).TryReplaceDraftAsync(flow.Id, null, v1, """{"rev":"late"}""", "fin2", SeedTime, default);
        await WaitUntilSomeoneBlocksOnALockAsync();
        await t1.CommitAsync();

        (await Within(stale)).Should().BeFalse();
        await t2.RollbackAsync();
        (Flow("po-s").CurrentVersionId, Flow("po-s").DraftJson).Should().Be((v2.Id, """{"rev":"published"}"""));
    }

    [PostgresFact]
    public async Task IsVersionUsed_DoesNotSeeUncommittedBinding_SoSerializingWritesIsWhatProtectsPublish()
    {
        // 記錄「為什麼發布與送簽的競爭要靠全庫寫入鎖」：沒有鎖時，另一個交易還沒提交的綁定在 READ COMMITTED 下看不到
        var r = await BuildPurchase();
        var flow = await SeedFlow(r, "po-s", r.Confirmed, 1, Linear("fin1"));
        var versionId = Flow("po-s").CurrentVersionId!.Value;
        await using var c1 = NewContext();
        await using var c2 = NewContext();
        await using var t1 = await c1.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var t2 = await c2.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var flows2 = new ApprovalFlowRepository(c2);
        c1.Add(new ApprovalInstance
        {
            RecordId = 2, ModuleId = r.ModuleId, FlowId = flow.Id, FlowVersionId = versionId, RouteKind = ApprovalRouteKind.Flow,
            FromOptionId = r.Draft, ToOptionId = flow.ToOptionId, InitiatorUserId = "buyer1", Status = ApprovalInstanceStatus.Pending,
            TitleSnapshot = "y", SubmittedTime = SeedTime, RowVersion = Guid.NewGuid().ToString(), OperationId = Guid.NewGuid().ToString("N")
        });
        await c1.SaveChangesAsync();

        (await flows2.IsVersionUsedAsync(versionId, default)).Should().BeFalse("c1 還沒提交，c2 看不到這筆綁定");
        await t1.CommitAsync();
        (await flows2.IsVersionUsedAsync(versionId, default)).Should().BeTrue("同一個交易內下一個陳述式就看得到已提交的綁定");
        await t2.RollbackAsync();
    }
}

/// <summary>
/// 正式設定（全庫寫入鎖）下，發布與送簽在 PostgreSQL 上真的同時進來（票 #39）：兩個請求各自的連線，其中一個握著鎖時另一個在閘門排隊，
/// 輪到它時看到的是前一個已提交的結果。不論誰先，都不會出現「版本已被實例使用、定義卻被原地改掉」。
/// </summary>
[Trait("Category", "Postgres")]
public class ApprovalPublishVersionRaceTests : PgInterleavingTestBase
{
    protected override bool Unserialized => false;

    /// <summary>等「沒排隊就會先完成」的那一方多久；只用來讓壞掉時快速失敗，正確與否由最後的結果斷言決定。</summary>
    private static readonly TimeSpan WaitForUnqueued = TimeSpan.FromSeconds(2);

    private Func<Task>? _hook;
    private string _hookMethod = nameof(IApprovalFlowRepository.TryReplaceDraftAsync);

    protected override void Extra(IServiceCollection services) =>
        RacingFlowRepository.Register(services, () =>
        {
            var h = _hook;
            _hook = null;
            return h;
        }, () => _hookMethod);

    private static ApprovalFlowDraftInput Input(PurchaseRig r, ApprovalDefinition d, long baseVersionId) =>
        new("po-g", Text("po-g"), r.Confirmed, 1, d, null, null, baseVersionId, false);

    private ApprovalFlowVersion[] Versions() =>
        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Where(v => v.FlowId == Flow("po-g").Id).OrderBy(v => v.Version).ToArray();

    [PostgresFact]
    public async Task PublishHoldsTheLock_SubmitQueues_ThenBindsToTheNewlyPublishedVersion()
    {
        var r = await Rig(Linear("fin1"));
        var po = await NewPoBy("buyer1", r);
        var v1 = Flow("po-g").CurrentVersionId!.Value;
        var inHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 發布已讀過「版本沒被用過」、握著寫入鎖、還沒條件更新草稿的這一刻，送簽進來
        _hook = async () =>
        {
            inHook.SetResult();
            await release.Task;
        };

        var publish = By("admin", () => Send(new PublishApprovalFlowRequest("po-g", Input(r, Linear("fin2"), v1))));
        ServiceResult published, submitted;
        try
        {
            await Within(inHook.Task);
            var submit = SubmitBy("buyer1", po.Id, r.Confirmed);
            // 只當快速失敗：排隊的話這段時間內送簽不會完成。證明排隊靠的是下面的結果（實例的任務負責人是 fin2），不是這個計時
            await Task.WhenAny(submit, Task.Delay(WaitForUnqueued));
            submit.IsCompleted.Should().BeFalse("發布握著全庫寫入鎖，送簽要排隊等它提交");
            release.TrySetResult();
            published = await Within(publish);
            submitted = await Within(submit);
        }
        finally { release.TrySetResult(); }

        published.IsSuccess.Should().BeTrue(Dump(published));
        submitted.IsCreated.Should().BeTrue(Dump(submitted));
        Versions().Should().ContainSingle("送簽排在發布之後，發布當時版本沒被用過，原地改寫");
        var inst = Inst(((SubmitApprovalResponse)submitted.Data!).InstanceId!.Value);
        inst.FlowVersionId.Should().Be(v1);
        TasksOf(inst.Id).Single(t => t.Status == ApprovalTaskStatus.Open).AssigneeUserId.Should().Be("fin2",
            "結果自己分辨先後：送簽若沒排隊、先於發布完成，會綁到舊內容、開出 fin1 的任務");
        Versions().Single().Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal(["fin2"], "實例綁到的是新內容，沒有用了舊內容卻被改掉");
    }

    [PostgresFact]
    public async Task SubmitHoldsTheLock_PublishQueues_ThenOpensANewVersion_OldVersionContentUntouched()
    {
        var r = await Rig(Linear("fin1"));
        var po = await NewPoBy("buyer1", r);
        var v1 = Flow("po-g").CurrentVersionId!.Value;
        var inHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 送簽在交易內、取流程之前停住（握著寫入鎖），這時發布進來
        _hookMethod = nameof(IApprovalFlowRepository.ListCurrentByModuleAsync);
        _hook = async () =>
        {
            inHook.SetResult();
            await release.Task;
        };

        var submit = SubmitBy("buyer1", po.Id, r.Confirmed);
        ServiceResult submitted, published;
        try
        {
            await Within(inHook.Task);
            var publish = By("admin", () => Send(new PublishApprovalFlowRequest("po-g", Input(r, Linear("fin2"), v1))));
            // 沒有閘門的話發布會在送簽停住時就跑完；等的時間夠長，慢機器上也跑得完，才不會假綠。排隊時這段等滿才放行
            await Task.WhenAny(publish, Task.Delay(WaitForUnqueued));
            publish.IsCompleted.Should().BeFalse("送簽握著全庫寫入鎖，發布要排隊等它提交");
            Flow("po-g").CurrentVersionId.Should().Be(v1, "放行送簽時發布還沒提交");
            release.TrySetResult();
            submitted = await Within(submit);
            published = await Within(publish);
        }
        finally { release.TrySetResult(); }

        submitted.IsCreated.Should().BeTrue(Dump(submitted));
        published.IsSuccess.Should().BeTrue(Dump(published));
        Versions().Select(v => v.Version).Should().Equal(1, 2);
        Versions()[0].Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal(["fin1"], "已被實例使用的版本內容不變");
        Versions()[1].Definition.Nodes!.Single(n => n.Id == "a").Approvers!.UserIds.Should().Equal(["fin2"]);
        Inst(((SubmitApprovalResponse)submitted.Data!).InstanceId!.Value).FlowVersionId.Should().Be(v1);
        Flow("po-g").CurrentVersionId.Should().Be(Versions()[1].Id);
    }
}
