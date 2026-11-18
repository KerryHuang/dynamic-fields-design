using System.Text.Json;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using F = DynamicModules.Domain.Reports.TaskReportFields;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>任務資料來源的載入、欄位目錄與定義驗證（看板設計 §7.5）：列＝看得到的任務，記錄判斷與看板同一個。</summary>
[Trait("Category", "Postgres")]
public class TaskReportSourceTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private static readonly TaskReportNarrowing None = new(null, null, null, null);

    private static DateTime Wall(int day, int hour = 9, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified);

    private async Task SetPolicy(PolicyDefinition def, string value)
    {
        Db.Context.Add(new SystemSetting { Key = def.SettingKey, Value = value });
        await Db.Context.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<TaskReportItem>> LoadAs(string user, TaskReportNarrowing? narrowing = null)
    {
        As(user);
        using var scope = NewScope();
        var (items, _) = await scope.ServiceProvider.GetRequiredService<TaskReportSource>().LoadAsync(narrowing ?? None, 10_000, default);
        As("admin");
        return items;
    }

    private async Task<HashSet<long>> VisibleIds(string user)
    {
        As(user);
        using var scope = NewScope();
        var v = await scope.ServiceProvider.GetRequiredService<TaskVisibilityService>().ForCurrentUserAsync(default);
        var ids = await scope.ServiceProvider.GetRequiredService<ITaskRepository>().ListVisibleIdsAsync(v, default);
        As("admin");
        return ids.ToHashSet();
    }

    private async Task<HashSet<long>> RecordAccessibleIds(string user)
    {
        Db.Context.ChangeTracker.Clear();
        var all = await Db.Context.Set<TaskItem>().AsNoTracking().Include(t => t.Participants).OrderBy(t => t.Id).ToListAsync();
        As(user);
        using var scope = NewScope();
        var perms = await scope.ServiceProvider.GetRequiredService<TaskAccess>().EvaluateManyAsync(all, default);
        As("admin");
        return all.Where((_, i) => perms[i].CanSee && perms[i].Record?.Accessible == true).Select(t => t.Id).ToHashSet();
    }

    /// <summary>tk2：只有業務群組 Own，業務主管（manager01）沒有任何權限。</summary>
    private async Task<Target> BuildRestrictedTarget(string key)
    {
        var t = await BuildTarget(key);
        var r = await Send(new DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions.SaveModulePermissionsRequest(key,
            new DynamicModules.Application.Dynamic.PermissionService.Data.ModulePermissionsDto(
                [new DynamicModules.Application.Dynamic.PermissionService.Data.GroupPermissionDto(t.SalesGroupId,
                    new DynamicModules.Application.Dynamic.PermissionService.Data.PrivilegesDto(true, false, true, false, false, false, false), DataScope.Own)],
                [], [], [])));
        r.IsSuccess.Should().BeTrue(r.Message);
        return t;
    }

    /// <summary>個人任務、記錄任務（Own 範圍內外各一）、軟刪除記錄的任務、凍結模組的任務、受限模組的部屬任務、只被 @ 的任務、部屬的未指派任務。</summary>
    private async Task SeedPersonas()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var t = await BuildTarget("tk");
        var frozen = await BuildTarget("tk3");
        var restricted = await BuildRestrictedTarget("tk2");
        var own = await NewRecord(t, "sales01");
        var other = await NewRecord(t, "sales02");
        var deleted = await NewRecord(t, "sales01", "已刪除記錄");
        await Db.Context.Set<Record>().Where(r => r.Id == deleted).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeleteFlag, true));
        var recFrozen = await NewRecord(frozen, "sales01");
        await Db.Context.Set<Module>().Where(m => m.Key == "tk3").ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        var recRestricted = await NewRecord(restricted, "sales01");

        await SeedTask("個人", createdBy: "sales01", assignee: "sales01");
        await SeedTask("自己範圍內的記錄", createdBy: "floor01", target: t, recordId: own);
        await SeedTask("範圍外的記錄", createdBy: "floor01", target: t, recordId: other);
        await SeedTask("已刪除記錄上的任務", createdBy: "floor01", target: t, recordId: deleted);
        await SeedTask("凍結模組", createdBy: "floor01", target: frozen, recordId: recFrozen);
        await SeedTask("受限模組的部屬任務", createdBy: "floor01", assignee: "sales01", target: restricted, recordId: recRestricted);
        await SeedTask("只被提到", createdBy: "sales02", mentioned: ["ops01"]);
        await SeedTask("部屬的未指派任務", createdBy: "keeper01");
    }

    private const string Personal = "個人", OwnRecord = "自己範圍內的記錄", OtherRecord = "範圍外的記錄", DeletedRecord = "已刪除記錄上的任務",
        Frozen = "凍結模組", RestrictedSub = "受限模組的部屬任務", Mentioned = "只被提到", SubUnassigned = "部屬的未指派任務";

    /// <summary>
    /// 每個身分應該看到的任務（標題，照 Seed 手算，審查 Y3）與其中看不到記錄的任務。
    /// manager01 管總經理室（含下層全部部門，遞迴預設開），其他人都是部屬；ops01 管營運部（keeper01、採購、生產部）；
    /// 軟刪除記錄與關閉模組的記錄仍在 Read 範圍內就看得到；tk2 只有業務 Own，manager01 看不到那筆記錄。
    /// </summary>
    public static TheoryData<string, string[], string[]> Personas => new()
    {
        { "admin", [Personal, OwnRecord, OtherRecord, DeletedRecord, Frozen, RestrictedSub, Mentioned, SubUnassigned], [] },
        { "sales01", [Personal, OwnRecord, DeletedRecord, Frozen, RestrictedSub], [] },
        { "sales02", [OtherRecord, Mentioned], [] },
        { "manager01", [Personal, OwnRecord, OtherRecord, DeletedRecord, Frozen, RestrictedSub, Mentioned, SubUnassigned], [RestrictedSub] },
        { "ops01", [OwnRecord, OtherRecord, DeletedRecord, Frozen, Mentioned, SubUnassigned], [OwnRecord, OtherRecord, DeletedRecord, Frozen] },
        { "floor01", [OwnRecord, OtherRecord, DeletedRecord, Frozen, RestrictedSub], [OwnRecord, OtherRecord, DeletedRecord, Frozen, RestrictedSub] },
        { "keeper01", [SubUnassigned], [] },
        { "nobody", [], [] },
    };

    [Theory]
    [MemberData(nameof(Personas))]
    public async Task Load_EqualsVisibleIds_ForEveryPersona(string user, string[] visible, string[] recordHidden)
    {
        await SeedPersonas();

        var items = await LoadAs(user);

        items.Select(i => i.Task.Title).Should().BeEquivalentTo(visible, $"身分 {user}");
        items.Where(i => i.Task.RecordId is not null && !i.RecordAccessible).Select(i => i.Task.Title).Should().BeEquivalentTo(recordHidden, $"身分 {user}");
        // 報表＝看板：與看板的可見 Id、逐筆記錄判斷一致
        items.Select(i => i.Task.Id).Should().BeEquivalentTo(await VisibleIds(user), $"身分 {user}");
        items.Where(i => i.RecordAccessible).Select(i => i.Task.Id).Should().BeEquivalentTo(await RecordAccessibleIds(user), $"身分 {user}");
        items.Select(i => i.Task.Id).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Load_OverLimit_JudgedOnRawCount()
    {
        await SeedTask("一");
        await SeedTask("二");
        await SeedTask("三");
        using var scope = NewScope();
        var source = scope.ServiceProvider.GetRequiredService<TaskReportSource>();

        var (items, over) = await source.LoadAsync(None, 2, default);
        var (all, notOver) = await source.LoadAsync(None, 4, default);

        over.Should().BeTrue();
        items.Should().HaveCount(2);
        notOver.Should().BeFalse();
        all.Should().HaveCount(3);
    }

    [Fact]
    public async Task Load_SupervisorPolicyOff_DropsSubordinateTasks()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var task = await SeedTask("業務的", createdBy: "sales01", assignee: "sales01");
        (await LoadAs("manager01")).Select(i => i.Task.Id).Should().Contain(task.Id);

        await SetPolicy(PolicyCatalog.TaskSupervisorScope, "false");

        (await LoadAs("manager01")).Select(i => i.Task.Id).Should().NotContain(task.Id);
    }

    [Fact]
    public async Task Load_AssigneeDepartment_IsCurrentDepartment()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        await SeedTask("業務的", createdBy: "admin", assignee: "sales01");
        await SeedTask("沒人接", createdBy: "admin");
        var sales = await Db.Context.Set<Department>().Where(d => d.Code == "sales").Select(d => d.Id).SingleAsync();

        var items = await LoadAs("admin");

        items.Single(i => i.Task.Title == "業務的").AssigneeDepartmentId.Should().Be(sales);
        items.Single(i => i.Task.Title == "沒人接").AssigneeDepartmentId.Should().BeNull();
    }

    [Fact]
    public async Task Catalog_CacheIsPerUser_AndReset()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        await BuildTarget("tk");
        await BuildRestrictedTarget("tk2");
        using var scope = NewScope();
        var source = scope.ServiceProvider.GetRequiredService<TaskReportSource>();
        var visibility = scope.ServiceProvider.GetRequiredService<TaskVisibilityService>();
        long[] ModuleOptions(Module m) => m.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == F.Module).Options.Select(o => o.Id).ToArray();

        As("manager01");
        var manager = ModuleOptions(await source.CatalogAsync(ReportDatasets.Tasks, default));
        As("admin");
        var admin = ModuleOptions(await source.CatalogAsync(ReportDatasets.Tasks, default));
        admin.Should().HaveCount(2);
        manager.Should().HaveCount(1, "業務主管讀不到 tk2");

        await BuildTarget("tk4");
        ModuleOptions(await source.CatalogAsync(ReportDatasets.Tasks, default)).Should().HaveCount(2, "同一個 scope 內快取");
        source.Reset();
        visibility.Reset();
        ModuleOptions(await source.CatalogAsync(ReportDatasets.Tasks, default)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Load_QueryCount_DoesNotGrowWithTaskCount()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var targets = new[] { await BuildTarget("tk"), await BuildTarget("tk2"), await BuildTarget("tk3") };
        var records = new List<(Target T, long Id)>();
        for (var i = 0; i < 10; i++)
            foreach (var target in targets) records.Add((target, await NewRecord(target, "sales01", $"案件{i}")));

        async Task<int> CountFor(int taskCount)
        {
            Db.Context.Set<TaskItem>().RemoveRange(Db.Context.Set<TaskItem>());
            await Db.Context.SaveChangesAsync();
            for (var i = 0; i < taskCount; i++)
            {
                var (target, id) = records[i % records.Count];
                await SeedTask($"任務{i}", createdBy: "floor01", assignee: i % 2 == 0 ? "sales02" : null, target: target, recordId: id);
            }
            Db.Context.ChangeTracker.Clear();
            As("manager01");
            using var scope = NewScope();
            var source = scope.ServiceProvider.GetRequiredService<TaskReportSource>();
            var before = Db.Counter.Count;
            var (items, _) = await source.LoadAsync(None, 10_000, default);
            var used = Db.Counter.Count - before;
            As("admin");
            items.Should().HaveCount(taskCount);
            return used;
        }

        var small = await CountFor(10);
        var large = await CountFor(40);

        large.Should().Be(small, "查詢次數不隨任務筆數成長");
    }

    [Fact]
    public async Task Load_Narrowing_StatusAndDates()
    {
        var inRange = await SeedTask("區間內完成", status: TaskItemStatus.Done, created: Wall(1), completed: Wall(1, 0, 0));
        var lastDay = await SeedTask("最後一天完成", status: TaskItemStatus.Done, created: Wall(1), completed: Wall(3, 23, 59));
        var after = await SeedTask("區間後完成", status: TaskItemStatus.Done, created: Wall(1), completed: Wall(4, 0, 0));
        var createdInRange = await SeedTask("區間內建立的待辦", created: Wall(2));
        await SeedTask("區間前建立的待辦", created: new DateTime(2026, 10, 31, 23, 59, 0, DateTimeKind.Unspecified));
        var due = await SeedTask("到期在區間", due: new DateOnly(2026, 11, 3), created: Wall(5));

        async Task<long[]> Ids(TaskReportNarrowing n) => (await LoadAs("admin", n)).Select(i => i.Task.Id).ToArray();
        DateOnly from = new(2026, 11, 1), to = new(2026, 11, 3);

        (await Ids(new([TaskItemStatus.Done], "completed", from, to))).Should().BeEquivalentTo([inRange.Id, lastDay.Id]);
        (await Ids(new(null, "created", from, to))).Should().BeEquivalentTo([inRange.Id, lastDay.Id, after.Id, createdInRange.Id]);
        (await Ids(new([TaskItemStatus.Todo], "createdOrCompleted", from, to))).Should().BeEquivalentTo([createdInRange.Id]);
        (await Ids(new(null, "due", from, to))).Should().BeEquivalentTo([due.Id]);
        (await Ids(new([], null, null, null))).Should().BeEmpty();
    }

    [Fact]
    public async Task Catalog_OptionsFollowViewer()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var tk = await BuildTarget("tk");
        var tk2 = await BuildRestrictedTarget("tk2");
        Db.Context.Add(new Department { Code = "old", NameI18n = Text("舊部門"), Active = false, CreatedUserId = "admin", CreatedTime = SeedTime });
        Db.Context.Add(new TaskRule { Key = "r-tk", NameI18n = Text("tk 的規則"), ModuleId = tk.ModuleId, CreatedUserId = "admin", CreatedTime = SeedTime });
        Db.Context.Add(new TaskRule { Key = "r-tk2", NameI18n = Text("tk2 的規則"), ModuleId = tk2.ModuleId, CreatedUserId = "admin", CreatedTime = SeedTime });
        await Db.Context.SaveChangesAsync();

        async Task<Module> CatalogAs(string user)
        {
            As(user);
            using var scope = NewScope();
            var m = await scope.ServiceProvider.GetRequiredService<TaskReportSource>().CatalogAsync(ReportDatasets.Tasks, default);
            As("admin");
            return m;
        }
        Field Of(Module m, long id) => m.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == id);

        var manager = await CatalogAs("manager01");
        Of(manager, F.Module).Options.Select(o => o.Id).Should().BeEquivalentTo([tk.ModuleId]);
        Of(manager, F.Rule).Options.Select(o => o.LabelI18n.Resolve("zh-TW")).Should().BeEquivalentTo(["tk 的規則"]);
        var admin = await CatalogAs("admin");
        Of(admin, F.Module).Options.Select(o => o.Id).Should().BeEquivalentTo([tk.ModuleId, tk2.ModuleId]);
        Of(admin, F.Rule).Options.Should().HaveCount(2);
        Of(manager, F.AssigneeDepartment).Options.Should().Contain(o => o.LabelI18n.Resolve("zh-TW") == "舊部門" && !o.Active);
    }

    // ---- 定義驗證 ----

    private static ReportFieldRef T(long code) => new("main", code, null);

    private static ReportFilter Cond(long code, string op, object? value) =>
        new(T(code), op, value is null ? null : JsonSerializer.SerializeToElement(value));

    private static ReportDefinition TaskDef(string dataset = ReportDatasets.Tasks, string? main = "", ReportChild? child = null,
        List<ReportGroup>? groups = null, List<ReportMeasure>? measures = null, List<ReportFilter>? filters = null, List<ReportFieldRef>? columns = null) =>
        new(1, main!, child, columns ?? [], groups ?? [], measures ?? [new ReportMeasure("m1", "countMain", null, null)], filters ?? [], null, null, [], "table", dataset);

    private async Task<(ReportDefinition? Definition, DynamicModules.Application.Common.ServiceResult? Failure)> Validate(ReportDefinition d, string user = "admin")
    {
        As(user);
        using var scope = NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<ReportDefinitionValidator>().ValidateAsync("報表", d, default);
        As("admin");
        return result;
    }

    [Fact]
    public async Task Validate_ModuleDatasetNormalized()
    {
        await BuildTarget("tk");
        var plain = new ReportDefinition(1, "tk", null, [], [], [new ReportMeasure("m1", "countMain", null, null)], [], null, null, [], "table");

        var (withModule, f1) = await Validate(plain with { Dataset = ReportDatasets.Module });
        var (without, f2) = await Validate(plain);

        f1.Should().BeNull();
        f2.Should().BeNull();
        ReportDefinition.Serialize(withModule!).Should().Be(ReportDefinition.Serialize(without!)).And.NotContain("dataset");
        var (task, f3) = await Validate(TaskDef(main: null));
        f3.Should().BeNull();
        task!.MainModuleKey.Should().Be("");
        task.Dataset.Should().Be(ReportDatasets.Tasks);
    }

    [Fact]
    public async Task Validate_TaskDefinitionShape_400()
    {
        await BuildTarget("tk");
        var bad = new[]
        {
            TaskDef(main: "tk"),
            TaskDef(child: new ReportChild("tk", 1)),
            TaskDef(columns: [new ReportFieldRef("main", F.Title, F.Module)]),
            TaskDef(dataset: "foo"),
            TaskDef(dataset: ReportDatasets.TaskEvents, columns: [T(F.Overdue)]),
            TaskDef(dataset: ReportDatasets.TaskEvents, columns: [T(F.Title)]),
            TaskDef(groups: [new ReportGroup(T(F.Title), null)]),
            TaskDef(measures: [new ReportMeasure("m1", "countChild", null, null)]),
            TaskDef(columns: [T(-17)]),
            TaskDef(columns: [T(99)]),
        };
        foreach (var d in bad)
        {
            var (ok, failure) = await Validate(d);
            ok.Should().BeNull(JsonSerializer.Serialize(d));
            failure!.IsInvalid.Should().BeTrue(JsonSerializer.Serialize(d));
        }
    }

    [Fact]
    public async Task Validate_ValidTaskDefinition_Normalized()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var tk = await BuildTarget("tk");
        var tk2 = await BuildRestrictedTarget("tk2");

        var (d, failure) = await Validate(TaskDef(
            groups: [new ReportGroup(T(F.Assignee), null), new ReportGroup(T(F.CompletedTime), "week")],
            measures: [new ReportMeasure("m1", "countIf", null, Cond(F.Overdue, "eq", true)), new ReportMeasure("m2", "avg", T(F.DelayDays), Cond(F.Completion, "eq", "opt_02"))],
            filters: [Cond(F.Status, "in", new[] { "opt_1", "opt_2" }), Cond(F.Assignee, "eq", "sales01"), Cond(F.Module, "eq", $"opt_{tk.ModuleId}")]), "manager01");

        failure.Should().BeNull();
        d!.Filters[0].Value!.Value.GetRawText().Should().Be("""["opt_1","opt_2"]""");
        d.Measures[1].Condition!.Value!.Value.GetString().Should().Be("opt_2");

        var (_, hidden) = await Validate(TaskDef(filters: [Cond(F.Module, "eq", $"opt_{tk2.ModuleId}")]), "manager01");
        hidden!.IsInvalid.Should().BeTrue("看不到的模組不在選項裡");
        var (_, unknownUser) = await Validate(TaskDef(filters: [Cond(F.Assignee, "eq", "ghost")]));
        unknownUser!.IsInvalid.Should().BeTrue();
    }
}
