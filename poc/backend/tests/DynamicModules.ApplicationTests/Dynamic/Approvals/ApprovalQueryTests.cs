using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>記錄的簽核狀態與歷程、簽核中心、記錄摘要與 schema 旗標（設計 §10.2、§13.3、§13.4、§15）。</summary>
public class ApprovalQueryTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private async Task<RecordApprovalResponse> RecordApproval(long recordId, string user)
    {
        As(user);
        var res = await Send(new GetRecordApprovalRequest(recordId));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (RecordApprovalResponse)res.Data!;
    }

    private async Task<long> SubmitAs(PurchaseRig r, string user, decimal total)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
    }

    private async Task Approve(long instanceId, string user)
    {
        As(user);
        var task = TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);
        (await Send(new ApproveTaskRequest(task.Id, Inst(instanceId).RowVersion, null, null))).IsSuccess.Should().BeTrue();
    }

    private async Task<PagedResponse<ApprovalInboxItem>> Inbox(string user, string box, string? module = null, int page = 1, int size = 20)
    {
        As(user);
        var res = await Send(new GetApprovalTasksRequest(box, module, null, null, page, size));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (PagedResponse<ApprovalInboxItem>)res.Data!;
    }

    [Fact]
    public async Task RecordApproval_TimelineAndCanWithdraw()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        var po = Inst(id).RecordId;

        var mine = await RecordApproval(po, "buyer1");
        mine.Current!.Id.Should().Be(id);
        mine.Current.Status.Should().Be(ApprovalInstanceStatus.Pending);
        mine.Current.CanWithdraw.Should().BeTrue();
        mine.Current.RowVersion.Should().Be(Inst(id).RowVersion);
        mine.Current.FlowName!.Resolve("zh-TW").Should().Be("po-a");
        mine.Current.Stages.Should().ContainSingle().Which.Tasks.Single().AssigneeUserId.Should().Be("gm");
        mine.Current.Stages[0].Tasks[0].CanAct.Should().BeFalse();   // 發起人不是處理人
        mine.Current.Actions.Select(a => a.Action).Should().Equal(ApprovalActions.Submit);
        mine.Previous.Should().BeEmpty();
        mine.SubmitTargets.Should().BeEmpty();   // 簽核中不能再送

        (await RecordApproval(po, "gm")).Current!.Stages[0].Tasks[0].CanAct.Should().BeTrue();
        await Approve(id, "gm");
        var afterFirst = await RecordApproval(po, "buyer1");
        afterFirst.Current!.Stages.Select(s => (s.StageCode, s.Status)).Should().Equal(("mgr", "approved"), ("board", "active"));
        afterFirst.Current.CanWithdraw.Should().BeTrue();   // 目前關卡還沒有人處理
        await Approve(id, "gm");
        (await RecordApproval(po, "buyer1")).Current!.CanWithdraw.Should().BeFalse();
        (await RecordApproval(po, "fin1")).Current!.CanWithdraw.Should().BeFalse();   // 有 Read 看得到全部，但不是發起人

        // 核准後：Current 為 null，舊實例在 Previous
        await Approve(id, "buyer2");
        var done = await RecordApproval(po, "buyer1");
        done.Current.Should().BeNull();
        done.Previous.Should().ContainSingle().Which.Status.Should().Be(ApprovalInstanceStatus.Approved);
        done.Previous[0].Actions.Select(a => a.Action).Should().EndWith([ApprovalActions.Approve, ApprovalActions.Complete]);
    }

    [Fact]
    public async Task RecordApproval_Visibility_TaskHolderAndOthers404()
    {
        var r = await Scenario3();
        Db.Context.Add(new AppUser { UserId = "outsider", DisplayName = "outsider" });
        await Db.Context.SaveChangesAsync();
        var id = await SubmitAs(r, "buyer1", 50000);
        var po = Inst(id).RecordId;
        As("outsider");
        (await Send(new GetRecordApprovalRequest(po))).IsNotFound.Should().BeTrue();   // 沒有 Read、不是發起人、沒有任務
        (await Send(new GetRecordApprovalRequest(999999))).IsNotFound.Should().BeTrue();

        // 處理人沒有 Read：只看得到自己處理過的那些實例的歷程，SubmitTargets 一律空
        await Db.Context.Set<UserGroupMapping>().Where(m => m.UserId == "buyer2" && m.GroupId != PurchaseLeadGroup).ExecuteDeleteAsync();
        var holder = await RecordApproval(po, "buyer2");
        holder.Current!.Id.Should().Be(id);
        holder.SubmitTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task Inbox_Todo_Done_Submitted()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-big", r.Confirmed, 1, Def(Cond(r.Total, "gte", 100000), Stage("lead", Group(PurchaseLeadGroup), dueHours: 10)));
        await SeedFlow(r, "po-mid", r.Confirmed, 2, Def(Cond(r.Total, "gte", 10000), Stage("lead", Group(PurchaseLeadGroup), dueHours: 5)));
        await SeedFlow(r, "po-any", r.Confirmed, 3, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var small = await SubmitAs(r, "buyer1", 5000);
        var mid = await SubmitAs(r, "buyer1", 50000);
        var big = await SubmitAs(r, "buyer1", 150000);

        var todo = await Inbox("buyer2", "todo");
        todo.Items.Select(i => i.InstanceId).Should().Equal(mid, big, small);   // DueTime 升冪、null 最後
        todo.TotalCount.Should().Be(3);
        todo.Items[0].ModuleKey.Should().Be(r.Module);
        todo.Items[0].InitiatorUserId.Should().Be("buyer1");
        todo.Items[0].StageName!.Resolve("zh-TW").Should().Be("lead");
        todo.Items[0].ModuleClosed.Should().BeFalse();
        todo.Items.Should().OnlyContain(i => !i.Overdue);
        var paged = await Inbox("buyer2", "todo", size: 2);
        paged.Items.Should().HaveCount(2);
        paged.TotalCount.Should().Be(3);

        // 逾期：DueTime 早於現在
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == big)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DueTime, new DateTime(2026, 11, 1)));
        (await Inbox("buyer2", "todo")).Items.Single(i => i.InstanceId == big).Overdue.Should().BeTrue();

        await Approve(mid, "buyer2");
        (await Inbox("buyer2", "todo")).Items.Select(i => i.InstanceId).Should().Equal(big, small);
        var done = await Inbox("buyer2", "done");
        done.Items.Should().ContainSingle().Which.InstanceId.Should().Be(mid);
        done.Items[0].Status.Should().Be(ApprovalInstanceStatus.Approved);

        var submitted = await Inbox("buyer1", "submitted");
        submitted.Items.Select(i => i.InstanceId).Should().BeEquivalentTo([small, mid, big]);
        (await Inbox("buyer1", "todo")).Items.Should().BeEmpty();
        (await Inbox("buyer2", "todo", module: "nope")).Items.Should().BeEmpty();

        As("buyer2");
        (await Send(new GetApprovalTasksRequest("bogus", null, null, null, 1, 20))).Errors.Should().ContainKey("box");
    }

    [Fact]
    public async Task Inbox_Done_DueAndOverdueFollowCurrentStage_NotTheActedTask()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-two", r.Confirmed, 1, Def(null, Stage("lead", Users("buyer2"), dueHours: 5), Stage("fin", Users("gm"))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        await Approve(id, "buyer2");
        // 第 1 關的任務期限早已過去；單據在第 2 關（無期限），buyer2 準時處理
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DueTime, new DateTime(2026, 11, 1)));
        var row = (await Inbox("buyer2", "done")).Items.Should().ContainSingle().Subject;
        row.Overdue.Should().BeFalse();
        row.DueTime.Should().BeNull();
    }

    [Fact]
    public async Task Inbox_All_RequiresApproveAxis_403()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 50000);
        As("buyer2");
        (await Send(new GetApprovalTasksRequest("all", r.Module, null, null, 1, 20))).IsForbidden.Should().BeTrue();
        (await Send(new GetApprovalTasksRequest("all", null, null, null, 1, 20))).IsForbidden.Should().BeTrue();
        var all = await Inbox("admin", "all", r.Module);
        all.Items.Should().ContainSingle().Which.InstanceId.Should().Be(id);
        (await Inbox("admin", "all")).Items.Select(i => i.InstanceId).Should().Contain(id);
    }

    [Fact]
    public async Task OutOfScopeRecord_NotLeaked_RecordApproval404_InboxAllFiltered()
    {
        // 審查測試缺口 1：Read 與 Approve 軸的資料範圍只到自己的記錄（Own），範圍外的記錄不得洩漏標題或欄位值
        var r = await Scenario3();
        var everyone = Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone");
        As("admin");
        (await Send(new SaveModulePermissionsRequest(r.Module, new ModulePermissionsDto(
            [new GroupPermissionDto(everyone.Id, new PrivilegesDto(true, true, true, true, false, true, true), DataScope.Own)], [], [], []))))
            .IsSuccess.Should().BeTrue();

        async Task<long> SubmitTitled(string user, string title)
        {
            As(user);
            var po = await NewPo(r, price: 50000, title: title);
            return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        }
        var other = await SubmitTitled("buyer1", "範圍外的單據");
        var mine = await SubmitTitled("fin1", "自己的單據");
        var otherRecord = Inst(other).RecordId;

        // fin1：有 Read 與 Approve，但記錄在範圍外、不是發起人、也沒有任務 → 404，本文不帶任何實例
        As("fin1");
        var res = await Send(new GetRecordApprovalRequest(otherRecord));
        res.IsNotFound.Should().BeTrue(Dump(res));
        res.Data.Should().BeNull();
        (await Send(new GetRecordRequest(otherRecord))).IsSuccess.Should().BeFalse();   // 對照：記錄本身也讀不到

        // 簽核中心 all：只列範圍內的實例，範圍外的標題快照不出現
        var all = await Inbox("fin1", "all", r.Module);
        all.Items.Select(i => i.InstanceId).Should().Equal(mine);
        all.TotalCount.Should().Be(1);
        all.Items.Should().NotContain(i => i.TitleSnapshot.Contains("範圍外"));
        (await Inbox("fin1", "all")).Items.Select(i => i.InstanceId).Should().Equal(mine);

        // 任務負責人 buyer2 在 todo 看得到（§10.2），但 all 依資料範圍：buyer2 沒有自己的記錄 → 空
        (await Inbox("buyer2", "todo")).Items.Select(i => i.InstanceId).Should().BeEquivalentTo([other, mine]);
        (await Inbox("buyer2", "all")).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecord_HasApprovalSummary()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000);
        async Task<RecordApprovalSummary> Summary() => ((DynamicRecordResponse)(await Send(new GetRecordRequest(po.Id))).Data!).Approval!;
        (await Summary()).Should().Be(new RecordApprovalSummary("none", null, null, true));
        await MustSubmit(po.Id, r.Confirmed);
        var s = await Summary();
        s.Status.Should().Be("pending");
        s.StageSeq.Should().Be(1);
        s.StageName!.Resolve("zh-TW").Should().Be("mgr");
        s.CanSubmit.Should().BeFalse();

        // 沒有需簽核轉移的模組：null
        As("admin");
        var plain = await BuildPurchase("pq");
        await GrantEveryone(plain.Module);
        var other = await NewPo(plain);
        ((DynamicRecordResponse)(await Send(new GetRecordRequest(other.Id))).Data!).Approval.Should().BeNull();
    }

    [Fact]
    public async Task Schema_TransitionApprovalFlag_ChangesETag()
    {
        var r = await BuildPurchase();
        async Task<ModuleSchemaResponse> Schema() => (ModuleSchemaResponse)(await Send(new GetModuleSchemaRequest(r.Module))).Data!;
        var before = await Schema();
        before.Workflow!.Transitions.Should().OnlyContain(t => !t.Approval);
        await SeedFlow(r, "po-s", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        var after = await Schema();
        after.Workflow!.Transitions.Single(t => t.From == $"opt_{r.Draft}" && t.To == $"opt_{r.Confirmed}").Approval.Should().BeTrue();
        after.Workflow.Transitions.Single(t => t.To == $"opt_{r.Partial}").Approval.Should().BeFalse();
        after.PermHash.Should().NotBe(before.PermHash);
    }

    // ── a2b2 審查建議 3、4、6 ──

    private Task RemoveFromEveryone(params string[] users) =>
        Db.Context.Set<UserGroupMapping>().Where(m => users.Contains(m.UserId)
            && m.GroupId == Db.Context.Set<PermissionGroup>().Single(g => g.Key == "everyone").Id).ExecuteDeleteAsync();

    [Fact]
    public async Task RecordApproval_TaskHolderWithoutRead_SeesOnlyOwnStages()
    {
        // 設計 §10.2「任務負責人：自己處理過的關卡」：別關的任務與意見不給
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        await Approve(id, "gm");
        await RemoveFromEveryone("buyer2");
        await Approve(id, "buyer2");   // 會簽：buyer2 同意後 board 仍等 gm
        var view = (await RecordApproval(Inst(id).RecordId, "buyer2")).Current!;
        view.Stages.Select(s => s.StageCode).Should().Equal("board");
        var boardRun = view.Stages.Single().Id;
        view.Actions.Should().NotBeEmpty().And.OnlyContain(a => a.Action != ApprovalActions.Submit);
        Db.Context.Set<ApprovalAction>().AsNoTracking().Where(a => a.InstanceId == id && a.StageRunId != boardRun).Count().Should().BeGreaterThan(0);
        view.Actions.Should().HaveCount(Db.Context.Set<ApprovalAction>().AsNoTracking().Count(a => a.InstanceId == id && a.StageRunId == boardRun));
        // 有 Read 的人照舊看全部
        (await RecordApproval(Inst(id).RecordId, "fin1")).Current!.Stages.Should().HaveCount(2);
    }

    [Fact]
    public async Task RecordApproval_OpenActorWithoutRead_SeesInstanceContentChanged()
    {
        // 設計 §5.5 第 7 點：第 2 關的簽核人看得到第 1 關期間金額為什麼變了（content-changed 掛在當時的關卡）
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 150000);
        As("buyer1");
        var extra = await Send(new Application.Dynamic.RecordService.Commands.CreateRecord.CreateRecordRequest(r.Extra,
            V((TitleId(r.Extra), "追加"), (r.ExtraRef, Inst(id).RecordId), (r.ExtraAmount, 1000))));
        extra.IsCreated.Should().BeTrue(Dump(extra));
        await Approve(id, "gm");
        await RemoveFromEveryone("buyer2");
        var view = (await RecordApproval(Inst(id).RecordId, "buyer2")).Current!;
        view.Stages.Select(s => s.StageCode).Should().Equal("board");
        var changed = view.Actions.Should().ContainSingle(a => a.Action == ApprovalActions.ContentChanged).Subject;
        changed.Data!.Value.EnumerateArray().Select(e => e.GetProperty("fieldId").GetInt64()).Should().Contain(r.Total);
        view.Actions.Should().NotContain(a => a.Action == ApprovalActions.Submit);   // 其他歷程照舊只給自己的關卡
    }

    [Fact]
    public async Task RecordApproval_ClosedWithoutActing_NotVisible()
    {
        // 或簽時別人先同意、自己的任務被關閉：從沒動作過，看不到這個實例（沒有 Read → 404）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-f", r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        var id = await SubmitAs(r, "buyer1", 5000);
        await RemoveFromEveryone("fin1", "fin2");
        await Approve(id, "fin1");
        As("fin2");
        (await Send(new GetRecordApprovalRequest(Inst(id).RecordId))).IsNotFound.Should().BeTrue();
        (await RecordApproval(Inst(id).RecordId, "fin1")).Previous.Should().ContainSingle();   // 動作過的人看得到
    }

    [Fact]
    public async Task Inbox_All_DeletedRecord_HiddenLikeLimitedScope()
    {
        var r = await Scenario3();
        var id = await SubmitAs(r, "buyer1", 50000);
        As("buyer1");
        await Send(new WithdrawApprovalRequest(id, Inst(id).RowVersion, null));
        await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Where(x => x.Id == Inst(id).RecordId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeleteFlag, true));
        (await Inbox("admin", "all", r.Module)).Items.Should().BeEmpty();
        (await Inbox("admin", "all")).Items.Should().NotContain(i => i.InstanceId == id);
        (await Inbox("buyer1", "submitted")).Items.Should().Contain(i => i.InstanceId == id);   // 發起人仍看得到自己送出的（§10.2 永久可看）
    }

    [Fact]
    public async Task Inbox_Todo_ExcludesTasksUserCannotAct()
    {
        // 會簽兩個備援席位共用 gm、mgrA：gm 同意 fb:0 後，fb:1 的 gm 任務仍 open，但 todo 不列（點下去會 409）
        var r = await BuildPurchase();
        await SeedFlow(r, "po-n", r.Confirmed, 1,
            Def(null, Stage("both", Users("emp1", "emp2"), mode: ApprovalModes.All)) with { FallbackApprovers = Users("gm", "mgrA") });
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        await Db.Context.Set<AppUser>().Where(u => u.UserId == "emp1" || u.UserId == "emp2").ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        var id = await SubmitAs(r, "buyer1", 5000);
        (await Inbox("gm", "todo")).Items.Should().HaveCount(2);
        var first = TasksOf(id).Where(t => t.AssigneeUserId == "gm").OrderBy(t => t.SeatKey).First();
        As("gm");
        (await Send(new ApproveTaskRequest(first.Id, Inst(id).RowVersion, null, null))).IsSuccess.Should().BeTrue();
        TasksOf(id).Count(t => t.AssigneeUserId == "gm" && t.Status == ApprovalTaskStatus.Open).Should().Be(1);
        (await Inbox("gm", "todo")).Items.Should().BeEmpty();
        (await Inbox("mgrA", "todo")).Items.Should().ContainSingle().Which.InstanceId.Should().Be(id);
    }

    // ───────── a4b1：可編輯欄位、標題快照、狀態篩選 ─────────

    /// <summary>草稿 → 已確認 需簽核：第 1 關採購主管群組、第 2 關財務群組（可編輯交期）；已確認 → 已取消 也需簽核（財務單關，不可編輯：定義約束不允許鎖定起點的流程有可編輯欄位）。</summary>
    private async Task<PurchaseRig> EditableRig()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup)), Stage("fin", Group(FinanceGroup), editable: [r.DueDate])));
        await SeedFlow(r, "po-x", r.Cancelled, 1, Def(null, Stage("fin", Group(FinanceGroup))));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Needs(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
        return r;
    }

    private ApprovalStageView StageOf(RecordApprovalResponse view, string code) => view.Current!.Stages.Single(s => s.StageCode == code);

    [Fact]
    public async Task RecordApproval_ActorOnEditableStage_ListsEditableFieldKeys()
    {
        var r = await EditableRig();
        var id = await SubmitAs(r, "buyer1", 1000);
        await Approve(id, "buyer2");
        var po = Inst(id).RecordId;

        var view = await RecordApproval(po, "fin1");

        StageOf(view, "fin").EditableFieldKeys.Should().Equal($"f_{r.DueDate}");
        StageOf(view, "lead").EditableFieldKeys.Should().BeEmpty();   // 已完成的關卡
    }

    [Fact]
    public async Task RecordApproval_NonActor_EditableFieldKeysEmpty()
    {
        var r = await EditableRig();
        var id = await SubmitAs(r, "buyer1", 1000);
        await Approve(id, "buyer2");
        var po = Inst(id).RecordId;

        (await RecordApproval(po, "buyer1")).Current!.Stages.Should().OnlyContain(s => s.EditableFieldKeys.Count == 0);   // 發起人
        (await RecordApproval(po, "emp1")).Current!.Stages.Should().OnlyContain(s => s.EditableFieldKeys.Count == 0);   // 有 Read 的旁觀者
    }

    [Fact]
    public async Task RecordApproval_StageNotYetActive_EditableFieldKeysEmpty()
    {
        var r = await EditableRig();
        var id = await SubmitAs(r, "buyer1", 1000);   // 第 1 關進行中，財務關還沒開始

        (await RecordApproval(Inst(id).RecordId, "fin1")).Current!.Stages.Should().OnlyContain(s => s.EditableFieldKeys.Count == 0);
    }

    [Fact]
    public async Task RecordApproval_EditableFieldHiddenForActor_NotListed()
    {
        var r = await EditableRig();
        var id = await SubmitAs(r, "buyer1", 1000);
        await Approve(id, "buyer2");
        As("admin");
        await HideFor(r.Module, r.DueDate, FinanceGroup);

        var view = await RecordApproval(Inst(id).RecordId, "fin1");

        StageOf(view, "fin").EditableFieldKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordApproval_EditableFieldReadOnlyForActor_NotListed()
    {
        // a4b4 審查 U3：欄位例外設成唯讀的欄位，不列在「這一關可修改」
        var r = await EditableRig();
        var id = await SubmitAs(r, "buyer1", 1000);
        await Approve(id, "buyer2");
        As("admin");
        await ReadOnlyFor(r.Module, r.DueDate, FinanceGroup);

        var view = await RecordApproval(Inst(id).RecordId, "fin1");

        StageOf(view, "fin").EditableFieldKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordApproval_FromLockedState_EditableFieldKeysEmpty()
    {
        var r = await EditableRig();
        var first = await SubmitAs(r, "buyer1", 1000);
        await Approve(first, "buyer2");
        await Approve(first, "fin1");
        var po = Inst(first).RecordId;
        StateOf(r, po).Should().Be(r.Confirmed);
        // 定義約束不允許存這種流程，直接改版本內容模擬舊資料或繞過驗證的流程
        var version = Db.Context.Set<ApprovalFlowVersion>().Single(v => v.Id == Flow("po-x").CurrentVersionId);
        version.Definition = Def(null, Stage("fin", Group(FinanceGroup), editable: [r.DueDate]));
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        As("buyer1");
        var voided = (await MustSubmit(po, r.Cancelled)).InstanceId!.Value;   // 作廢：from 是鎖定狀態

        var view = await RecordApproval(po, "fin1");

        view.Current!.Id.Should().Be(voided);
        view.Current.Stages.Single().Tasks.Single(t => t.AssigneeUserId == "fin1").CanAct.Should().BeTrue();
        view.Current.Stages.Single().EditableFieldKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordApproval_TitleHiddenForReader_SnapshotEmptyForBystanderOnly()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000, title: "採購甲");
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        // 設計器不允許把標題用到的欄位設為隱藏，所以先隱藏備註，再直接把標題樣板改成引用它（模擬標題欄位對讀者隱藏）
        As("admin");
        await HideFor(r.Module, r.Memo, Db.Context.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "everyone").Id);
        await Db.Context.Set<Module>().Where(m => m.Key == r.Module).ExecuteUpdateAsync(u => u.SetProperty(m => m.TitleTemplate, $"{{{r.Memo}}}"));
        Db.Context.ChangeTracker.Clear();

        (await RecordApproval(po.Id, "emp1")).Current!.TitleSnapshot.Should().BeEmpty();   // 有 Read、標題欄位被隱藏的旁觀者
        (await RecordApproval(po.Id, "buyer1")).Current!.TitleSnapshot.Should().Be("採購甲");   // 發起人
        (await RecordApproval(po.Id, "gm")).Current!.TitleSnapshot.Should().Be("採購甲");   // 有任務的人
        Inst(id).TitleSnapshot.Should().Be("採購甲");
    }

    [Fact]
    public async Task RecordApproval_InstanceHasTitleSnapshot()
    {
        var r = await Scenario3();
        As("buyer1");
        var po = await NewPo(r, price: 150000, title: "採購甲");
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;

        (await RecordApproval(po.Id, "buyer1")).Current!.TitleSnapshot.Should().Be("採購甲").And.Be(Inst(id).TitleSnapshot);
    }

    [Fact]
    public async Task Inbox_StatusFilter_Rejected_OnlyRejectedInstances()
    {
        var r = await Scenario3();
        var rejected = await SubmitAs(r, "buyer1", 50000);
        var pending = await SubmitAs(r, "buyer1", 50000);
        As("buyer2");
        var task = TasksOf(rejected).Single(t => t.AssigneeUserId == "buyer2" && t.Status == ApprovalTaskStatus.Open);
        (await Send(new RejectTaskRequest(task.Id, Inst(rejected).RowVersion, "請補資料", RejectTaskHandler.ToInitiator))).IsSuccess.Should().BeTrue();

        As("buyer1");
        var res = await Send(new GetApprovalTasksRequest("submitted", null, null, null, 1, 20, "rejected"));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var page = (PagedResponse<ApprovalInboxItem>)res.Data!;
        page.Items.Select(i => i.InstanceId).Should().Equal(rejected);
        page.TotalCount.Should().Be(1);
        var all = await Inbox("buyer1", "submitted");
        all.Items.Select(i => i.InstanceId).Should().BeEquivalentTo([rejected, pending]);
    }

    [Fact]
    public async Task Inbox_StatusInvalid_Returns400()
    {
        await Scenario3();
        As("buyer1");

        var res = await Send(new GetApprovalTasksRequest("submitted", null, null, null, 1, 20, "x"));

        res.Errors.Should().ContainKey("status");
    }
}
