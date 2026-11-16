using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
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

/// <summary>手動套用任務範本（設計 §5.1 manual、§5.2）：once 只能套一次、everyTime 每按一次一批但上一輪未完成時擋下、記錄任務回應列出可套用的規則。</summary>
public class TaskRuleManualTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed record Rig(string Key, long Title, long Owner, long Flag);

    private async Task<Rig> BuildRig(string key = "mr")
    {
        await NewModule(key);
        var owner = await MustAdd(key, FieldType.User, label: "負責人");
        var flag = await MustAdd(key, FieldType.Boolean, label: "已確認");
        (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        var sales = (await Db.Context.Set<PermissionGroup>().AsNoTracking().SingleAsync(g => g.Key == "sales")).Id;
        (await Send(new SaveModulePermissionsRequest(key, new ModulePermissionsDto(
            [new GroupPermissionDto(sales, new PrivilegesDto(true, true, true, false, false, false, false), DataScope.All)], [], [], []))))
            .IsSuccess.Should().BeTrue();
        return new Rig(key, (await Detail(key)).TitleFieldId!.Value, owner.Id, flag.Id);
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

    private static TaskRuleTaskInput Step(Rig r, string code, JsonElement? when = null) =>
        new(code, Text("準備" + code), null, r.Owner, null, 0, when);

    private JsonElement FlagIsTrue(Rig r) => J($$"""{"all":[{"field":{{r.Flag}},"op":"eq","value":true}]}""");

    private async Task CreateRule(Rig r, string key, string on = "manual", TaskRuleRepeat repeat = TaskRuleRepeat.Once,
        JsonElement? when = null, bool active = true, params TaskRuleTaskInput[] steps)
    {
        var res = await Run(new CreateTaskRuleRequest(r.Key,
            new TaskRuleInput(key, Text(key), active, repeat, new(on, null, null), when, steps.Length == 0 ? [Step(r, "a")] : steps.ToList())));
        res.IsCreated.Should().BeTrue(Dump(res));
    }

    private async Task<long> NewRecord(Rig r, bool flag)
    {
        var res = await Run(new CreateRecordRequest(r.Key, new Dictionary<string, JsonElement?>
        {
            [$"f_{r.Title}"] = J("\"案\""), [$"f_{r.Owner}"] = J("\"sales01\""), [$"f_{r.Flag}"] = J(flag ? "true" : "false")
        }));
        res.IsCreated.Should().BeTrue(Dump(res));
        return ((DynamicRecordResponse)res.Data!).Id;
    }

    private async Task<RecordTasksResponse> Apply(long recordId, string ruleKey, string user = "admin")
    {
        var res = await Run(new ApplyTaskRuleRequest(recordId, ruleKey), user);
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (RecordTasksResponse)res.Data!;
    }

    private async Task<RecordTasksResponse> RecordTasks(long recordId) =>
        (RecordTasksResponse)(await Run(new GetRecordTasksRequest(recordId))).Data!;

    private async Task<List<TaskItem>> Tasks(long recordId) =>
        await Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == recordId).OrderBy(t => t.Id).ToListAsync();

    [Fact]
    public async Task Manual_ApplyOnce_SecondIs409()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-once");
        var rec = await NewRecord(r, flag: false);
        (await Tasks(rec)).Should().BeEmpty("manual 不由存檔觸發");

        var applied = await Apply(rec, "m-once", "sales01");
        applied.Items.Should().ContainSingle().Which.Title.Should().Be("準備a");
        applied.ManualRules.Should().ContainSingle().Which.Applied.Should().BeTrue();
        var task = (await Tasks(rec)).Single();
        task.CreatedBy.Should().BeNull();
        task.RuleOccurrence.Should().Be(1);

        var again = await Run(new ApplyTaskRuleRequest(rec, "m-once"), "sales01");
        again.IsConflict.Should().BeTrue();
        again.Message.Should().Be("已套用");
        (await Tasks(rec)).Should().ContainSingle();
    }

    [Fact]
    public async Task Manual_NotApplicable_409()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-cond", when: FlagIsTrue(r));
        var rec = await NewRecord(r, flag: false);

        (await RecordTasks(rec)).ManualRules.Should().ContainSingle().Which.Applicable.Should().BeFalse();
        var res = await Run(new ApplyTaskRuleRequest(rec, "m-cond"));

        res.IsConflict.Should().BeTrue();
        res.Message.Should().Be("這筆記錄不符合規則條件");
        (await Tasks(rec)).Should().BeEmpty();
    }

    [Fact]
    public async Task Manual_UnknownInactiveOrNotManual_404()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-auto", on: "onCreate");
        await CreateRule(r, "m-off", active: false);
        var rec = await NewRecord(r, flag: false);

        foreach (var key in new[] { "m-auto", "m-off", "m-none" })
        {
            var res = await Run(new ApplyTaskRuleRequest(rec, key));
            res.IsNotFound.Should().BeTrue(key);
            res.Message.Should().Be("找不到任務規則");
        }
        (await Run(new ApplyTaskRuleRequest(rec + 999, "m-auto"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Manual_AfterApply_TaskConditionsKeepEvaluating()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-steps", steps: [Step(r, "a"), Step(r, "b", FlagIsTrue(r))]);
        var rec = await NewRecord(r, flag: false);
        (await Apply(rec, "m-steps")).Items.Select(t => t.Title).Should().Equal("準備a");

        var rowVersion = (await Db.Context.Set<Record>().AsNoTracking().SingleAsync(x => x.Id == rec)).RowVersion;
        (await Run(new UpdateRecordRequest(rec, new Dictionary<string, JsonElement?> { [$"f_{r.Flag}"] = J("true") }, rowVersion)))
            .IsSuccess.Should().BeTrue();

        (await Tasks(rec)).Select(t => t.RuleTaskCode).Should().Equal("a", "b");
    }

    [Fact]
    public async Task RecordTasks_ListsManualRulesWithAppliedFlag()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-once");
        await CreateRule(r, "m-every", repeat: TaskRuleRepeat.EveryTime);
        await CreateRule(r, "m-auto", on: "onCreate");
        await CreateRule(r, "m-off", active: false);
        var rec = await NewRecord(r, flag: false);

        var before = (await RecordTasks(rec)).ManualRules;
        before.Select(m => (m.Key, m.Repeat, m.Applied, m.Applicable, m.PreviousOpen)).Should().Equal(
            ("m-every", TaskRuleRepeat.EveryTime, false, true, false), ("m-once", TaskRuleRepeat.Once, false, true, false));

        await Apply(rec, "m-once");
        var after = await Apply(rec, "m-every");

        after.ManualRules.Select(m => (m.Key, m.Applied, m.PreviousOpen)).Should().Equal(("m-every", false, true), ("m-once", true, false));
    }

    [Fact]
    public async Task EveryTime_Manual_EachPressNewBatch_BlockedWhileOpen()
    {
        var r = await BuildRig();
        await CreateRule(r, "m-every", repeat: TaskRuleRepeat.EveryTime);
        var rec = await NewRecord(r, flag: false);

        await Apply(rec, "m-every");
        var first = (await Tasks(rec)).Single();
        first.RuleOccurrence.Should().Be(1);

        var blocked = await Run(new ApplyTaskRuleRequest(rec, "m-every"));
        blocked.IsConflict.Should().BeTrue();
        blocked.Message.Should().Be("上一輪任務尚未完成");

        (await Run(new UpdateTaskRequest(first.Id, J("""{"status":"done"}""")))).IsSuccess.Should().BeTrue();
        var second = await Apply(rec, "m-every");

        (await Tasks(rec)).Select(t => t.RuleOccurrence).Should().Equal(1, 2);
        second.ManualRules.Single().Applied.Should().BeFalse("everyTime 的按鈕不變灰");
        (await Db.Context.Set<TaskRuleRun>().AsNoTracking().CountAsync(x => x.RecordId == rec)).Should().Be(2);
    }
}
