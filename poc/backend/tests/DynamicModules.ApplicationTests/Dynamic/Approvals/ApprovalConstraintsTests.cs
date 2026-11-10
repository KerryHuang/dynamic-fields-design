using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Queries.GetWorkflow;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>儲存狀態機時的入口約束與事件交叉檢查（設計 §5.2、§5.3、§9.2、§9.3）；流程事先直接放在 Db。</summary>
public class ApprovalConstraintsTests : ApprovalTestBase
{
    private static string T(long from, long to) => $"transitions[{from}>{to}]";
    private static string S(long id) => $"states[{id}]";

    private static string[] Msgs(ServiceResult r, string key)
    {
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(key, string.Join("；", r.Errors.Select(e => $"{e.Key}={string.Join("|", e.Value)}")));
        return r.Errors[key];
    }

    private static void Ok(ServiceResult r) =>
        r.IsSuccess.Should().BeTrue(string.Join("；", r.Errors.Select(e => $"{e.Key}={string.Join("|", e.Value)}")));

    /// <summary>流程 A（≥ 100000，主管＋board 會簽）、B（≥ 10000，purchase-lead）掛在已確認。</summary>
    private async Task<PurchaseRig> WithFlowsAB(bool active = true)
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1,
            Def(Cond(r.Total, "gte", 100000), Stage("mgr", Manager(1)), Stage("board", Group(BoardGroup, PurchaseLeadGroup), mode: ApprovalModes.All)),
            active);
        await SeedFlow(r, "po-b", r.Confirmed, 2, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup))), active);
        return r;
    }

    private static List<WorkflowTransition> Approved(PurchaseRig r, params WorkflowTransition[] extra) =>
    [
        Needs(r.Draft, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
        Plain(r.Confirmed, r.Cancelled), .. extra
    ];

    [Fact]
    public async Task SecondEntry_NotApproval_400()
    {
        var r = await WithFlowsAB();
        var res = await SaveWorkflow(r, Approved(r, Plain(r.Confirmed, r.Returned), Plain(r.Returned, r.Confirmed)));
        Msgs(res, T(r.Returned, r.Confirmed)).Should().Contain(ApprovalMessages.EntryMismatch("已確認"));
    }

    [Fact]
    public async Task ApprovalTarget_NoFlow_400()
    {
        var r = await BuildPurchase();
        Msgs(await SaveWorkflow(r, Approved(r)), S(r.Confirmed)).Should().Contain(ApprovalMessages.NoFlow("已確認"));
    }

    [Fact]
    public async Task DirectEntry_AllFlowsInactive_400_BlockEntryAllowed()
    {
        var r = await WithFlowsAB(active: false);
        Msgs(await SaveWorkflow(r, Approved(r)), S(r.Confirmed)).Should().Contain(ApprovalMessages.DirectNeedsActive("已確認"));

        Ok(await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received)]));

        var r2 = await WithFlowsAB2();
        Ok(await SaveWorkflow(r2, [
            Needs(r2.Draft, r2.Confirmed, ApprovalNoMatch.Direct), Needs(r2.Confirmed, r2.Cancelled), Plain(r2.Confirmed, r2.Partial)
        ]));
    }

    /// <summary>第二張單據：已確認有啟用中的 A；已取消只有停用的 C（入口 block）。</summary>
    private async Task<PurchaseRig> WithFlowsAB2()
    {
        var r = await BuildPurchase("po2");
        await SeedFlow(r, "po2-a", r.Confirmed, 1, Def(null, Stage("mgr", Manager(1))));
        await SeedFlow(r, "po2-c", r.Cancelled, 1, Def(null, Stage("board", Group(BoardGroup))), active: false);
        return r;
    }

    [Fact]
    public async Task PlainTransitionIntoDownstreamLock_400()
    {
        var r = await WithFlowsAB();
        Msgs(await SaveWorkflow(r, Approved(r, Plain(r.Draft, r.Partial))), T(r.Draft, r.Partial))
            .Should().Contain(ApprovalMessages.LockEntry("草稿", "部分進貨"));
    }

    [Fact]
    public async Task LockedToLockedViaApproval_NotLockEntry()
    {
        var r = await WithFlowsAB();
        await SeedFlow(r, "po-c", r.Cancelled, 1, Def(null, Stage("board", Group(BoardGroup))));
        var transitions = Approved(r);
        transitions[3] = Needs(r.Confirmed, r.Cancelled);
        transitions.Add(Needs(r.Partial, r.Cancelled));
        Ok(await SaveWorkflow(r, transitions));
    }

    [Fact]
    public async Task InitialLocked_400()
    {
        var r = await WithFlowsAB();
        Msgs(await SaveWorkflow(r, Approved(r), initial: r.Confirmed), "initialOptionId").Should().Contain(ApprovalMessages.InitialLocked);
    }

    [Fact]
    public async Task LockedStateMarkedEditable_400()
    {
        var r = await WithFlowsAB();
        Msgs(await SaveWorkflow(r, Approved(r), AllStates(r, r.Confirmed)), S(r.Confirmed))
            .Should().Contain(ApprovalMessages.LockedMarkedEditable("已確認"));
    }

    [Fact]
    public async Task RejectTo_UndefinedOrApproval_400()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("mgr", Manager(1))) with { RejectTo = r.Returned });
        await SeedFlow(r, "po-r", r.Returned, 1, Def(null, Stage("mgr", Manager(1))));

        // 草稿 → 已退回 沒有定義
        Msgs(await SaveWorkflow(r, Approved(r), AllStates(r, r.Returned)), "flows[po-a]")
            .Should().Contain(ApprovalMessages.RejectToInvalid("po-a", "草稿", "已退回"));
        // 草稿 → 已退回 需簽核
        Msgs(await SaveWorkflow(r, Approved(r, Needs(r.Draft, r.Returned)), AllStates(r, r.Returned)), "flows[po-a]")
            .Should().Contain(ApprovalMessages.RejectToInvalid("po-a", "草稿", "已退回"));
        // 草稿 → 已退回 是一般轉移：通過；rejectTo 等於 from 也通過
        Ok(await SaveWorkflow(r, Approved(r, Plain(r.Draft, r.Returned)), AllStates(r, r.Returned)));
    }

    [Fact]
    public async Task RejectTo_LockedFromUnlocked_400()
    {
        var r = await WithFlowsAB();
        await SeedFlow(r, "po-c", r.Cancelled, 1, Def(null, Stage("board", Group(BoardGroup))) with { RejectTo = r.Confirmed });
        var transitions = Approved(r);
        transitions[3] = Needs(r.Confirmed, r.Cancelled);

        Ok(await SaveWorkflow(r, transitions));   // 入口只有 已確認 → 已取消：退回留在原地

        transitions.Add(Needs(r.Draft, r.Cancelled));
        Msgs(await SaveWorkflow(r, transitions), "flows[po-c]").Should().Contain(ApprovalMessages.RejectToLocked("po-c"));
    }

    [Fact]
    public async Task EditableField_InFormulaClosure_400_ListsVia()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(Cond(r.NetTotal, "gte", 100000), Stage("mgr", Manager(1))));
        await SeedFlow(r, "po-b", r.Confirmed, 2, Def(null, Stage("lead", Group(PurchaseLeadGroup), editable: [r.Discount])));

        var msg = Msgs(await SaveWorkflow(r, Approved(r)), "flows[po-b]");
        msg.Should().ContainSingle(m => m.StartsWith("可編輯欄位「折扣」") && m.Contains("折扣後總額") && m.Contains("po-a"));
    }

    [Fact]
    public async Task EditableField_FromLockedState_400()
    {
        var r = await WithFlowsAB();
        await SeedFlow(r, "po-c", r.Cancelled, 1, Def(null, Stage("board", Group(BoardGroup), editable: [r.DueDate])));
        var transitions = Approved(r);
        transitions[3] = Needs(r.Confirmed, r.Cancelled);
        Msgs(await SaveWorkflow(r, transitions), "flows[po-c]").Should().Contain(ApprovalMessages.EditableFromLocked("po-c"));
    }

    [Fact]
    public async Task StatusFieldInCondition_NotInLockedFields()
    {
        var r = await WithFlowsAB();
        await SeedFlow(r, "po-d", r.Confirmed, 3, Def(
            System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                all = new object[]
                {
                    new { field = r.Status, op = "eq", value = Opt(r.Changing) }, new { field = r.Total, op = "lt", value = 100000 }
                }
            }),
            Stage("lead", Group(PurchaseLeadGroup))));

        var res = await SaveWorkflow(r,
            Approved(r, Plain(r.Confirmed, r.Changing), Needs(r.Changing, r.Confirmed, ApprovalNoMatch.Direct), Plain(r.Changing, r.Draft)),
            AllStates(r, r.Changing));
        Ok(res);
        var wf = (WorkflowResponse)res.Data!;
        wf.LockedFieldIds.Should().NotContain(r.Status).And.Contain([r.Total, r.LineSum, r.ExtraSum]);
        wf.EditableStates.Should().BeEquivalentTo([r.Draft, r.Changing]);
    }

    [Fact]
    public async Task EventPushesApprovalTransition_400()
    {
        var r = await WithFlowsAB();
        var input = Event("po-push", r.Extra, new EventTrigger("create", null, null, null),
            UpdateVia("touch", r.Module, r.ExtraRef, Const(r.Status, $"\"{Opt(r.Confirmed)}\"")));

        // 先存事件（狀態機還沒有需簽核），再存需簽核的狀態機 → 擋下
        var created = await Send(new CreateEventRequest(input));
        created.IsCreated.Should().BeTrue(string.Join("；", created.Errors.Select(e => $"{e.Key}={e.Value[0]}")));
        Msgs(await SaveWorkflow(r, Approved(r)), "events[po-push]").Should().Contain(ApprovalMessages.EventPushes("po-push", "已確認"));

        // 狀態機已需簽核時儲存事件 → 擋下
        (await Send(new DeleteEventRequest("po-push"))).IsSuccess.Should().BeTrue();
        Ok(await SaveWorkflow(r, Approved(r)));
        Msgs(await Send(new CreateEventRequest(input)), "definition.effects[0]").Should().Contain(ApprovalMessages.EventPushes("po-push", "已確認"));
    }

    [Fact]
    public async Task SaveWorkflow_ResponseListsLockedStates()
    {
        var r = await WithFlowsAB();
        var res = await SaveWorkflow(r, Approved(r));
        Ok(res);
        var wf = (WorkflowResponse)res.Data!;
        wf.LockedStates.Should().BeEquivalentTo([r.Confirmed, r.Partial, r.Received, r.Cancelled]);
        wf.EditableStates.Should().BeEquivalentTo([r.Draft]);
        wf.LockedFieldIds.Should().Contain([r.Total, r.LineSum, r.ExtraSum]).And.NotContain(r.Memo);

        var got = (WorkflowResponse)(await Send(new GetWorkflowRequest(r.Module))).Data!;
        got.LockedStates.Should().BeEquivalentTo(wf.LockedStates);
        got.LockedFieldIds.Should().BeEquivalentTo(wf.LockedFieldIds);
    }

    [Fact]
    public async Task SaveWorkflow_NoApproval_EmptyLocks()
    {
        var r = await BuildPurchase();
        var res = await SaveWorkflow(r, BaseTransitions(r));
        Ok(res);
        var wf = (WorkflowResponse)res.Data!;
        wf.LockedStates.Should().BeEmpty();
        wf.LockedFieldIds.Should().BeEmpty();
        wf.EditableStates.Should().BeEquivalentTo([r.Draft]);
    }
}
