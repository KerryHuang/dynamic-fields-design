using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using F = DynamicModules.Domain.Reports.TaskReportFields;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>tasks 資料來源的執行（看板設計 §7）：衍生欄位、權限矩陣、看不到記錄時的 restricted、只走記憶體路徑。基準日 2026-11-04。</summary>
[Trait("Category", "Postgres")]
public class TaskReportRunnerTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private static readonly DateOnly Nov4 = new(2026, 11, 4);

    private static DateTime Wall(int month, int day, int hour = 9) => new(2026, month, day, hour, 0, 0, DateTimeKind.Unspecified);
    private static DateOnly Day(int day) => new(2026, 11, day);

    private static ReportFieldRef T(long code) => new("main", code, null);
    private static ReportMeasure M(string id, string kind, long? field = null, ReportFilter? cond = null) =>
        new(id, kind, field is null ? null : T(field.Value), cond);
    private static ReportFilter Fl(long code, string op, object? value = null) =>
        new(T(code), op, value is null ? null : JsonSerializer.SerializeToElement(value));
    private static ReportGroup G(long code, string? bucket = null) => new(T(code), bucket);
    private static string Opt(long id) => $"opt_{id}";

    private static ReportDefinition TaskDef(List<ReportMeasure> measures, List<ReportGroup>? groups = null, List<ReportFilter>? filters = null,
        long? primary = null, ReportDateRange? range = null, string dataset = ReportDatasets.Tasks) =>
        new(1, "", null, [], groups ?? [], measures, filters ?? [], primary is null ? null : T(primary.Value), range, [], "table", dataset);

    private async Task<ReportResult> Run(ReportDefinition d, string user = "admin")
    {
        As(user);
        using var scope = NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<ReportRunner>().RunAsync(d, new ReportRunOptions(null, null), default);
        As("admin");
        return result;
    }

    /// <summary>不跳脫中文，NotContain 才驗得到名稱。</summary>
    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });

    private static ReportGroupResult Group(ReportResult r, string label) => r.Groups!.Single(g => g.GroupLabels[0] == label);

    private async Task<Target> BuildRestrictedTarget(string key)
    {
        var t = await BuildTarget(key);
        var r = await Send(new SaveModulePermissionsRequest(key, new ModulePermissionsDto(
            [new GroupPermissionDto(t.SalesGroupId, new PrivilegesDto(true, false, true, false, false, false, false), DataScope.Own)], [], [], [])));
        r.IsSuccess.Should().BeTrue(r.Message);
        return t;
    }

    private async Task<TaskRule> SeedRule(Target t, string name)
    {
        var rule = new TaskRule { Key = $"r-{t.Key}-{name}", NameI18n = Text(name), ModuleId = t.ModuleId, CreatedUserId = "admin", CreatedTime = SeedTime };
        Db.Context.Add(rule);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return rule;
    }

    private Task<TaskItem> Done(string assignee, int due, int completedDay, int createdDay = 1) =>
        SeedTask($"完成{due}-{completedDay}", createdBy: "admin", assignee: assignee, due: Day(due), status: TaskItemStatus.Done,
            created: Wall(11, createdDay), completed: Wall(11, completedDay, 17));

    [Fact]
    public async Task SpecExample_AvgDelayAndOnTimeRate()
    {
        await Done("sales01", 1, 3);
        await Done("sales01", 1, 5);
        await Done("sales01", 1, 7);
        await Done("sales01", 3, 3);
        await Done("sales01", 4, 2);
        await SeedTask("還沒做", assignee: "sales01", due: Day(1));
        var onTime = Fl(F.Completion, "eq", Opt(F.CompletionOnTime));

        var r = await Run(TaskDef(
            [M("n", "countMain"), M("ok", "countIf", cond: onTime), M("rate", "ratio", cond: onTime),
             M("lateAvg", "avg", F.DelayDays, Fl(F.Completion, "eq", Opt(F.CompletionLate))), M("avg", "avg", F.DelayDays)],
            [G(F.Assignee)], [Fl(F.Status, "eq", Opt(F.StatusOption(TaskItemStatus.Done)))]));

        r.State.Should().Be("ok", r.Message);
        var v = r.Groups!.Single().Values;
        v["n"].Should().Be(5);
        v["ok"].Should().Be(2);
        v["rate"].Should().Be(0.4m);
        v["lateAvg"].Should().Be(4);
        v["avg"].Should().Be(2.4m);
        r.RowMeaning.Should().Be("一筆任務");
        r.Engine.Should().Be("memory");
        r.FallbackReasons.Should().Equal("datasetMemoryOnly");
        r.Columns.Single(c => c.Key == "ok").Label.Should().Be("完成狀況＝準時完成 筆數");
    }

    [Fact]
    public async Task WorkloadShape_GroupsByAssignee_WithUnassigned()
    {
        await SeedTask("待辦逾期", assignee: "sales01", due: Day(1));
        await SeedTask("待辦", assignee: "sales01", due: Day(9));
        await SeedTask("進行中逾期", assignee: "sales01", due: Day(2), status: TaskItemStatus.InProgress);
        await SeedTask("沒人接", assignee: null, due: Day(1));
        await SeedTask("已取消且過期", assignee: "sales01", due: Day(1), status: TaskItemStatus.Cancelled);
        var measures = new List<ReportMeasure>
        {
            M("todo", "countIf", cond: Fl(F.Status, "eq", Opt(1))), M("doing", "countIf", cond: Fl(F.Status, "eq", Opt(2))),
            M("late", "countIf", cond: Fl(F.Overdue, "eq", true)), M("delay", "sum", F.DelayDays),
        };

        var r = await Run(TaskDef(measures, [G(F.Assignee)], [Fl(F.Status, "in", new[] { Opt(1), Opt(2) })]));

        r.State.Should().Be("ok", r.Message);
        r.Groups!.Last().GroupLabels[0].Should().Be("未指派", "空值排最後");
        Group(r, "未指派").Values["late"].Should().Be(1);
        var sales = r.Groups!.First();
        sales.Values["todo"].Should().Be(2);
        sales.Values["doing"].Should().Be(1);
        sales.Values["late"].Should().Be(2);
        sales.Values["delay"].Should().Be(3 + 0 + 2);
        // B1：不看狀態時，已取消的過期任務也不算逾期、延遲天數空白
        var all = await Run(TaskDef([M("late", "countIf", cond: Fl(F.Overdue, "eq", true)), M("delay", "sum", F.DelayDays)], [G(F.Status)]));
        Group(all, "已取消").Values["late"].Should().Be(0);
        Group(all, "已取消").Values["delay"].Should().BeNull();
    }

    [Fact]
    public async Task CompletionDays_Median()
    {
        await Done("sales01", 30, 2, createdDay: 1);
        await Done("sales01", 30, 4, createdDay: 1);
        await Done("sales01", 30, 9, createdDay: 1);

        var r = await Run(TaskDef([M("med", "median", F.CompletionDays), M("avg", "avg", F.CompletionDays)]));

        r.Total!["med"].Should().Be(3);
        r.Total["avg"].Should().Be(4);
    }

    /// <summary>tk 業務 Own、業務主管 All；tk2 只有業務 Own；tk3 已關閉（凍結）。含個人、記錄、軟刪除記錄、只被 @、部屬的未指派任務。</summary>
    private async Task<(Target Tk, Target Tk2)> SeedPersonas()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var tk = await BuildTarget("tk");
        var frozen = await BuildTarget("tk3");
        var tk2 = await BuildRestrictedTarget("tk2");
        var own = await NewRecord(tk, "sales01");
        var other = await NewRecord(tk, "sales02");
        var deleted = await NewRecord(tk, "sales01", "已刪除記錄");
        await Db.Context.Set<Record>().Where(r => r.Id == deleted).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeleteFlag, true));
        var recFrozen = await NewRecord(frozen, "sales01");
        await Db.Context.Set<Module>().Where(m => m.Key == "tk3").ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        var recRestricted = await NewRecord(tk2, "sales01");

        await SeedTask("個人", createdBy: "sales01", assignee: "sales01");
        await SeedTask("自己範圍內的記錄", createdBy: "floor01", target: tk, recordId: own);
        await SeedTask("範圍外的記錄", createdBy: "floor01", target: tk, recordId: other);
        await SeedTask("已刪除記錄上的任務", createdBy: "floor01", target: tk, recordId: deleted);
        await SeedTask("凍結模組", createdBy: "floor01", target: frozen, recordId: recFrozen);
        await SeedTask("受限模組的部屬任務", createdBy: "floor01", assignee: "sales01", target: tk2, recordId: recRestricted);
        await SeedTask("只被提到", createdBy: "sales02", mentioned: ["ops01"]);
        await SeedTask("部屬的未指派任務", createdBy: "keeper01");
        return (tk, tk2);
    }

    [Fact]
    public async Task PermissionMatrix_CountsPerPersona()
    {
        await SeedPersonas();
        foreach (var who in new[] { "admin", "sales01", "sales02", "manager01", "ops01", "floor01", "nobody" })
        {
            As(who);
            int expected;
            using (var scope = NewScope())
            {
                var v = await scope.ServiceProvider.GetRequiredService<TaskVisibilityService>().ForCurrentUserAsync(default);
                expected = (await scope.ServiceProvider.GetRequiredService<ITaskRepository>().ListVisibleIdsAsync(v, default)).Count;
            }
            var r = await Run(TaskDef([M("n", "countMain")]), who);
            (r.Total?["n"] ?? 0).Should().Be(expected, $"身分 {who}");
        }
    }

    [Fact]
    public async Task GroupByModule_RestrictedOwnGroup_SortedLast()
    {
        var (tk, tk2) = await SeedPersonas();

        var r = await Run(TaskDef([M("n", "countMain")], [G(F.Module)]), "manager01");

        r.State.Should().Be("ok", r.Message);
        // 看得到記錄的模組在前，個人任務「（無）」、看不到記錄的「（無權限）」固定排最後
        r.Groups!.Select(g => g.GroupKeys[0]).TakeLast(2).Should().Equal("", ReportCalculator.RestrictedKey);
        r.Groups!.Select(g => g.GroupLabels[0]).TakeLast(2).Should().Equal("（無）", "（無權限）");
        r.Groups!.SkipLast(2).Select(g => g.GroupKeys[0]).Should().Contain(Opt(tk.ModuleId)).And.OnlyContain(k => k.StartsWith("opt_"));
        Group(r, "（無權限）").Values["n"].Should().Be(1, "tk2 的部屬任務，記錄看不到");
        Json(r).Should().NotContain(Opt(tk2.ModuleId));
    }

    /// <summary>manager01：tk2 的記錄看不到、但任務是部屬的；其中一件掛著 tk（可讀模組）的規則。另有 tk 上看得到的規則任務與個人任務。</summary>
    private async Task<(Target Tk, Target Tk2, TaskRule Rule)> SeedRestricted()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var tk = await BuildTarget("tk");
        var tk2 = await BuildRestrictedTarget("tk2");
        var rule = await SeedRule(tk, "tk 的規則");
        var visible = await NewRecord(tk, "sales01");
        var hidden = await NewRecord(tk2, "sales01", "機密案件甲");
        await SeedTask("看得到的規則任務", createdBy: null, assignee: "sales01", target: tk, recordId: visible, ruleId: rule.Id);
        await SeedTask("看不到記錄的規則任務", createdBy: null, assignee: "sales01", target: tk2, recordId: hidden, ruleId: rule.Id);
        await SeedTask("個人", createdBy: "sales01", assignee: "sales01");
        return (tk, tk2, rule);
    }

    [Theory]
    [InlineData(F.Module, "eq")]
    [InlineData(F.Module, "ne")]
    [InlineData(F.Module, "in")]
    [InlineData(F.Module, "notIn")]
    [InlineData(F.Module, "isEmpty")]
    [InlineData(F.Module, "isNotEmpty")]
    [InlineData(F.Rule, "eq")]
    [InlineData(F.Rule, "ne")]
    [InlineData(F.Rule, "in")]
    [InlineData(F.Rule, "notIn")]
    [InlineData(F.Rule, "isEmpty")]
    [InlineData(F.Rule, "isNotEmpty")]
    public async Task ModuleOrRuleCondition_RestrictedNeverMatches_EveryOperator(long field, string op)
    {
        var (tk, _, rule) = await SeedRestricted();
        var value = Opt(field == F.Module ? tk.ModuleId : rule.Id);
        var cond = Fl(field, op, op switch { "eq" or "ne" => value, "in" or "notIn" => new[] { value }, _ => null });

        var filtered = await Run(TaskDef([M("n", "countMain")], filters: [cond]), "manager01");
        var counted = await Run(TaskDef([M("n", "countMain"), M("hit", "countIf", cond: cond)]), "manager01");

        // 每個運算子都只有一件符合：看得到的規則任務（eq、in、isNotEmpty）或個人任務（ne、notIn、isEmpty）；restricted 那件永遠不算
        filtered.Total!["n"].Should().Be(1, $"{field} {op} 當篩選");
        counted.Total!["n"].Should().Be(3);
        counted.Total["hit"].Should().Be(1, $"{field} {op} 當 countIf 條件");
    }

    [Fact]
    public async Task DeletedRule_IsMissingGroup()
    {
        var tk = await BuildTarget("tk");
        var rec = await NewRecord(tk, "sales01");
        await SeedTask("規則已刪除", createdBy: null, target: tk, recordId: rec, ruleId: 99999);

        var r = await Run(TaskDef([M("n", "countMain")], [G(F.Rule)]));

        r.Groups!.Single().GroupKeys[0].Should().Be(ReportCalculator.MissingKey);
        r.Groups!.Single().GroupLabels[0].Should().Be("（已刪除）");
    }

    [Fact]
    public async Task RestrictedRecord_RuleCell_IsRestricted_NotMissingNorOk()
    {
        var (tk, tk2, rule) = await SeedRestricted();
        var deletedRuleRecord = await NewRecord(tk2, "sales01", "機密案件乙");
        await SeedTask("看不到記錄、規則已刪除", createdBy: null, assignee: "sales01", target: tk2, recordId: deletedRuleRecord, ruleId: 99999);

        var byRule = await Run(TaskDef([M("n", "countMain")], [G(F.Rule)]), "manager01");
        var byModule = await Run(TaskDef([M("n", "countMain")], [G(F.Module)]), "manager01");

        byRule.Groups!.Select(g => (g.GroupLabels[0], g.Values["n"])).Should().Equal(("tk 的規則", 1m), ("（無）", 1m), ("（無權限）", 2m));
        Json(byRule).Should().Contain("tk 的規則", "對照組：看得到記錄的那件照常顯示，也證明 Json 沒有跳脫中文");
        byModule.Groups!.Select(g => g.GroupKeys[0]).Should().Equal(Opt(tk.ModuleId), "", ReportCalculator.RestrictedKey);
        Json(byModule).Should().NotContain(Opt(tk2.ModuleId)).And.NotContain("機密案件");

        // 只剩看不到記錄的規則任務時，結果完全不出現規則名稱與規則選項
        await Db.Context.Set<TaskItem>().Where(t => t.Title == "看得到的規則任務").ExecuteDeleteAsync();
        var onlyHidden = await Run(TaskDef([M("n", "countMain")], [G(F.Rule)], [Fl(F.Source, "eq", Opt(F.SourceRule))]), "manager01");
        onlyHidden.Groups!.Should().ContainSingle().Which.GroupLabels[0].Should().Be("（無權限）");
        Json(onlyHidden).Should().NotContain("tk 的規則").And.NotContain(Opt(rule.Id)).And.NotContain("opt_99999");
    }

    [Fact]
    public async Task EmptySystem_PersonalTasksOnly()
    {
        await SeedTask("個人一", createdBy: "admin", assignee: "admin");
        await SeedTask("個人二", createdBy: "admin");

        var byDept = await Run(TaskDef([M("n", "countMain")], [G(F.AssigneeDepartment)]));
        var byModule = await Run(TaskDef([M("n", "countMain")], [G(F.Module)]));

        byDept.State.Should().Be("ok", byDept.Message);
        byDept.Groups!.Single().GroupLabels[0].Should().Be("（無部門）");
        byModule.Groups!.Single().GroupLabels[0].Should().Be("（無）");
        byModule.Groups!.Single().Values["n"].Should().Be(2);
    }

    [Fact]
    public async Task Creator_RuleTask_IsSystem()
    {
        await SeedTask("規則產生", createdBy: null, assignee: "admin");
        await SeedTask("人建的", createdBy: "admin");

        var r = await Run(TaskDef([M("n", "countMain"), M("sys", "countIf", cond: Fl(F.Creator, "isEmpty"))], [G(F.Creator)]));

        Group(r, "系統").Values["n"].Should().Be(1);
        r.Total!["sys"].Should().Be(1);
    }

    [Fact]
    public async Task DateRange_OnCompletedTime_AndWeekBuckets()
    {
        await SeedTask("上月完成", status: TaskItemStatus.Done, created: Wall(10, 1), completed: Wall(10, 30));
        await SeedTask("本月第一週一", status: TaskItemStatus.Done, created: Wall(10, 1), completed: Wall(11, 2));
        await SeedTask("本月第一週二", status: TaskItemStatus.Done, created: Wall(10, 1), completed: Wall(11, 3));
        await SeedTask("沒完成", created: Wall(11, 2));

        var r = await Run(TaskDef([M("n", "countMain")], [G(F.CompletedTime, "week")], primary: F.CompletedTime, range: new ReportDateRange("thisMonth", null)));

        r.State.Should().Be("ok", r.Message);
        r.AppliedRange.Should().NotBeNull();
        r.Groups!.Single().GroupKeys[0].Should().Be(ReportDates.BucketKey(Day(2), "week"));
        r.Groups!.Single().Values["n"].Should().Be(2);
    }

    [Fact]
    public async Task Narrowing_UnknownStatusOption_Ignored()
    {
        await SeedTask("待辦");
        await SeedTask("完成", status: TaskItemStatus.Done, completed: Wall(11, 2));

        (await Run(TaskDef([M("n", "countMain")], filters: [Fl(F.Status, "eq", "opt_9")]))).State.Should().Be("empty");
        (await Run(TaskDef([M("n", "countMain")], filters: [Fl(F.Status, "in", new[] { "opt_9", "opt_8" })]))).State.Should().Be("empty");
        (await Run(TaskDef([M("n", "countMain")], filters: [Fl(F.Status, "in", new[] { "opt_9", Opt(1) })]))).Total!["n"].Should().Be(1);
        (await Run(TaskDef([M("n", "countMain")], filters: [Fl(F.Status, "in", new[] { Opt(1), Opt(3) }), Fl(F.Status, "eq", Opt(3))]))).Total!["n"].Should().Be(1);
    }

    [Fact]
    public async Task SqlOnly_StillRunsInMemory()
    {
        await SeedTask("待辦");
        Svc<ReportOptions>().Engine = ReportEngineMode.SqlOnly;

        var r = await Run(TaskDef([M("n", "countMain")]));

        r.State.Should().Be("ok", r.Message);
        r.Engine.Should().Be("memory");
        r.FallbackReasons.Should().Equal("datasetMemoryOnly");
    }

    [Fact]
    public async Task OverLoadLimit_TooMany()
    {
        await SeedTask("一");
        await SeedTask("二");
        await SeedTask("三");
        Svc<ReportOptions>().MaxLoadRows = 2;

        var r = await Run(TaskDef([M("n", "countMain")]));

        r.State.Should().Be("tooMany");
        r.Message.Should().Be("資料太多，請縮小條件");
    }

    [Fact]
    public async Task UnknownDataset_ModuleDisabled()
    {
        await SeedTask("待辦");
        (await Run(TaskDef([M("n", "countMain")], dataset: "foo"))).State.Should().Be("moduleDisabled");
        // 被直接改壞的定義（tasks 不能用的欄位、帶出）一律 fail closed，不回數字
        (await Run(TaskDef([M("n", "countMain")], [G(F.EventKind)]))).State.Should().Be("fieldMissing");
        (await Run(TaskDef([M("n", "countMain")], [new ReportGroup(new ReportFieldRef("main", F.Module, F.Rule), null)]))).State.Should().Be("fieldMissing");
    }

    [Fact]
    public async Task SavedReport_SharedToGroup_EachViewerOwnNumbers()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        await SeedTask("業務一的", createdBy: "admin", assignee: "sales01");
        await SeedTask("業務一的另一件", createdBy: "admin", assignee: "sales01");
        await SeedTask("業務二的", createdBy: "admin", assignee: "sales02");
        As("admin");
        var created = await Send(new CreateReportRequest("任務件數", TaskDef([M("n", "countMain")])));
        created.IsCreated.Should().BeTrue(created.Message);
        var id = ((ReportDetail)created.Data!).Id;
        var salesGroup = await Db.Context.Set<PermissionGroup>().Where(g => g.Key == "sales").Select(g => g.Id).SingleAsync();
        (await Send(new SetReportSharesRequest(id, [salesGroup]))).IsSuccess.Should().BeTrue();

        async Task<decimal?> RunAs(string user)
        {
            As(user);
            var r = await Send(new RunReportRequest(id, null, "zh-TW"));
            As("admin");
            r.IsSuccess.Should().BeTrue(r.Message);
            return ((ReportResult)r.Data!).Total?["n"];
        }
        (await RunAs("sales01")).Should().Be(2);
        (await RunAs("sales02")).Should().Be(1);
        (await RunAs("admin")).Should().Be(3);
    }

    [Fact]
    public async Task Card_TaskReport_OnDashboard()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        await SeedTask("業務一的", createdBy: "admin", assignee: "sales01");
        await SeedTask("業務二的", createdBy: "admin", assignee: "sales02");
        var d = TaskDef([M("n", "countMain")], [G(F.Assignee)]);

        async Task<ReportResult> CardAs(string user)
        {
            As(user);
            using var scope = NewScope();
            var r = await scope.ServiceProvider.GetRequiredService<ReportRunner>().RunCardAsync(d, new ReportRunOptions(null, null, TopN: 5), default);
            As("admin");
            return r;
        }
        var admin = await CardAs("admin");
        admin.State.Should().Be("ok", admin.Message);
        admin.Groups!.Should().HaveCount(2);
        (await CardAs("sales01")).Groups!.Should().ContainSingle().Which.Values["n"].Should().Be(1);
    }
}
