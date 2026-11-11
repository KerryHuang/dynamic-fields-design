using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 模組關閉任務（任務設計 §4.4）：新增與手動套用回 409、規則不觸發也不持續判斷與同步；既有任務照常可改、留言、列在我的任務；
/// 記錄刪除照樣取消任務；重新開啟不補做，下一次轉變才觸發。
/// </summary>
public class TaskToggleTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed record Rig(string Key, long Title, long Kind, long Exhibition, long Seminar, long Start, long Owner, long Flag);

    private async Task<long> Option(FieldResponse field, string label) =>
        ((OptionResponse)(await Send(new AddOptionRequest(field.Id, Text(label), null))).Data!).Id;

    /// <summary>類型（展覽、研討會）、開始日、負責人、是非欄位；sales 可讀、可新增、可修改全部記錄。</summary>
    private async Task<Rig> BuildRig(string key = "tt")
    {
        await NewModule(key);
        var kind = await MustAdd(key, FieldType.Enum, label: "類型");
        var exhibition = await Option(kind, "展覽");
        var seminar = await Option(kind, "研討會");
        var start = await MustAdd(key, FieldType.Date, label: "開始日");
        var owner = await MustAdd(key, FieldType.User, label: "負責人");
        var flag = await MustAdd(key, FieldType.Boolean, label: "已確認");
        (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        var sales = (await Db.Context.Set<PermissionGroup>().AsNoTracking().SingleAsync(g => g.Key == "sales")).Id;
        (await Send(new SaveModulePermissionsRequest(key, new ModulePermissionsDto(
            [new GroupPermissionDto(sales, new PrivilegesDto(true, true, true, false, false, false, false), DataScope.All)], [], [], []))))
            .IsSuccess.Should().BeTrue();
        return new Rig(key, (await Detail(key)).TitleFieldId!.Value, kind.Id, exhibition, seminar, start.Id, owner.Id, flag.Id);
    }

    private static string Dump(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + "=" + e.Value[0]));

    private async Task<ServiceResult> Run(IRequest<ServiceResult> request, string user = "admin")
    {
        Db.Context.ChangeTracker.Clear();
        As(user);
        try { return await Send(request); }
        finally
        {
            As("admin");
            Db.Context.ChangeTracker.Clear();
        }
    }

    private async Task SetTasks(Rig r, bool on)
    {
        var m = await Detail(r.Key);
        var res = await Run(new UpdateModuleRequest(m.Key, m.Name, m.Icon, m.Section, m.TitleFieldId, m.TitleTemplate, on));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private static string Opt(long id) => $"\"opt_{id}\"";

    private static string S(string s) => JsonSerializer.Serialize(s);

    private JsonElement Eq(long field, string valueJson) => J($$"""{"all":[{"field":{{field}},"op":"eq","value":{{valueJson}}}]}""");

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => $"f_{x.Field}", x => (JsonElement?)J(x.Json));

    /// <summary>onCreate：plan（開始日前 1 天，負責人）、setup（類型是展覽才有）。</summary>
    private async Task OnCreateRule(Rig r) => await CreateRule(r, "on-create", "onCreate", null,
        new TaskRuleTaskInput("plan", Text("規劃"), null, r.Owner, r.Start, -1, null),
        new TaskRuleTaskInput("setup", Text("佈置"), null, r.Owner, null, 0, Eq(r.Kind, Opt(r.Exhibition))));

    /// <summary>onUpdateMatch：已確認變成 true 時產生 check。</summary>
    private async Task OnMatchRule(Rig r) => await CreateRule(r, "on-match", "onUpdateMatch", Eq(r.Flag, "true"),
        new TaskRuleTaskInput("check", Text("確認"), null, r.Owner, null, 0, null));

    private async Task CreateRule(Rig r, string key, string on, JsonElement? when, params TaskRuleTaskInput[] steps)
    {
        var res = await Run(new CreateTaskRuleRequest(r.Key,
            new TaskRuleInput(key, Text(key), true, TaskRuleRepeat.Once, new(on, null, null), when, steps.ToList())));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    private async Task<long> NewRecord(Rig r, bool flag = false, long? kind = null)
    {
        var res = await Run(new CreateRecordRequest(r.Key, Vals(
            (r.Title, S("案")), (r.Owner, S("sales01")), (r.Flag, flag ? "true" : "false"),
            (r.Kind, Opt(kind ?? r.Exhibition)), (r.Start, S("2027-02-01")))));
        res.IsCreated.Should().BeTrue(Dump(res));
        return ((DynamicRecordResponse)res.Data!).Id;
    }

    private async Task<string> RowVersion(long recordId) =>
        (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.Id == recordId)).RowVersion;

    private async Task Change(long recordId, Dictionary<string, JsonElement?> values)
    {
        var res = await Run(new UpdateRecordRequest(recordId, values, await RowVersion(recordId)));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<List<TaskItem>> Tasks(long recordId)
    {
        Db.Context.ChangeTracker.Clear();
        return await Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == recordId).OrderBy(t => t.Id).ToListAsync();
    }

    private async Task<int> Runs(long recordId) =>
        await Db.Context.Set<TaskRuleRun>().AsNoTracking().CountAsync(x => x.RecordId == recordId);

    private async Task<long> AddRecordTask(long recordId, string title = "追蹤")
    {
        var res = await Run(new CreateRecordTaskRequest(recordId, new TaskInput(title, null, "sales01", null)));
        res.IsCreated.Should().BeTrue(Dump(res));
        return ((TaskDetailResponse)res.Data!).Id;
    }

    private static readonly string Disabled = MessageText.Get(null, MessageText.TasksDisabled);

    [Fact]
    public async Task Disabled_CreateRecordTask_409()
    {
        var r = await BuildRig();
        var rec = await NewRecord(r);
        await SetTasks(r, false);

        var res = await Run(new CreateRecordTaskRequest(rec, new TaskInput("追蹤", null, null, null)));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(Disabled);
        (await Tasks(rec)).Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_RecordTasks_ListsExisting_CanAddFalse_NoManualRules()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-rule", "manual", null, new TaskRuleTaskInput("a", Text("準備"), null, r.Owner, null, 0, null));
        var rec = await NewRecord(r);
        var task = await AddRecordTask(rec);
        await SetTasks(r, false);

        var res = await Run(new GetRecordTasksRequest(rec));

        res.IsSuccess.Should().BeTrue(Dump(res));
        var body = (RecordTasksResponse)res.Data!;
        body.Items.Select(i => i.Id).Should().Equal(task);
        body.CanAdd.Should().BeFalse();
        body.ManualRules.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_ManualApply_409_NoRun()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-rule", "manual", null, new TaskRuleTaskInput("a", Text("準備"), null, r.Owner, null, 0, null));
        var rec = await NewRecord(r);
        await SetTasks(r, false);

        var res = await Run(new ApplyTaskRuleRequest(rec, "m-rule"));

        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(Disabled);
        (await Tasks(rec)).Should().BeEmpty();
        (await Runs(rec)).Should().Be(0);
    }

    [Fact]
    public async Task Disabled_OnCreateRule_DoesNotFire()
    {
        var r = await BuildRig();
        await OnCreateRule(r);
        await SetTasks(r, false);

        var rec = await NewRecord(r);

        (await Tasks(rec)).Should().BeEmpty();
        (await Runs(rec)).Should().Be(0);
    }

    [Fact]
    public async Task Disabled_ExistingRuleTasks_NotReconciled()
    {
        var r = await BuildRig();
        await OnCreateRule(r);
        var rec = await NewRecord(r);
        var before = await Tasks(rec);
        before.Select(t => t.RuleTaskCode).Should().Equal("plan", "setup");
        before[0].DueDate.Should().Be(new DateOnly(2027, 1, 31));
        await SetTasks(r, false);

        await Change(rec, Vals((r.Start, S("2027-03-01")), (r.Kind, Opt(r.Seminar)), (r.Owner, S("sales02"))));

        var after = await Tasks(rec);
        after.Should().HaveCount(2);
        after.Single(t => t.RuleTaskCode == "plan").DueDate.Should().Be(new DateOnly(2027, 1, 31), "關閉時不同步到期日");
        after.Single(t => t.RuleTaskCode == "plan").AssigneeUserId.Should().Be("sales01", "關閉時不同步負責人");
        after.Single(t => t.RuleTaskCode == "setup").Status.Should().Be(TaskItemStatus.Todo, "關閉時不做個別任務條件的持續判斷");
    }

    [Fact]
    public async Task Disabled_ExistingTasks_StillEditableAndListed()
    {
        var r = await BuildRig();
        var rec = await NewRecord(r);
        var task = await AddRecordTask(rec);
        await SetTasks(r, false);

        (await Run(new UpdateTaskRequest(task, J("""{"status":"done"}""")))).IsSuccess.Should().BeTrue();
        (await Run(new AddTaskCommentRequest(task, "看一下", []))).IsCreated.Should().BeTrue();
        var mine = await Run(new GetMyTasksRequest("assigned", "all", 1, 20), "sales01");

        mine.IsSuccess.Should().BeTrue(Dump(mine));
        ((PagedResponse<TaskSummaryResponse>)mine.Data!).Items.Select(i => i.Id).Should().Contain(task);
        (await Tasks(rec)).Single().Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public async Task Disabled_RecordDelete_StillCancelsOpenTasks()
    {
        var r = await BuildRig();
        var rec = await NewRecord(r);
        await AddRecordTask(rec);
        await SetTasks(r, false);

        var res = await Run(new DeleteRecordRequest(rec, await RowVersion(rec)));

        res.IsSuccess.Should().BeTrue(Dump(res));
        (await Tasks(rec)).Single().Status.Should().Be(TaskItemStatus.Cancelled);
    }

    [Fact]
    public async Task Reenabled_NoBackfill_NextEdgeFires()
    {
        var r = await BuildRig();
        await OnCreateRule(r);
        await OnMatchRule(r);
        await SetTasks(r, false);
        var rec = await NewRecord(r, flag: false);
        await SetTasks(r, true);

        await Change(rec, Vals((r.Title, S("案改"))));
        (await Tasks(rec)).Should().BeEmpty("重新開啟不補做 onCreate");

        await Change(rec, Vals((r.Flag, "true")));
        (await Tasks(rec)).Select(t => t.RuleTaskCode).Should().Equal("check");
    }
}
