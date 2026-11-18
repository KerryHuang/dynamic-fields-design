using System.Text.Encodings.Web;
using ClosedXML.Excel;
using System.Text.Json;
using System.Text.Unicode;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.ReportService.Queries.DrillReport;
using DynamicModules.Application.Dynamic.ReportService.Queries.ExportReport;
using DynamicModules.Application.Dynamic.ReportService.Queries.GetReport;
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

    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    private readonly Clock _clock = new();

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(_clock);

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

    /// <summary>每個身分應該數到的任務（標題寫死，照 Seed 手算，審查 Y3）與「所屬模組」是（無權限）的任務；推導見 TaskReportSourceTests.Personas。</summary>
    public static TheoryData<string, string[], string[]> Personas => TaskReportSourceTests.Personas;

    [Theory]
    [MemberData(nameof(Personas))]
    public async Task PermissionMatrix_CountsPerPersona(string who, string[] visible, string[] recordHidden)
    {
        await SeedPersonas();

        var r = await Run(TaskDef([M("n", "countMain")]) with { Columns = [T(F.Title), T(F.Module)] }, who);

        (r.Total?["n"] ?? 0).Should().Be(visible.Length, $"身分 {who}");
        var rows = r.DetailRows ?? [];
        rows.Select(row => (string)row[0]!).Should().BeEquivalentTo(visible, $"身分 {who}");
        rows.Where(row => (string?)row[1] == "（無權限）").Select(row => (string)row[0]!).Should().BeEquivalentTo(recordHidden, $"身分 {who}");
        // 對照：報表＝看板的可見 Id 數
        As(who);
        using (var scope = NewScope())
        {
            var v = await scope.ServiceProvider.GetRequiredService<TaskVisibilityService>().ForCurrentUserAsync(default);
            (await scope.ServiceProvider.GetRequiredService<ITaskRepository>().ListVisibleIdsAsync(v, default)).Should().HaveCount(visible.Length);
        }
        As("admin");
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
        r.Message.Should().Be("資料太多，請縮小條件；部門篩選不會減少載入量，請縮小期間");
    }

    [Fact]
    public async Task OverMaxRows_AfterConditions_TooMany()
    {
        await SeedTask("一", assignee: "sales01");
        await SeedTask("二", assignee: "sales01");
        await SeedTask("三", assignee: "sales02");
        Svc<ReportOptions>().MaxLoadRows = 10;
        Svc<ReportOptions>().MaxRows = 1;

        var two = await Run(TaskDef([M("n", "countMain")], filters: [Fl(F.Assignee, "eq", "sales01")]));
        var one = await Run(TaskDef([M("n", "countMain")], filters: [Fl(F.Assignee, "eq", "sales02")]));

        two.State.Should().Be("tooMany");
        two.Message.Should().Be("資料太多，請縮小條件");
        one.State.Should().Be("ok", one.Message);
    }

    /// <summary>資料庫端縮小（J7）只會多撈：三種主要日期、邊界時刻、重開後留著完成時間的待辦、沒有到期日，Default 與不縮小的 MemoryOnly 數字相同（審查 G3）。</summary>
    [Theory]
    [InlineData(F.DueDate)]
    [InlineData(F.CreatedTime)]
    [InlineData(F.CompletedTime)]
    public async Task Narrowing_MatchesUnnarrowed(long primary)
    {
        await SeedTask("到期邊界", due: Day(1), created: Wall(10, 31, 23), status: TaskItemStatus.Done, completed: new DateTime(2026, 11, 1, 0, 0, 0));
        await SeedTask("月底最後一刻", due: Day(30), created: new DateTime(2026, 11, 30, 23, 59, 0), status: TaskItemStatus.Done,
            completed: new DateTime(2026, 11, 30, 23, 59, 0));
        await SeedTask("下月第一刻", due: new DateOnly(2026, 12, 1), created: new DateTime(2026, 12, 1, 0, 0, 0), status: TaskItemStatus.Done,
            completed: new DateTime(2026, 12, 1, 0, 0, 0));
        await SeedTask("重開後的待辦", due: Day(10), created: Wall(11, 5), completed: Wall(11, 6));
        await SeedTask("沒有到期日", created: Wall(11, 7));
        await SeedTask("上月", due: new DateOnly(2026, 10, 31), created: Wall(10, 1), status: TaskItemStatus.Done, completed: Wall(10, 31, 23));
        var d = TaskDef([M("n", "countMain"), M("todo", "countIf", cond: Fl(F.Status, "eq", Opt(1)))], [G(F.Status)],
            [Fl(F.Status, "in", new[] { Opt(1), Opt(3) })], primary, new ReportDateRange("thisMonth", null));

        var narrowed = await Run(d);
        Svc<ReportOptions>().Engine = ReportEngineMode.MemoryOnly;
        var plain = await Run(d);

        narrowed.State.Should().Be("ok", narrowed.Message);
        narrowed.Total.Should().BeEquivalentTo(plain.Total);
        narrowed.Groups!.Select(g => (g.GroupKeys[0], g.Values["n"])).Should().Equal(plain.Groups!.Select(g => (g.GroupKeys[0], g.Values["n"])));
    }

    // ---- taskEvents ----

    private static ReportDefinition EventDef(List<ReportMeasure> measures, List<ReportGroup>? groups = null, long? primary = null, ReportDateRange? range = null) =>
        TaskDef(measures, groups, primary: primary, range: range, dataset: ReportDatasets.TaskEvents);

    private static ReportFilter IsEvent(long kind) => Fl(F.EventKind, "eq", Opt(kind));

    [Fact]
    public async Task TaskEvents_OneTaskTwoRows()
    {
        await SeedTask("建立後完成", status: TaskItemStatus.Done, created: Wall(11, 2), completed: Wall(11, 3));
        await SeedTask("只建立", created: Wall(11, 3));

        var r = await Run(EventDef([M("n", "countMain"), M("created", "countIf", cond: IsEvent(F.EventCreated)), M("done", "countIf", cond: IsEvent(F.EventCompleted))]));

        r.State.Should().Be("ok", r.Message);
        r.Total!["n"].Should().Be(3);
        r.Total["created"].Should().Be(2);
        r.Total["done"].Should().Be(1);
        r.RowMeaning.Should().Be("一筆任務事件（建立或完成）");
        r.FallbackReasons.Should().Equal("datasetMemoryOnly");
    }

    [Fact]
    public async Task TaskEvents_WeeklyTrend()
    {
        // 基準日 11-04（週三，週一起始）：本週 11-02～11-08、上週 10-26～11-01
        await SeedTask("上週建立本週完成", status: TaskItemStatus.Done, created: Wall(10, 27), completed: Wall(11, 3));
        await SeedTask("上週建立上週完成", status: TaskItemStatus.Done, created: Wall(10, 28), completed: Wall(11, 1, 23));
        await SeedTask("本週建立", created: Wall(11, 2));
        await SeedTask("兩週前建立", created: Wall(10, 20));

        var r = await Run(EventDef([M("created", "countIf", cond: IsEvent(F.EventCreated)), M("done", "countIf", cond: IsEvent(F.EventCompleted))],
            [G(F.EventDate, "week")], F.EventDate, new ReportDateRange("lastWeeks", 2)));

        r.State.Should().Be("ok", r.Message);
        r.Groups!.Select(g => (g.GroupKeys[0], g.Values["created"], g.Values["done"])).Should().Equal(
            (ReportDates.BucketKey(new DateOnly(2026, 10, 26), "week"), 2m, 1m),
            (ReportDates.BucketKey(Day(2), "week"), 1m, 1m));
    }

    [Fact]
    public async Task TaskEvents_ReopenedKeepsLastCompletionOnly()
    {
        var task = await SeedTask("完成又重開", assignee: "admin");
        var days = 0;
        foreach (var status in new[] { "done", "todo", "done" })
        {
            _clock.Now = new DateTime(2026, 11, 4, 9, 0, 0).AddDays(days++);
            var r = await Send(new UpdateTaskRequest(task.Id, JsonSerializer.SerializeToElement(new { status })));
            r.IsSuccess.Should().BeTrue(r.Message);
        }
        (await Load(task.Id)).CompletedTime.Should().NotBeNull();

        var result = await Run(EventDef([M("n", "countMain"), M("done", "countIf", cond: IsEvent(F.EventCompleted))]));

        result.Total!["n"].Should().Be(2, "一列建立、一列（最後一次）完成");
        result.Total["done"].Should().Be(1);

        var detail = await Run(EventDef([M("n", "countMain")]) with { Columns = [T(F.EventKind), T(F.EventDate)] });
        detail.DetailRows!.Where(row => (string)row[0]! == "完成").Select(row => (string)row[1]!).Should()
            .ContainSingle().Which.Should().Be("2026-11-06 09:00", "留下的是最後一次完成的時間，不是第一次的 11-04");
    }

    [Fact]
    public async Task TaskEvents_DoneWithoutCompletedTime_NoCompletionRow()
    {
        await SeedTask("舊資料的完成", status: TaskItemStatus.Done, completed: null);
        await SeedTask("重開後留著完成時間", status: TaskItemStatus.Todo, completed: Wall(11, 3));

        var r = await Run(EventDef([M("n", "countMain"), M("done", "countIf", cond: IsEvent(F.EventCompleted))]));

        r.Total!["n"].Should().Be(2, "只有兩列建立");
        r.Total["done"].Should().Be(0);
    }

    [Fact]
    public async Task Card_TaskEvents_ComparePeriods()
    {
        // 本週（11-02～11-08）完成 1 件；前兩週（10-26～11-01、10-19～10-25）各 2、0 件 → 平均 1，countIf 缺值當 0
        await SeedTask("本週完成", status: TaskItemStatus.Done, created: Wall(10, 1), completed: Wall(11, 3));
        await SeedTask("上週完成一", status: TaskItemStatus.Done, created: Wall(10, 1), completed: Wall(10, 27));
        await SeedTask("上週完成二", status: TaskItemStatus.Done, created: Wall(10, 1), completed: Wall(10, 30));
        var d = EventDef([M("done", "countIf", cond: IsEvent(F.EventCompleted))], [G(F.EventDate, "week")], F.EventDate, new ReportDateRange("thisWeek", null));

        As("admin");
        using var scope = NewScope();
        var r = await scope.ServiceProvider.GetRequiredService<ReportRunner>().RunCardAsync(d, new ReportRunOptions(null, null, ComparePeriods: 2), default);

        r.State.Should().Be("ok", r.Message);
        r.Total!["done"].Should().Be(1);
        r.Compare.Should().NotBeNull();
        r.Compare!.PreviousAverage.Should().Be(1);
    }

    // ---- 下鑽、TaskIds、匯出 ----

    private async Task<ReportResult> Drill(ReportDefinition d, string user, params string[] keys)
    {
        As(user);
        using var scope = NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<ReportRunner>().RunAsync(d, new ReportRunOptions(null, keys), default);
        As("admin");
        return result;
    }

    private async Task<long> SaveReport(ReportDefinition d, string owner)
    {
        As(owner);
        var created = await Send(new CreateReportRequest("任務報表", d));
        As("admin");
        created.IsCreated.Should().BeTrue(created.Message);
        return ((ReportDetail)created.Data!).Id;
    }

    private static string[] FieldLabels(ReportResult r) => [.. r.Columns.Where(c => c.Role == "field").Select(c => c.Label)];

    [Fact]
    public async Task Drill_FixedColumns_WithTaskIds()
    {
        var a = await SeedTask("業務一甲", assignee: "sales01", due: Day(9));
        var b = await SeedTask("業務一乙", assignee: "sales01", status: TaskItemStatus.Done, created: Wall(11, 2), completed: Wall(11, 3));
        await SeedTask("業務二", assignee: "sales02");

        var r = await Drill(TaskDef([M("n", "countMain")], [G(F.Assignee)]), "admin", "sales01");

        r.State.Should().Be("ok", r.Message);
        FieldLabels(r).Should().Equal("標題", "狀態", "負責人", "到期日", "完成時間", "所屬模組");
        r.TaskIds.Should().BeEquivalentTo([a.Id, b.Id]);
        r.DetailRows!.Select(row => (string)row[0]!).Zip(r.TaskIds!).Should().BeEquivalentTo([("業務一甲", a.Id), ("業務一乙", b.Id)]);
        r.DetailRows!.Single(row => (string)row[0]! == "業務一乙").Should().Equal("業務一乙", "已完成", "業務一", null, "2026-11-03 09:00", "（無）");

        var ev = await Drill(EventDef([M("n", "countMain")], [G(F.Assignee)]), "admin", "sales01");

        FieldLabels(ev).Should().Equal("事件種類", "事件日期", "標題", "狀態", "負責人", "所屬模組");
        var rowsOfB = ev.DetailRows!.Where(row => (string)row[2]! == "業務一乙").ToList();
        rowsOfB.Select(row => ((string)row[0]!, (string)row[1]!)).Should().BeEquivalentTo([("建立", "2026-11-02 09:00"), ("完成", "2026-11-03 09:00")]);
    }

    [Fact]
    public async Task Drill_RestrictedGroup_ModuleColumnNoPermission()
    {
        await SeedPersonas();

        var r = await Drill(TaskDef([M("n", "countMain")], [G(F.Module)]), "manager01", ReportCalculator.RestrictedKey);

        r.State.Should().Be("ok", r.Message);
        r.DetailRows!.Should().ContainSingle().Which[0].Should().Be("受限模組的部屬任務");
        r.DetailRows!.Should().OnlyContain(row => (string?)row[5] == "（無權限）");
        Json(r.DetailRows!).Should().NotContain("案件甲").And.NotContain("tk2");
        r.TaskIds.Should().ContainSingle();
    }

    [Fact]
    public async Task Drill_TaskEvents_MapsToTaskIds()
    {
        var done = await SeedTask("完成的", assignee: "sales01", status: TaskItemStatus.Done, created: Wall(11, 2), completed: Wall(11, 3));

        var r = await Drill(EventDef([M("n", "countMain")], [G(F.Assignee)]), "admin", "sales01");

        r.DetailRows!.Should().HaveCount(2);
        r.TaskIds.Should().Equal(done.Id, done.Id);
    }

    [Fact]
    public async Task Drill_OverLimit_TruncatesTaskIdsToo()
    {
        Db.Context.AddRange(Enumerable.Range(0, ReportRules.DrillLimit + 1).Select(i => new TaskItem
        {
            Title = $"任務{i}", CreatedBy = "admin", AssigneeUserId = "sales01", Status = TaskItemStatus.Todo, CreatedTime = SeedTime,
        }));
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        var id = await SaveReport(TaskDef([M("n", "countMain")], [G(F.Assignee)]), "admin");

        var r = await Send(new DrillReportRequest(id, ["sales01"], null, "zh-TW"));

        r.IsSuccess.Should().BeTrue(r.Message);
        var drill = (DrillResult)r.Data!;
        drill.Truncated.Should().BeTrue();
        drill.DetailRows!.Should().HaveCount(ReportRules.DrillLimit);
        drill.TaskIds!.Should().HaveCount(ReportRules.DrillLimit).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Export_RestrictedCell_NoPermissionText()
    {
        await SeedPersonas();
        var d = TaskDef([M("n", "countMain")]) with { Columns = [T(F.Title), T(F.Module)] };

        async Task<Dictionary<string, string>> ExportAs(string user)
        {
            var id = await SaveReport(d, user);
            As(user);
            var r = await Send(new ExportReportRequest(id, "zh-TW"));
            As("admin");
            r.IsSuccess.Should().BeTrue($"{user}：{r.Message}");
            using var wb = new XLWorkbook(new MemoryStream(((ExportFile)r.Data!).Content));
            var ws = wb.Worksheets.Single();
            ws.Row(1).Cell(1).GetString().Should().Be("標題");
            ws.Row(1).Cell(2).GetString().Should().Be("所屬模組");
            // 明細下方空一列接總計（量值名稱、數字），只取明細列
            return ws.RowsUsed().Skip(1).Where(row => row.Cell(2).DataType != XLDataType.Number)
                .ToDictionary(row => row.Cell(1).GetString(), row => row.Cell(2).GetString());
        }

        var manager = await ExportAs("manager01");
        manager["受限模組的部屬任務"].Should().Be("（無權限）");
        manager["自己範圍內的記錄"].Should().Be("tk");
        manager["個人"].Should().Be("（無）");
        manager.Should().HaveCount(8);
        // U9：sales01 沒有任何模組的 PrintExport，看得到報表就能匯出看得到的任務
        (await ExportAs("sales01")).Keys.Should().BeEquivalentTo(["個人", "自己範圍內的記錄", "已刪除記錄上的任務", "凍結模組", "受限模組的部屬任務"]);
    }

    [Fact]
    public async Task Detail_TaskReport_CanExportTrue()
    {
        var (tk, _) = await SeedPersonas();
        var id = await SaveReport(TaskDef([M("n", "countMain")], filters: [Fl(F.Module, "eq", Opt(tk.ModuleId))]), "manager01");

        As("manager01");
        var r = await Send(new GetReportRequest(id));
        As("admin");

        r.IsSuccess.Should().BeTrue(r.Message);
        var detail = (ReportDetail)r.Data!;
        detail.CanExport.Should().BeTrue();
        detail.Definition.GetRawText().Should().Contain(Opt(tk.ModuleId));
    }

    [Fact]
    public async Task ModuleDrill_TaskIdsNull()
    {
        var tk = await BuildTarget("tk");
        await NewRecord(tk, "sales01");
        var d = new ReportDefinition(1, "tk", null, [], [new ReportGroup(new ReportFieldRef("main", tk.UserFieldId, null), null)],
            [new ReportMeasure("n", "countMain", null, null)], [], null, null, [], "table");

        var r = await Drill(d, "admin", "");

        r.State.Should().Be("ok", r.Message);
        r.DetailRows.Should().NotBeEmpty();
        r.TaskIds.Should().BeNull();
        JsonSerializer.Serialize(r).Should().NotContain("taskIds").And.NotContain("TaskIds");
    }

    [Fact]
    public async Task UnknownDataset_ModuleDisabled()
    {
        await SeedTask("待辦");
        (await Run(TaskDef([M("n", "countMain")], dataset: "foo"))).State.Should().Be("moduleDisabled");
        // 被直接改壞的定義（tasks 不能用的欄位、帶出）一律 fail closed，不回數字
        (await Run(TaskDef([M("n", "countMain")], [G(F.EventKind)]))).State.Should().Be("fieldMissing");
        (await Run(TaskDef([M("n", "countMain")], [new ReportGroup(new ReportFieldRef("main", F.Module, F.Rule), null)]))).State.Should().Be("fieldMissing");
        (await Run(TaskDef([M("n", "countMain")]) with { Child = new ReportChild("tk", 1) })).State.Should().Be("fieldMissing");
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

    private async Task<TaskItem> SeedChildOf(TaskItem parent, string title, string? assignee, TaskItemStatus status = TaskItemStatus.Todo)
    {
        var child = await SeedTask(title, createdBy: "admin", assignee: assignee, status: status);
        await Db.Context.Set<TaskItem>().Where(x => x.Id == child.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ParentTaskId, parent.Id));
        return child;
    }

    [Fact]
    public async Task C43_Level_GroupsStandaloneParentChild_AndFiltersEvents()
    {
        await SeedTask("獨立", createdBy: "admin", assignee: "sales01");
        var p = await SeedTask("父", createdBy: "admin", assignee: "sales01");
        await SeedChildOf(p, "子一", "sales01");
        await SeedChildOf(p, "子二已取消", "sales01", TaskItemStatus.Cancelled);
        var p2 = await SeedTask("父二", createdBy: "admin", assignee: "sales01");
        var only = await SeedChildOf(p2, "唯一的子", "sales01");

        var r = await Run(TaskDef([M("n", "countMain")], [G(F.Level)]));
        r.State.Should().Be("ok", r.Message);
        Group(r, "獨立任務").Values["n"].Should().Be(1);
        Group(r, "父任務").Values["n"].Should().Be(2);
        Group(r, "子任務").Values["n"].Should().Be(3);

        // 子任務全刪後父任務回到獨立任務
        await Db.Context.Set<TaskItem>().Where(x => x.Id == only.Id).ExecuteDeleteAsync();
        Db.Context.ChangeTracker.Clear();
        r = await Run(TaskDef([M("n", "countMain")], [G(F.Level)]));
        Group(r, "獨立任務").Values["n"].Should().Be(2);
        Group(r, "父任務").Values["n"].Should().Be(1);

        // taskEvents 也能用 -16 篩：子任務 2 筆各一個建立事件
        var ev = await Run(TaskDef([M("n", "countMain")], filters: [Fl(F.Level, "eq", Opt(F.LevelChild))], dataset: ReportDatasets.TaskEvents));
        ev.State.Should().Be("ok", ev.Message);
        ev.Total!["n"].Should().Be(2);
    }

    [Fact]
    public async Task C43_Level_ChildStaysChild_WhenViewerCannotSeeParent_AndParentStaysParent()
    {
        var p = await SeedTask("父", createdBy: "admin", assignee: "sales01");
        await SeedChildOf(p, "子", "sales02");

        var asSales02 = await Run(TaskDef([M("n", "countMain")], [G(F.Level)]), "sales02");
        asSales02.Groups!.Should().ContainSingle().Which.GroupLabels[0].Should().Be("子任務");
        var asSales01 = await Run(TaskDef([M("n", "countMain")], [G(F.Level)]), "sales01");
        Group(asSales01, "父任務").Values["n"].Should().Be(1);
    }
}
