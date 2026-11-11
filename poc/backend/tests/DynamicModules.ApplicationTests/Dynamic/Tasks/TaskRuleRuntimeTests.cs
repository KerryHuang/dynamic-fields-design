using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Commands.SaveConversion;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 任務規則的執行（設計 §5.2 到 §5.6）：觸發只產生一次或每次符合都產生、個別任務條件持續判斷、來源記錄變動時同步、
/// 規則停用與刪除、應用程式關閉、匯入／轉換／事件寫入也觸發、通知與語系。
/// </summary>
public class TaskRuleRuntimeTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed class SwitchableLocale : ICurrentLocale
    {
        public string Locale { get; set; } = I18nText.FallbackLocale;
    }

    private readonly SwitchableLocale _locale = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<ICurrentLocale>(_locale);

    protected sealed record Rig(
        string Key, long Title, long Kind, long Exhibition, long Seminar, long Start, long End,
        long PlanOwner, long SetupOwner, long FeedbackOwner, long Stage, long Designing, long Confirming, long Flag);

    private async Task<long> Option(FieldResponse field, string label) =>
        ((OptionResponse)(await Send(new AddOptionRequest(field.Id, Text(label), null))).Data!).Id;

    /// <summary>類型（兩個選項）、開始日、結束日、三個人員欄位、進度（兩個選項）、是非欄位；sales 與 sales-manager 可讀、可新增、可修改全部記錄。</summary>
    private async Task<Rig> BuildRig(string key = "cp")
    {
        await NewModule(key);
        var kind = await MustAdd(key, FieldType.Enum, label: "類型");
        var exhibition = await Option(kind, "展覽");
        var seminar = await Option(kind, "研討會");
        var start = await MustAdd(key, FieldType.Date, label: "開始日");
        var end = await MustAdd(key, FieldType.Date, label: "結束日");
        var plan = await MustAdd(key, FieldType.User, label: "規劃負責人");
        var setup = await MustAdd(key, FieldType.User, label: "佈置負責人");
        var feedback = await MustAdd(key, FieldType.User, label: "回饋負責人");
        var stage = await MustAdd(key, FieldType.Enum, label: "進度");
        var designing = await Option(stage, "設計中");
        var confirming = await Option(stage, "客戶確認中");
        var flag = await MustAdd(key, FieldType.Boolean, label: "已確認");
        (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        var groups = await Db.Context.Set<PermissionGroup>().AsNoTracking().ToListAsync();
        var all = new PrivilegesDto(true, true, true, false, false, false, false);
        (await Send(new SaveModulePermissionsRequest(key, new ModulePermissionsDto(
        [
            new GroupPermissionDto(groups.Single(g => g.Key == "sales").Id, all, DataScope.All),
            new GroupPermissionDto(groups.Single(g => g.Key == "sales-manager").Id, all, DataScope.All)
        ], [], [], [])))).IsSuccess.Should().BeTrue();
        var m = await Detail(key);
        return new Rig(key, m.TitleFieldId!.Value, kind.Id, exhibition, seminar, start.Id, end.Id,
            plan.Id, setup.Id, feedback.Id, stage.Id, designing, confirming, flag.Id);
    }

    private static string Dump(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + "=" + e.Value[0]));

    private static string Opt(long id) => $"\"opt_{id}\"";

    private JsonElement Eq(long field, string valueJson) => J($$"""{"all":[{"field":{{field}},"op":"eq","value":{{valueJson}}}]}""");

    private static TaskRuleTaskInput Def(string code, string title, long assignee, long? due, int offset, JsonElement? when = null,
        I18nText? titleI18n = null) => new(code, titleI18n ?? Text(title), null, assignee, due, offset, when);

    private TaskRuleTaskInput[] CampaignTasks(Rig r) =>
    [
        Def("plan", "規劃活動內容", r.PlanOwner, r.Start, -62),
        Def("setup", "展場佈置", r.SetupOwner, r.Start, -1, Eq(r.Kind, Opt(r.Exhibition))),
        Def("feedback", "活動回饋", r.FeedbackOwner, r.End, 12)
    ];

    private static readonly TaskRuleTriggerInput OnCreate = new("onCreate", null, null);

    private static TaskRuleInput RuleInput(string key, TaskRuleTriggerInput trigger, TaskRuleTaskInput[] tasks, JsonElement? when = null,
        TaskRuleRepeat repeat = TaskRuleRepeat.Once, bool active = true) =>
        new(key, Text(key), active, repeat, trigger, when, tasks.ToList());

    private async Task CreateRule(Rig r, TaskRuleInput input)
    {
        var res = await Run(new CreateTaskRuleRequest(r.Key, input));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    /// <summary>每個請求前後清掉共用 DbContext 的追蹤器，與正式環境每個請求一個 DbContext 一樣，不會讀到上一個請求留下的實體。</summary>
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

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string? Json)[] v) =>
        v.ToDictionary(x => $"f_{x.Field}", x => x.Json is null ? (JsonElement?)null : J(x.Json));

    private static string S(string s) => JsonSerializer.Serialize(s);

    private static Dictionary<string, JsonElement?> Campaign(Rig r, long kind, string title = "春季展", string? start = "2027-02-01",
        string? end = "2027-02-03", string? plan = "sales01", string? setup = "sales02", string? feedback = "manager01")
    {
        var v = Vals((r.Title, S(title)), (r.Kind, Opt(kind)));
        if (start is not null) v[$"f_{r.Start}"] = J(S(start));
        if (end is not null) v[$"f_{r.End}"] = J(S(end));
        if (plan is not null) v[$"f_{r.PlanOwner}"] = J(S(plan));
        if (setup is not null) v[$"f_{r.SetupOwner}"] = J(S(setup));
        if (feedback is not null) v[$"f_{r.FeedbackOwner}"] = J(S(feedback));
        return v;
    }

    private async Task<long> NewRecord(Rig r, Dictionary<string, JsonElement?> values, string user = "admin")
    {
        var res = await Run(new CreateRecordRequest(r.Key, values), user);
        res.IsCreated.Should().BeTrue(Dump(res));
        return ((DynamicRecordResponse)res.Data!).Id;
    }

    private async Task<string> RowVersion(long recordId) =>
        (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.Id == recordId)).RowVersion;

    private async Task Change(long recordId, Dictionary<string, JsonElement?> values, string user = "admin")
    {
        var res = await Run(new UpdateRecordRequest(recordId, values, await RowVersion(recordId)), user);
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task Patch(long taskId, string json)
    {
        var res = await Run(new UpdateTaskRequest(taskId, J(json)));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<List<TaskItem>> Tasks(long recordId)
    {
        Db.Context.ChangeTracker.Clear();
        return await Db.Context.Set<TaskItem>().AsNoTracking().Include(t => t.Participants)
            .Where(t => t.RecordId == recordId).OrderBy(t => t.Id).ToListAsync();
    }

    private static TaskItem Code(IEnumerable<TaskItem> tasks, string code, int occurrence = 1) =>
        tasks.Single(t => t.RuleTaskCode == code && t.RuleOccurrence == occurrence);

    private async Task<List<string?>> Events(long taskId) =>
        await Db.Context.Set<TaskComment>().AsNoTracking().Where(c => c.TaskId == taskId && c.Kind == TaskCommentKind.System)
            .OrderBy(c => c.Id).Select(c => c.SystemEvent).ToListAsync();

    private async Task<List<TaskRuleRun>> Runs(long recordId) =>
        await Db.Context.Set<TaskRuleRun>().AsNoTracking().Where(x => x.RecordId == recordId).OrderBy(x => x.Occurrence).ToListAsync();

    private static string Line(TaskItem t) => $"{t.RuleTaskCode}|{t.DueDate:yyyy-MM-dd}|{t.AssigneeUserId}";

    // ───────── 產生 ─────────

    [Fact]
    public async Task OnCreate_GeneratesThreeTasks_WithExactDueDates()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));

        var rec = await NewRecord(r, Campaign(r, r.Exhibition), "sales01");

        var tasks = await Tasks(rec);
        tasks.Select(Line).Should().Equal("plan|2026-12-01|sales01", "setup|2027-01-31|sales02", "feedback|2027-02-15|manager01");
        tasks.Select(t => t.Title).Should().Equal("規劃活動內容", "展場佈置", "活動回饋");
        tasks.Should().OnlyContain(t => t.CreatedBy == null && t.RuleOccurrence == 1 && t.Status == TaskItemStatus.Todo
                                        && t.RecordTitleSnapshot == "春季展" && t.ModuleId != null);
        (await Events(tasks[0].Id)).Should().Equal(TaskSystemEvents.Created);
        var run = (await Runs(rec)).Should().ContainSingle().Subject;
        run.Occurrence.Should().Be(1);
        run.TriggeredBy.Should().Be("sales01");
    }

    [Fact]
    public async Task OnCreate_SeminarSkipsConditionalTask()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));

        var rec = await NewRecord(r, Campaign(r, r.Seminar));

        (await Tasks(rec)).Select(t => t.RuleTaskCode).Should().Equal("plan", "feedback");
    }

    // ───────── 個別任務條件持續判斷 ─────────

    [Fact]
    public async Task TaskCondition_BecomesTrue_GeneratesMissing()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Seminar));

        await Change(rec, Vals((r.Kind, Opt(r.Exhibition))));

        var tasks = await Tasks(rec);
        tasks.Select(t => t.RuleTaskCode).Should().Equal("plan", "feedback", "setup");
        Line(Code(tasks, "setup")).Should().Be("setup|2027-01-31|sales02");
        (await Runs(rec)).Should().ContainSingle("補產生不是新的觸發");
    }

    [Fact]
    public async Task TaskCondition_BecomesFalse_Cancels_ThenRestoresSameTask()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        var setupId = Code(await Tasks(rec), "setup").Id;

        await Change(rec, Vals((r.Kind, Opt(r.Seminar))));
        var cancelled = Code(await Tasks(rec), "setup");
        cancelled.Status.Should().Be(TaskItemStatus.Cancelled);
        cancelled.CancelledByRule.Should().BeTrue();
        (await Events(setupId)).Should().Contain(TaskSystemEvents.ConditionUnmet);

        await Change(rec, Vals((r.Kind, Opt(r.Exhibition))));
        var tasks = await Tasks(rec);
        tasks.Should().HaveCount(3, "恢復同一筆，不另建");
        var restored = Code(tasks, "setup");
        restored.Id.Should().Be(setupId);
        restored.Status.Should().Be(TaskItemStatus.Todo);
        restored.CancelledByRule.Should().BeFalse();
        (await Events(setupId)).Last().Should().Be(TaskSystemEvents.ConditionRestored);
    }

    [Fact]
    public async Task Restored_Task_ResyncsAssigneeAndDue()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        var overriddenRec = await NewRecord(r, Campaign(r, r.Exhibition));
        await Patch(Code(await Tasks(overriddenRec), "setup").Id, """{"assigneeUserId":"manager01"}""");

        foreach (var id in new[] { rec, overriddenRec })
        {
            await Change(id, Vals((r.Kind, Opt(r.Seminar))));
            await Change(id, Vals((r.Start, S("2027-03-01")), (r.SetupOwner, S("sales01"))));
            await Change(id, Vals((r.Kind, Opt(r.Exhibition))));
        }

        Line(Code(await Tasks(rec), "setup")).Should().Be("setup|2027-02-28|sales01", "恢復時依目前記錄重新同步");
        Line(Code(await Tasks(overriddenRec), "setup")).Should().Be("setup|2027-02-28|manager01", "負責人被人改過不覆蓋，到期日照同步");
    }

    [Fact]
    public async Task TaskCondition_ManualCancelOrDone_Untouched()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var a = await NewRecord(r, Campaign(r, r.Exhibition, title: "甲"));
        var b = await NewRecord(r, Campaign(r, r.Exhibition, title: "乙"));
        await Patch(Code(await Tasks(a), "setup").Id, """{"status":"cancelled"}""");
        await Patch(Code(await Tasks(b), "setup").Id, """{"status":"done"}""");

        await Change(a, Vals((r.Kind, Opt(r.Seminar))));
        await Change(b, Vals((r.Kind, Opt(r.Seminar))));
        await Change(a, Vals((r.Kind, Opt(r.Exhibition))));

        var manual = Code(await Tasks(a), "setup");
        manual.Status.Should().Be(TaskItemStatus.Cancelled, "人手動取消的不自動恢復");
        manual.CancelledByRule.Should().BeFalse();
        (await Tasks(a)).Should().HaveCount(3);
        Code(await Tasks(b), "setup").Status.Should().Be(TaskItemStatus.Done);
    }

    // ───────── 觸發方式 ─────────

    [Fact]
    public async Task OnUpdateMatch_OnlyOnTransition_AndOnCreateWhenMatching()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-check", new("onUpdateMatch", null, null),
            [Def("check", "確認報名", r.PlanOwner, null, 0)], when: Eq(r.Flag, "true")));

        var a = await NewRecord(r, Vals((r.Title, S("甲")), (r.Flag, "false"), (r.PlanOwner, S("sales01"))));
        (await Tasks(a)).Should().BeEmpty();
        await Change(a, Vals((r.Title, S("甲改"))));
        (await Tasks(a)).Should().BeEmpty("改到無關欄位不觸發");

        await Change(a, Vals((r.Flag, "true")));
        (await Tasks(a)).Should().ContainSingle();
        await Change(a, Vals((r.Flag, "false")));
        await Change(a, Vals((r.Flag, "true")));
        (await Tasks(a)).Should().ContainSingle("once 只觸發一次");
        (await Runs(a)).Should().ContainSingle();

        var b = await NewRecord(r, Vals((r.Title, S("乙")), (r.Flag, "true"), (r.PlanOwner, S("sales01"))));
        (await Tasks(b)).Should().ContainSingle("建立時就符合也算一次");
    }

    [Fact]
    public async Task OnFieldBecomes_Once_EvenWhenToggled()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-design", new("onFieldBecomes", r.Stage, J(Opt(r.Designing))),
            [Def("draft", "繪製設計稿", r.PlanOwner, null, 0)]));
        var rec = await NewRecord(r, Vals((r.Title, S("案")), (r.Stage, Opt(r.Confirming)), (r.PlanOwner, S("sales01"))));
        (await Tasks(rec)).Should().BeEmpty();

        await Change(rec, Vals((r.Stage, Opt(r.Designing))));
        (await Tasks(rec)).Should().ContainSingle();
        await Change(rec, Vals((r.Stage, Opt(r.Confirming))));
        await Change(rec, Vals((r.Stage, Opt(r.Designing))));

        (await Tasks(rec)).Should().ContainSingle();
        (await Runs(rec)).Should().ContainSingle();
    }

    // ───────── 同步 ─────────

    [Fact]
    public async Task EmptyAssignee_Unassigned_EmptyDue_ThenSyncWhenFilled()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Seminar, start: null, plan: null));
        var plan = Code(await Tasks(rec), "plan");
        plan.AssigneeUserId.Should().BeNull();
        plan.DueDate.Should().BeNull();

        await Change(rec, Vals((r.PlanOwner, S("sales02")), (r.Start, S("2027-03-01"))));

        Line(Code(await Tasks(rec), "plan")).Should().Be("plan|2026-12-29|sales02");
        (await Db.Context.Set<Notification>().AsNoTracking()
            .CountAsync(n => n.UserId == "sales02" && n.Kind == NotificationKinds.TaskAssigned && n.SourceId == plan.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Sync_OnlyOpenAndNotOverridden_OldAssigneeStaysParticipant()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        var tasks = await Tasks(rec);
        await Patch(Code(tasks, "setup").Id, """{"assigneeUserId":"manager01"}""");
        await Patch(Code(tasks, "feedback").Id, """{"status":"done"}""");

        await Change(rec, Vals((r.PlanOwner, S("manager01")), (r.SetupOwner, S("sales01")), (r.FeedbackOwner, S("sales01")),
            (r.Start, S("2027-03-01")), (r.End, S("2027-03-03"))));

        tasks = await Tasks(rec);
        var plan = Code(tasks, "plan");
        Line(plan).Should().Be("plan|2026-12-29|manager01");
        plan.Participants.Where(p => p.Reason == TaskParticipantReason.Assignee).Select(p => p.UserId)
            .Should().BeEquivalentTo(["sales01", "manager01"], "舊負責人保留為參與者");
        Line(Code(tasks, "setup")).Should().Be("setup|2027-02-28|manager01", "負責人被人改過就不跟著變，到期日照樣同步");
        Line(Code(tasks, "feedback")).Should().Be("feedback|2027-02-15|manager01", "已完成的不動");

        await Patch(plan.Id, """{"dueDate":"2027-01-05"}""");
        await Change(rec, Vals((r.Start, S("2027-04-01"))));
        tasks = await Tasks(rec);
        Code(tasks, "plan").DueDate.Should().Be(new DateOnly(2027, 1, 5), "到期日被人改過就不跟著變");
        Code(tasks, "setup").DueDate.Should().Be(new DateOnly(2027, 3, 31));
    }

    [Fact]
    public async Task Sync_UsesCurrentDefinition()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        var tasks = CampaignTasks(r);
        tasks[0] = tasks[0] with { DueOffsetDays = -30 };
        (await Run(new UpdateTaskRuleRequest(r.Key, "cp-tasks", RuleInput("cp-tasks", OnCreate, tasks)))).IsSuccess.Should().BeTrue();

        await Change(rec, Vals((r.Start, S("2027-03-01"))));

        Code(await Tasks(rec), "plan").DueDate.Should().Be(new DateOnly(2027, 1, 30));
    }

    // ───────── 規則停用、刪除、應用程式關閉 ─────────

    [Fact]
    public async Task InactiveRule_NoTriggerNoSync()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        (await Run(new UpdateTaskRuleRequest(r.Key, "cp-tasks", RuleInput("cp-tasks", OnCreate, CampaignTasks(r), active: false))))
            .IsSuccess.Should().BeTrue();

        await Change(rec, Vals((r.PlanOwner, S("manager01"))));
        var fresh = await NewRecord(r, Campaign(r, r.Exhibition, title: "新"));

        Code(await Tasks(rec), "plan").AssigneeUserId.Should().Be("sales01");
        (await Tasks(fresh)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeletedRule_TaskKept_RuleDeletedTrue()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        (await Run(new DeleteTaskRuleRequest(r.Key, "cp-tasks"))).IsSuccess.Should().BeTrue();

        await Change(rec, Vals((r.PlanOwner, S("manager01"))));

        var tasks = await Tasks(rec);
        tasks.Should().HaveCount(3);
        Code(tasks, "plan").AssigneeUserId.Should().Be("sales01");
        var detail = (TaskDetailResponse)(await Run(new GetTaskRequest(tasks[0].Id))).Data!;
        detail.RuleDeleted.Should().BeTrue();
        detail.RuleKey.Should().BeNull();
    }

    [Fact]
    public async Task ClosedApp_RuleDoesNotFire_TasksKept()
    {
        var r = await BuildRig();
        var app = new App { Key = "cp-app", Kind = AppKind.App, Status = AppStatus.Open, Source = "builtIn", RowVersion = "x" };
        Db.Context.Add(app);
        await Db.Context.SaveChangesAsync();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));
        await Db.Context.Set<TaskRule>().Where(x => x.Key == "cp-tasks").ExecuteUpdateAsync(s => s.SetProperty(x => x.AppId, app.Id));
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        (await Tasks(rec)).Should().HaveCount(3, "應用程式開啟時照常觸發");

        await Db.Context.Set<App>().Where(x => x.Id == app.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AppStatus.Closed));
        await Change(rec, Vals((r.PlanOwner, S("manager01")), (r.Kind, Opt(r.Seminar))));
        var fresh = await NewRecord(r, Campaign(r, r.Exhibition, title: "新"));

        var tasks = await Tasks(rec);
        tasks.Should().HaveCount(3);
        tasks.Should().OnlyContain(t => t.Status == TaskItemStatus.Todo, "關閉期間不取消、不同步");
        Code(tasks, "plan").AssigneeUserId.Should().Be("sales01");
        (await Tasks(fresh)).Should().BeEmpty();
    }

    // ───────── 其他寫入路徑 ─────────

    [Fact]
    public async Task ImportConvertAndEventWrites_TriggerRules()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-plan", OnCreate, [Def("plan", "規劃活動內容", r.PlanOwner, r.Start, -62)]));
        var moduleId = (await Detail(r.Key)).Id;
        async Task<List<TaskItem>> TasksOf(string title)
        {
            var id = (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.ModuleId == moduleId && x.Title == title)).Id;
            return await Tasks(id);
        }

        // 匯入
        var csv = Encoding.UTF8.GetBytes($"f_{r.Title},f_{r.PlanOwner},f_{r.Start}\r\n匯入展,sales01,2027-02-01\r\n");
        var imported = await Run(new ImportRecordsRequest(r.Key, csv));
        imported.IsSuccess.Should().BeTrue(Dump(imported));
        Line((await TasksOf("匯入展")).Should().ContainSingle().Subject).Should().Be("plan|2026-12-01|sales01");

        // 事件 create 效果的目標模組
        await NewModule("cp-src", activate: true);
        var srcTitle = (await Detail("cp-src")).TitleFieldId!.Value;
        var ev = await Run(new CreateEventRequest(new EventInput("cp-src-post", Text("建立活動"), "cp-src", true,
            new EventDefinition(new EventTrigger("create", null, null, null), null,
            [
                new EventEffect("mk", "create", r.Key, null, null,
                [
                    new EventSet(r.Title, null, null, J(S("事件展")), false),
                    new EventSet(r.PlanOwner, null, null, J(S("sales02")), false),
                    new EventSet(r.Start, null, null, J(S("2027-02-01")), false)
                ], null, null, null, null)
            ]))));
        ev.IsCreated.Should().BeTrue(Dump(ev));
        (await Run(new CreateRecordRequest("cp-src", Vals((srcTitle, S("來源")))))).IsCreated.Should().BeTrue();
        Line((await TasksOf("事件展")).Should().ContainSingle().Subject).Should().Be("plan|2026-12-01|sales02");

        // 單據轉換（表頭）
        await NewModule("cp-cv", activate: true);
        var cvTitle = (await Detail("cp-cv")).TitleFieldId!.Value;
        var from = await MustAdd(r.Key, FieldType.Reference, RefTo("cp-cv") with { ReadOnly = true }, "來源");
        var conv = await Run(new CreateConversionRequest(new ConversionInput("cp-conv", Text("轉活動"), "cp-cv", r.Key, true,
            new ConversionDefinition([], new ConversionHeader(from.Id, [new FillMapping(cvTitle, r.Title)]), null))));
        conv.IsSuccess.Should().BeTrue(Dump(conv));
        var src = await Run(new CreateRecordRequest("cp-cv", Vals((cvTitle, S("轉換展")))));
        var converted = await Run(new ConvertRecordRequest(((DynamicRecordResponse)src.Data!).Id, "cp-conv", null, null, null));
        converted.IsCreated.Should().BeTrue(Dump(converted));
        var task = (await TasksOf("轉換展")).Should().ContainSingle().Subject;
        task.AssigneeUserId.Should().BeNull("轉換沒帶負責人，任務照樣產生、未指派");
    }

    // ───────── 通知與語系 ─────────

    [Fact]
    public async Task RuleTask_NotifiesAssigneeExceptTriggeringUser()
    {
        var r = await BuildRig();
        await CreateRule(r, RuleInput("cp-tasks", OnCreate, CampaignTasks(r)));

        await NewRecord(r, Campaign(r, r.Exhibition), "sales01");

        var assigned = await Db.Context.Set<Notification>().AsNoTracking().Where(n => n.Kind == NotificationKinds.TaskAssigned).ToListAsync();
        assigned.Where(n => n.UserId == "sales01").Should().BeEmpty("自己觸發、自己是負責人時不通知自己");
        assigned.Where(n => n.UserId == "sales02").Should().ContainSingle();
        assigned.Where(n => n.UserId == "manager01").Should().ContainSingle();
    }

    [Fact]
    public async Task RuleTask_TitleResolvedInTriggeringUserLocale()
    {
        var r = await BuildRig();
        var bilingual = new I18nText([KeyValuePair.Create("zh-TW", "規劃活動內容"), KeyValuePair.Create("en-US", "Plan the event")]);
        await CreateRule(r, RuleInput("cp-tasks", OnCreate,
            [Def("plan", "", r.PlanOwner, r.Start, -62, titleI18n: bilingual), Def("feedback", "活動回饋", r.FeedbackOwner, r.End, 12)]));

        _locale.Locale = "en-US";
        var rec = await NewRecord(r, Campaign(r, r.Exhibition));
        _locale.Locale = I18nText.FallbackLocale;

        (await Tasks(rec)).Select(t => t.Title).Should().Equal("Plan the event", "活動回饋");
    }

    // ───────── everyTime ─────────

    private TaskRuleInput DesignLoop(Rig r, params TaskRuleTaskInput[] tasks) =>
        RuleInput("cp-design", new("onFieldBecomes", r.Stage, J(Opt(r.Designing))), tasks, repeat: TaskRuleRepeat.EveryTime);

    [Fact]
    public async Task EveryTime_DesignLoop_GeneratesSecondBatch()
    {
        var r = await BuildRig();
        await CreateRule(r, DesignLoop(r, Def("draft", "繪製設計稿", r.PlanOwner, null, 0)));
        var rec = await NewRecord(r, Vals((r.Title, S("案")), (r.Stage, Opt(r.Designing)), (r.PlanOwner, S("sales01"))));
        var first = (await Tasks(rec)).Should().ContainSingle().Subject;
        first.RuleOccurrence.Should().Be(1);
        await Patch(first.Id, """{"status":"done"}""");

        await Change(rec, Vals((r.Stage, Opt(r.Confirming))));
        await Change(rec, Vals((r.Stage, Opt(r.Designing))));

        var tasks = await Tasks(rec);
        tasks.Should().HaveCount(2);
        Code(tasks, "draft", 2).Status.Should().Be(TaskItemStatus.Todo);
        (await Runs(rec)).Select(x => x.Occurrence).Should().Equal(1, 2);
    }

    [Fact]
    public async Task EveryTime_PreviousRoundOpen_SkipsWithSystemEntry()
    {
        var r = await BuildRig();
        await CreateRule(r, DesignLoop(r, Def("draft", "繪製設計稿", r.PlanOwner, null, 0)));
        var rec = await NewRecord(r, Vals((r.Title, S("案")), (r.Stage, Opt(r.Designing)), (r.PlanOwner, S("sales01"))));

        await Change(rec, Vals((r.Stage, Opt(r.Confirming))));
        await Change(rec, Vals((r.Stage, Opt(r.Designing))));

        var open = (await Tasks(rec)).Should().ContainSingle().Subject;
        open.Status.Should().Be(TaskItemStatus.Todo);
        (await Events(open.Id)).Should().Equal(TaskSystemEvents.Created, TaskSystemEvents.RetriggerSkipped);
        (await Runs(rec)).Should().ContainSingle();
    }

    [Fact]
    public async Task EveryTime_ConditionsAndSync_OnlyLatestBatch()
    {
        var r = await BuildRig();
        await CreateRule(r, DesignLoop(r,
            Def("draft", "繪製設計稿", r.PlanOwner, null, 0),
            Def("model", "製作模型", r.SetupOwner, null, 0, Eq(r.Kind, Opt(r.Exhibition)))));
        var rec = await NewRecord(r, Vals((r.Title, S("案")), (r.Stage, Opt(r.Designing)), (r.Kind, Opt(r.Exhibition)),
            (r.PlanOwner, S("sales01")), (r.SetupOwner, S("sales02"))));
        await Change(rec, Vals((r.Kind, Opt(r.Seminar))));
        var batch1 = await Tasks(rec);
        Code(batch1, "model").Status.Should().Be(TaskItemStatus.Cancelled);
        await Patch(Code(batch1, "draft").Id, """{"status":"done"}""");
        await Change(rec, Vals((r.Stage, Opt(r.Confirming))));
        await Change(rec, Vals((r.Stage, Opt(r.Designing))));
        (await Tasks(rec)).Where(t => t.RuleOccurrence == 2).Select(t => t.RuleTaskCode).Should().Equal("draft");

        await Change(rec, Vals((r.Kind, Opt(r.Exhibition))));
        await Change(rec, Vals((r.SetupOwner, S("manager01"))));

        var tasks = await Tasks(rec);
        var oldModel = Code(tasks, "model", 1);
        oldModel.Status.Should().Be(TaskItemStatus.Cancelled, "舊批次不再自動恢復");
        oldModel.AssigneeUserId.Should().Be("sales02");
        var newModel = Code(tasks, "model", 2);
        newModel.Status.Should().Be(TaskItemStatus.Todo);
        newModel.AssigneeUserId.Should().Be("manager01");
        (await Runs(rec)).Should().HaveCount(2);
    }
}
