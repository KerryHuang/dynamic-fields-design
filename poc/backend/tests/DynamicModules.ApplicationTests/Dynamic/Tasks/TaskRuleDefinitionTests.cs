using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Application.Dynamic.DeleteService.Queries.CheckDelete;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>任務規則的定義：驗證、增改刪、讀取警告、刪欄位與刪模組的盤點（設計 §5、§16）。</summary>
public class TaskRuleDefinitionTests : TaskTestBase
{
    protected sealed record Rig(
        string Key, long ModuleId, long UserField, long DateField, long DateTimeField, long EnumField, long OptionId, long BoolField, long TextField);

    protected async Task<Rig> BuildRig(string key = "tr")
    {
        await NewModule(key);
        var user = await MustAdd(key, FieldType.User, label: "負責人");
        var date = await MustAdd(key, FieldType.Date, label: "開始日");
        var dateTime = await MustAdd(key, FieldType.DateTime, label: "開始時間");
        var en = await MustAdd(key, FieldType.Enum, label: "階段");
        var option = (OptionResponse)(await Send(new AddOptionRequest(en.Id, Text("簽約"), null))).Data!;
        var flag = await MustAdd(key, FieldType.Boolean, label: "已確認");
        var text = await MustAdd(key, FieldType.Text, label: "備註");
        (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        return new Rig(key, (await Detail(key)).Id, user.Id, date.Id, dateTime.Id, en.Id, option.Id, flag.Id, text.Id);
    }

    protected static TaskRuleTaskInput Step(Rig r, string code = "t1", long? assignee = null, long? due = null, int offset = 0,
        I18nText? title = null, JsonElement? when = null) =>
        new(code, title ?? Text("準備資料"), null, assignee ?? r.UserField, due ?? r.DateField, offset, when);

    protected static TaskRuleTriggerInput OnCreate => new("onCreate", null, null);

    protected static TaskRuleInput Rule(string key, TaskRuleTriggerInput? trigger = null, JsonElement? when = null,
        TaskRuleRepeat repeat = TaskRuleRepeat.Once, bool active = true, params TaskRuleTaskInput[] steps) =>
        new(key, Text(key), active, repeat, trigger ?? OnCreate, when, steps.ToList());

    private async Task<TaskRuleResponse> Create(Rig r, TaskRuleInput input)
    {
        var res = await Send(new CreateTaskRuleRequest(r.Key, input));
        res.IsCreated.Should().BeTrue(Dump(res));
        return (TaskRuleResponse)res.Data!;
    }

    protected static string Dump(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + "=" + e.Value[0]));

    private JsonElement Cond(long fieldId, string op, string valueJson) =>
        J($$"""{"all":[{"field":{{fieldId}},"op":"{{op}}","value":{{valueJson}}}]}""");

    [Fact]
    public async Task Crud_RoundTrip_ReplacesTasksByCode()
    {
        var r = await BuildRig();
        var created = await Create(r, Rule("rule-a", steps: [Step(r, "a"), Step(r, "b", due: r.DateTimeField, offset: -2)]));
        created.Tasks.Select(t => t.Code).Should().Equal("a", "b");
        created.Repeat.Should().Be(TaskRuleRepeat.Once);
        created.Warnings.Should().BeEmpty();

        var upd = await Send(new UpdateTaskRuleRequest(r.Key, "rule-a", Rule("rule-a", active: false, repeat: TaskRuleRepeat.EveryTime,
            trigger: new("manual", null, null), steps: [Step(r, "b", offset: 5), Step(r, "c")])));
        upd.IsSuccess.Should().BeTrue(Dump(upd));

        var list = (IReadOnlyList<TaskRuleResponse>)(await Send(new GetTaskRulesRequest(r.Key))).Data!;
        var got = list.Should().ContainSingle().Subject;
        got.Active.Should().BeFalse();
        got.Repeat.Should().Be(TaskRuleRepeat.EveryTime);
        got.Trigger.On.Should().Be("manual");
        got.Tasks.Select(t => (t.Code, t.DueOffsetDays)).Should().Equal(("b", 5), ("c", 0));

        (await Send(new DeleteTaskRuleRequest(r.Key, "rule-a"))).IsSuccess.Should().BeTrue();
        ((IReadOnlyList<TaskRuleResponse>)(await Send(new GetTaskRulesRequest(r.Key))).Data!).Should().BeEmpty();
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<TaskRuleTask>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OnUpdateMatch_RequiresCondition()
    {
        var r = await BuildRig();
        var res = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", new("onUpdateMatch", null, null), steps: [Step(r)])));
        res.IsInvalid.Should().BeTrue();
        res.Errors["when"][0].Should().Be("記錄變成符合條件時觸發，條件必填");

        var bad = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", new("onUpdateMatch", null, null),
            when: Cond(999999, "eq", "\"x\""), steps: [Step(r)])));
        bad.Errors["when"][0].Should().StartWith("條件不正確：");

        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", new("onUpdateMatch", null, null),
            when: Cond(r.BoolField, "eq", "true"), steps: [Step(r)])))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task AssigneeMustBeUserField()
    {
        var r = await BuildRig();
        var res = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r, assignee: r.TextField)])));
        res.Errors["tasks[0].assigneeFieldId"][0].Should().Be("負責人必須是這個模組的人員欄位");
    }

    [Fact]
    public async Task DueMustBeDateField()
    {
        var r = await BuildRig();
        var res = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r, due: r.TextField)])));
        res.Errors["tasks[0].dueFieldId"][0].Should().Be("到期基準必須是這個模組的日期欄位");
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", steps: [Step(r, due: r.DateTimeField)])))).IsCreated.Should().BeTrue();
        var noDue = Step(r) with { DueFieldId = null };
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-c", steps: [noDue])))).IsCreated.Should().BeTrue("到期基準可以不填");
    }

    [Fact]
    public async Task DueOffset_OutOfRange_400()
    {
        var r = await BuildRig();
        var res = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r, offset: 3651)])));
        res.Errors.Should().ContainKey("tasks[0].dueOffsetDays");
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", steps: [Step(r, offset: -3650)])))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task DetailModuleRejected()
    {
        var r = await BuildRig();
        await NewModule("tr-line");
        await MustAdd("tr-line", FieldType.Reference, RefTo(r.Key, DynamicModules.Domain.Entities.Dynamic.ReferenceRelation.Detail), "表頭");
        var user = await MustAdd("tr-line", FieldType.User, label: "人員");
        (await Send(new ActivateModuleRequest("tr-line"))).IsSuccess.Should().BeTrue();

        var res = await Send(new CreateTaskRuleRequest("tr-line", Rule("line-a", steps: [Step(r, assignee: user.Id)])));

        res.IsInvalid.Should().BeTrue();
        res.Errors["moduleKey"][0].Should().Be("明細模組不能設定任務規則");
    }

    [Fact]
    public async Task DuplicateTaskCode_400()
    {
        var r = await BuildRig();
        var res = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r, "a"), Step(r, "a")])));
        res.Errors["tasks[1].code"][0].Should().Be("任務代碼在同一條規則內不能重複");
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", steps: [Step(r, "1bad")])))).Errors.Should().ContainKey("tasks[0].code");
    }

    [Fact]
    public async Task KeyImmutable_400()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r)]));
        var res = await Send(new UpdateTaskRuleRequest(r.Key, "rule-a", Rule("rule-z", steps: [Step(r)])));
        res.IsInvalid.Should().BeTrue();
        res.Errors["key"][0].Should().Be("規則代碼不能更改");
    }

    [Fact]
    public async Task DuplicateKey_409_AndKeyFormat_400()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r)]));
        var dup = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r)])));
        dup.IsConflict.Should().BeTrue();
        dup.Message.Should().Be("規則代碼已存在");
        var bad = await Send(new CreateTaskRuleRequest(r.Key, Rule("Bad_Key", steps: [Step(r)])));
        bad.Errors["key"][0].Should().Be("規則代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭");
    }

    [Fact]
    public async Task RuleOfAnotherModule_404()
    {
        var r = await BuildRig();
        var other = await BuildRig("tr2");
        await Create(r, Rule("rule-a", steps: [Step(r)]));
        var put = await Send(new UpdateTaskRuleRequest(other.Key, "rule-a", Rule("rule-a", steps: [Step(other)])));
        put.IsNotFound.Should().BeTrue();
        put.Message.Should().Be("找不到任務規則");
        (await Send(new DeleteTaskRuleRequest(other.Key, "rule-a"))).IsNotFound.Should().BeTrue();
        (await Send(new GetTaskRulesRequest("nope"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task FieldBecomes_TriggerRules()
    {
        var r = await BuildRig();
        Task<ServiceResult> Try(long? field, string? valueJson) => Send(new CreateTaskRuleRequest(r.Key,
            Rule("rule-a", new("onFieldBecomes", field, valueJson is null ? null : J(valueJson)), steps: [Step(r)])));

        (await Try(r.TextField, "\"x\"")).Errors["trigger.field"][0].Should().Be("指定值觸發只支援下拉或是非欄位");
        (await Try(null, null)).Errors.Should().ContainKey("trigger.field");
        (await Try(r.EnumField, "\"opt_999999\"")).Errors.Should().ContainKey("trigger.value");
        (await Try(r.BoolField, "\"yes\"")).Errors.Should().ContainKey("trigger.value");
        (await Try(r.EnumField, $"\"opt_{r.OptionId}\"")).IsCreated.Should().BeTrue();
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", new("onFieldBecomes", r.BoolField, J("true")), steps: [Step(r)]))))
            .IsCreated.Should().BeTrue();
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-c", new("bogus", null, null), steps: [Step(r)])))).Errors.Should().ContainKey("trigger.on");
    }

    [Theory]
    [InlineData("opt_0{id}")]
    [InlineData("opt_+{id}")]
    public async Task FieldBecomes_NonCanonicalOptionValue_IsStoredAsCanonical(string template)
    {
        var r = await BuildRig();
        var raw = template.Replace("{id}", r.OptionId.ToString());

        await Create(r, Rule("rule-a", new("onFieldBecomes", r.EnumField, J($"\"{raw}\"")), steps: [Step(r)]));

        var list = (IReadOnlyList<TaskRuleResponse>)(await Send(new GetTaskRulesRequest(r.Key))).Data!;
        var stored = list.Single().Trigger.Value!.Value;
        stored.GetString().Should().Be($"opt_{r.OptionId}");
        Db.Context.ChangeTracker.Clear();
        var rule = await Db.Context.Set<TaskRule>().AsNoTracking().SingleAsync();
        var after = new Dictionary<string, JsonElement?> { [$"f_{r.EnumField}"] = J($"\"opt_{r.OptionId}\"") };
        TaskRuleEvaluator.Triggers(rule.Trigger, true, null, true, null, after).Should().BeTrue("記錄裡的值一律是標準格式");
    }

    [Fact]
    public async Task TitleI18n_NullLocaleValue_400_NotServerError()
    {
        var r = await BuildRig();
        var withNull = new I18nText([KeyValuePair.Create("zh-TW", "短"), KeyValuePair.Create<string, string>("en-US", null!)]);

        var res = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r, title: withNull)])));

        res.IsInvalid.Should().BeTrue(Dump(res));
        res.Errors.Should().ContainKey("tasks[0].title");
    }

    [Fact]
    public async Task TasksCount_And_RulesPerModule_Limits()
    {
        var r = await BuildRig();
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [])))).Errors.Should().ContainKey("tasks");
        var tooMany = Enumerable.Range(0, 21).Select(i => Step(r, $"t{i}")).ToArray();
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", steps: tooMany)))).Errors.Should().ContainKey("tasks");

        for (var i = 0; i < 20; i++) await Create(r, Rule($"rule-{i:00}", steps: [Step(r)]));
        var over = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-xx", steps: [Step(r)])));
        over.Errors["moduleKey"][0].Should().Be("每個模組至多 20 條任務規則");
        (await Send(new UpdateTaskRuleRequest(r.Key, "rule-00", Rule("rule-00", steps: [Step(r)])))).IsSuccess.Should().BeTrue("既有的規則照常可改");
    }

    [Fact]
    public async Task EveryTime_WithOnCreate_400()
    {
        var r = await BuildRig();
        var res = await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", repeat: TaskRuleRepeat.EveryTime, steps: [Step(r)])));
        res.Errors["repeat"][0].Should().Be("建立時觸發只有一次，不能選每次符合都觸發");
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", new("manual", null, null), repeat: TaskRuleRepeat.EveryTime, steps: [Step(r)]))))
            .IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task TitleI18n_TooLongInAnyLocale_400()
    {
        var r = await BuildRig();
        var longEn = new I18nText([KeyValuePair.Create("zh-TW", "短"), KeyValuePair.Create("en-US", new string('x', 201))]);
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r, title: longEn)])))).Errors["tasks[0].title"][0]
            .Should().Be("標題不能超過 200 字");

        var empty = new I18nText([KeyValuePair.Create("en-US", "Only English")]);
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", steps: [Step(r, title: empty)])))).Errors["tasks[0].title"][0]
            .Should().Be("標題必填", "用 zh-TW 解析後是空的");

        var longDesc = Step(r) with { Description = new I18nText([KeyValuePair.Create("th", new string('x', 4001))]) };
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-c", steps: [longDesc])))).Errors.Should().ContainKey("tasks[0].description");

        var full = new I18nText(I18nText.SupportedLocales.Select(l => KeyValuePair.Create(l, new string('x', 200))));
        var created = await Create(r, Rule("rule-d", steps: [Step(r, title: full)]));
        created.Tasks[0].Title.Values.Should().HaveCount(4);
    }

    [Fact]
    public async Task Get_WarnsWhenReferencedFieldMissing()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r, "a", due: r.DateTimeField)]));
        await Db.Context.Set<Field>().Where(f => f.Id == r.DateTimeField).ExecuteDeleteAsync();
        Db.Context.ChangeTracker.Clear();

        var list = (IReadOnlyList<TaskRuleResponse>)(await Send(new GetTaskRulesRequest(r.Key))).Data!;

        list.Single().Warnings.Should().Equal("引用的欄位已刪除");
    }

    [Fact]
    public async Task Get_InactiveFieldIsNotAWarning()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r, "a", due: r.DateTimeField)]));
        await Deactivate(r.DateTimeField);

        var list = (IReadOnlyList<TaskRuleResponse>)(await Send(new GetTaskRulesRequest(r.Key))).Data!;

        list.Single().Warnings.Should().BeEmpty("停用的欄位仍然存在");
    }

    [Fact]
    public async Task NonDesigner_403()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r)]));
        As("sales01");
        (await Send(new GetTaskRulesRequest(r.Key))).IsForbidden.Should().BeTrue();
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-b", steps: [Step(r)])))).IsForbidden.Should().BeTrue();
        (await Send(new UpdateTaskRuleRequest(r.Key, "rule-a", Rule("rule-a", steps: [Step(r)])))).IsForbidden.Should().BeTrue();
        (await Send(new DeleteTaskRuleRequest(r.Key, "rule-a"))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task MasterModuleNeedsSystemAdmin_403()
    {
        var r = await BuildRig();
        var plain = await BuildRig("tr-plain");
        var app = new App { Key = "master-x", Kind = AppKind.Master, Status = AppStatus.Open, Source = "builtIn", RowVersion = "x" };
        Db.Context.Add(app);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new AppModule { AppId = app.Id, Code = "m", ModuleId = r.ModuleId });
        Db.Context.Add(new UserProgramPrivilege { UserId = "sales01", ProgramCode = "DYN00000", CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        As("sales01");
        (await Send(new CreateTaskRuleRequest(plain.Key, Rule("rule-p", steps: [Step(plain)])))).IsCreated.Should().BeTrue("對照組：有設計器權限、非主檔模組可以存");
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r)])))).IsForbidden.Should().BeTrue("設計器權限不夠，主檔還要系統管理員");
        As("admin");
        (await Send(new CreateTaskRuleRequest(r.Key, Rule("rule-a", steps: [Step(r)])))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task DeletingFieldUsedByTaskRule_IsBlocked()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r, due: r.DateTimeField)]));

        var used = (DeleteCheckResponse)(await Send(new CheckDeleteRequest(DeleteTarget.Field, null, r.DateTimeField))).Data!;
        var free = (DeleteCheckResponse)(await Send(new CheckDeleteRequest(DeleteTarget.Field, null, r.TextField))).Data!;

        used.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.TaskRule && b.ModuleKey == r.Key && b.Name == "rule-a");
        free.Blockers.Should().NotContain(b => b.Kind == DeleteBlockerKind.TaskRule);
    }

    [Fact]
    public async Task DeletingAssigneeField_IsBlocked()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r)]));

        var check = (DeleteCheckResponse)(await Send(new CheckDeleteRequest(DeleteTarget.Field, null, r.UserField))).Data!;

        check.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.TaskRule && b.Name == "rule-a");
    }

    [Fact]
    public async Task DeletingTriggerField_IsBlocked()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", new("onFieldBecomes", r.EnumField, J($"\"opt_{r.OptionId}\"")), steps: [Step(r)]));

        var check = (DeleteCheckResponse)(await Send(new CheckDeleteRequest(DeleteTarget.Field, null, r.EnumField))).Data!;

        check.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.TaskRule && b.Name == "rule-a");
    }

    [Fact]
    public async Task DeletingFieldUsedByTaskLevelCondition_IsBlocked()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r, when: Cond(r.TextField, "eq", "\"x\""))]));

        var check = (DeleteCheckResponse)(await Send(new CheckDeleteRequest(DeleteTarget.Field, null, r.TextField))).Data!;

        check.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.TaskRule && b.Name == "rule-a");
    }

    [Fact]
    public async Task DeletingFieldUsedByRuleCondition_IsBlocked_EvenWhenRuleIsInactive()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", active: false, trigger: new("manual", null, null), when: Cond(r.TextField, "eq", "\"x\""), steps: [Step(r)]));

        var check = (DeleteCheckResponse)(await Send(new CheckDeleteRequest(DeleteTarget.Field, null, r.TextField))).Data!;

        check.Blockers.Should().Contain(b => b.Kind == DeleteBlockerKind.TaskRule);
    }

    [Fact]
    public async Task DeletingModule_RemovesItsRules()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r, "a"), Step(r, "b")]));
        var other = await BuildRig("tr2");
        await Create(other, Rule("rule-b", steps: [Step(other)]));
        Db.Context.Add(new TaskRuleRun { RuleId = (await Db.Context.Set<TaskRule>().AsNoTracking().SingleAsync(x => x.Key == "rule-a")).Id, RecordId = 1, Occurrence = 1, CreatedTime = SeedTime });
        await Db.Context.SaveChangesAsync();
        (await Send(new DeactivateModuleRequest(r.Key))).IsSuccess.Should().BeTrue();
        Db.Context.ChangeTracker.Clear();   // 正式環境每個請求一個新的 DbContext，不會帶著建立規則時追蹤的實體

        var res = await Send(new DeleteModuleRequest(r.Key, 0));

        res.IsSuccess.Should().BeTrue(Dump(res));
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<TaskRule>().AsNoTracking().Select(x => x.Key).ToListAsync()).Should().Equal("rule-b");
        (await Db.Context.Set<TaskRuleTask>().AsNoTracking().CountAsync()).Should().Be(1);
        (await Db.Context.Set<TaskRuleRun>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClearedFields_DeactivateRulesThatUseThem()
    {
        var r = await BuildRig();
        await Create(r, Rule("rule-a", steps: [Step(r, due: r.DateTimeField)]));
        await Create(r, Rule("rule-b", steps: [Step(r)]));
        await Create(r, Rule("rule-c", active: false, steps: [Step(r, due: r.DateTimeField)]));
        var deletion = Svc<IDeletionRepository>();

        var count = await deletion.DeactivateTaskRulesReferencingAsync(r.ModuleId, new HashSet<long> { r.DateTimeField }, CancellationToken.None);
        await Db.Context.SaveChangesAsync();

        count.Should().Be(1, "只算原本啟用、被停用的");
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<TaskRule>().AsNoTracking().OrderBy(x => x.Key).Select(x => x.Active).ToListAsync()).Should().Equal(false, true, false);
    }

    [Fact]
    public async Task TaskConditions_NullHolds_BadJsonDoesNot_ConditionIsEvaluated()
    {
        var r = await BuildRig();
        var module = (await Svc<IModuleRepository>().GetByKeyAsync(r.Key, includeChildren: true, CancellationToken.None))!;
        var registry = Svc<DynamicModules.Domain.FieldTypes.FieldTypeRegistry>();
        var today = new DateOnly(2026, 11, 4);
        var users = new HashSet<string>();
        IReadOnlyDictionary<string, JsonElement?> flag(bool on) => new Dictionary<string, JsonElement?> { [$"f_{r.BoolField}"] = J(on ? "true" : "false") };

        TaskConditions.Holds(null, module, flag(false), registry, today, users).Should().BeTrue("沒有條件一律成立");
        TaskConditions.Holds("{壞掉", module, flag(true), registry, today, users).Should().BeFalse("解析失敗不成立");
        TaskConditions.Holds(Cond(999999, "eq", "true").GetRawText(), module, flag(true), registry, today, users).Should().BeFalse("引用不存在的欄位");
        var when = Cond(r.BoolField, "eq", "true").GetRawText();
        TaskConditions.Holds(when, module, flag(true), registry, today, users).Should().BeTrue();
        TaskConditions.Holds(when, module, flag(false), registry, today, users).Should().BeFalse();
    }
}
