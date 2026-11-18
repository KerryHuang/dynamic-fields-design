using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>
/// 甘特查詢 POST /tasks/gantt（看板設計 §11.5.2.5，G34–G48，另驗 gt1b 的 G33）：時間窗與相交、範圍與篩選同看板、未排程、parents、spans、上限、查詢次數。
/// 今天＝2026-11-18（星期三），A7 出廠＝星期一。
/// </summary>
[Trait("Category", "Postgres")]
public class TaskGanttQueryTests : TaskBoardTestBase
{
    private const string Outsider = "outsider01";
    private const string Flat = """{"version":1,"groupBy":"none","scope":"assigned","scale":"week"}""";

    private static DateOnly N(int day) => new(2026, 11, day);
    private static DateOnly Dec(int day) => new(2026, 12, day);
    private static TaskGanttWindowInput W(DateOnly from, DateOnly to) => new(from, to);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        BusinessClock.Now = new DateTime(2026, 11, 18, 9, 0, 0);
        Db.Context.Add(new AppUser { UserId = Outsider, DisplayName = "外部人員", IsActive = true });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }

    private Task<ServiceResult> Raw(string user, string? config = null, long? viewId = null, TaskGanttWindowInput? window = null) =>
        SendAs(user, new GetTaskGanttRequest(viewId, config is null ? null : J(config), window));

    private async Task<TaskGanttResponse> Gantt(string user, string? config = null, long? viewId = null, TaskGanttWindowInput? window = null)
    {
        var r = await Raw(user, config, viewId, window);
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value)));
        return (TaskGanttResponse)r.Data!;
    }

    private static string Config(string groupBy = "none", string scope = "assigned", string? filters = null, bool? showSubtasks = null) =>
        $$"""{"version":1,"groupBy":"{{groupBy}}","scope":"{{scope}}","scale":"week"{{(filters is null ? "" : $",\"filters\":{filters}")}}{{(showSubtasks is null ? "" : $",\"showSubtasks\":{(showSubtasks.Value ? "true" : "false")}")}}}""";

    private async Task<TaskItem> SeedChild(TaskItem parent, string title, string? createdBy = "sales01", string? assignee = null,
        TaskItemStatus status = TaskItemStatus.Todo, DateOnly? start = null, DateOnly? due = null)
    {
        var child = await SeedTask(title, createdBy: createdBy, assignee: assignee, status: status, due: due, start: start,
            recordId: parent.RecordId, moduleId: parent.ModuleId);
        await Db.Context.Set<TaskItem>().Where(t => t.Id == child.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ParentTaskId, parent.Id));
        return child;
    }

    [Fact]
    public async Task G34_Intersection_OrderAndUnscheduled()
    {
        var a = await SeedTask("A 跨進", createdBy: "sales01", assignee: "sales01", start: N(10), due: N(17));
        var b = await SeedTask("B 菱形", createdBy: "sales01", assignee: "sales01", due: N(20));
        await SeedTask("C 之前", createdBy: "sales01", assignee: "sales01", start: N(1), due: N(15));
        var d = await SeedTask("D 只有開始日", createdBy: "sales01", assignee: "sales01", start: N(18));
        var e = await SeedTask("E 都沒有", createdBy: "sales01", assignee: "sales01");
        var f = await SeedTask("F 跨出", createdBy: "sales01", assignee: "sales01", start: N(28), due: Dec(10));

        var g = await Gantt("sales01", Flat, window: W(N(16), N(29)));

        g.Rows.Select(r => r.Id).Should().Equal(a.Id, b.Id, f.Id);
        g.Rows[0].StartDate.Should().Be(N(10));
        g.Total.Should().Be(3);
        g.Truncated.Should().BeFalse();
        g.Unscheduled.Select(r => r.Id).Should().Equal(d.Id, e.Id);
        g.Unscheduled[0].StartDate.Should().Be(N(18));
        g.UnscheduledTotal.Should().Be(2);
        g.Window.Should().Be(new TaskGanttWindow(N(16), N(29)));
        g.Today.Should().Be(N(18));
        g.WeekStart.Should().Be("monday");
    }

    [Fact]
    public async Task G34_WindowEnds_AreInclusive_TieBreakByDueThenId()
    {
        var onFrom = await SeedTask("到期在第一天", createdBy: "sales01", assignee: "sales01", due: N(16));
        var onTo = await SeedTask("開始在最後一天", createdBy: "sales01", assignee: "sales01", start: N(29), due: Dec(5));
        await SeedTask("到期在前一天", createdBy: "sales01", assignee: "sales01", due: N(15));
        await SeedTask("開始在隔天", createdBy: "sales01", assignee: "sales01", start: N(30), due: Dec(5));
        var later = await SeedTask("同起點、晚到期", createdBy: "sales01", assignee: "sales01", start: N(16), due: N(20));
        var sooner = await SeedTask("同起點、早到期", createdBy: "sales01", assignee: "sales01", start: N(16), due: N(18));

        var g = await Gantt("sales01", Flat, window: W(N(16), N(29)));

        g.Rows.Select(r => r.Id).Should().Equal(onFrom.Id, sooner.Id, later.Id, onTo.Id);
    }

    [Fact]
    public async Task G35_WindowValidation_AndDefaults()
    {
        (await Raw("sales01", Flat, window: W(N(20), N(19)))).Errors["window"].Should().Equal("時間範圍的開始不能晚於結束");
        (await Raw("sales01", Flat, window: W(N(1), N(1).AddDays(366)))).Errors["window"].Should().Equal("時間範圍最多 366 天");
        (await Raw("sales01", Flat, window: new TaskGanttWindowInput(N(1), null))).Errors["window"].Should().Equal("時間範圍的開始與結束都必填");
        (await Raw("sales01", Flat, window: new TaskGanttWindowInput(null, null))).Errors.Should().ContainKey("window");
        (await Gantt("sales01", Flat, window: W(N(1), N(1).AddDays(365)))).Window.To.Should().Be(N(1).AddDays(365), "366 天剛好可以");
        (await Gantt("sales01", Flat, window: W(N(18), N(18)))).Window.Should().Be(new TaskGanttWindow(N(18), N(18)));

        (await Gantt("sales01", Flat)).Window.Should().Be(new TaskGanttWindow(N(16), new DateOnly(2027, 2, 14)));
        (await Gantt("sales01", Flat.Replace("week", "day"))).Window.Should().Be(new TaskGanttWindow(N(16), N(29)));
        (await Gantt("sales01", Flat.Replace("week", "month"))).Window.Should().Be(new TaskGanttWindow(N(1), new DateOnly(2027, 4, 30)));
        (await Gantt("sales01")).Window.Should().Be(new TaskGanttWindow(N(16), new DateOnly(2027, 2, 14)), "預設甘特圖是週刻度");

        (await SetWeekStart("sunday")).IsSuccess.Should().BeTrue();
        var sunday = await Gantt("sales01", Flat);
        sunday.Window.Should().Be(new TaskGanttWindow(N(15), new DateOnly(2027, 2, 13)));
        sunday.WeekStart.Should().Be("sunday");
    }

    [Fact]
    public async Task G36_ScopesAndFilters_SameAsBoard()
    {
        var t = await BuildTarget();
        var rec = await NewRecord(t, "sales01");
        var rule = await SeedRule(t, "gantt-rule", "甘特規則");
        await SeedTask("業務一的展覽", createdBy: "sales01", assignee: "sales01", due: N(20));
        await SeedTask("業務一進行中", createdBy: "manager01", assignee: "sales01", status: TaskItemStatus.InProgress, start: N(10), due: N(25));
        await SeedTask("業務一完成", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Done, due: N(12));
        await SeedTask("業務一取消", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Cancelled);
        await SeedTask("業務二的", createdBy: "sales02", assignee: "sales02", due: N(19));
        await SeedTask("提到業務一", createdBy: "sales03", assignee: "sales03", mentioned: ["sales01"], due: N(22));
        await SeedTask("未指派", createdBy: "sales02", assignee: null, due: N(23));
        await SeedTask("記錄任務", createdBy: "sales01", assignee: "sales01", target: t, recordId: rec, due: N(24));
        await SeedTask("規則任務", createdBy: null, assignee: "sales01", target: t, recordId: rec, ruleId: rule, start: N(21), due: N(26));
        await SeedTask("主管自己的", createdBy: "manager01", assignee: "manager01", due: N(27));
        var parent = await SeedTask("展覽父任務", createdBy: "sales01", assignee: "sales01", start: N(5), due: N(28));
        await SeedChild(parent, "展覽子任務", assignee: "sales02", start: N(6), due: N(9));

        var filters = new string?[]
        {
            null,
            """{"statuses":["todo"]}""",
            """{"statuses":["inProgress","done","cancelled"]}""",
            """{"assigneeUserIds":["sales02"],"includeUnassigned":true}""",
            """{"includeUnassigned":false}""",
            """{"creatorUserIds":["sales01"]}""",
            """{"sources":["record","rule"]}""",
            """{"sources":["personal"]}""",
            """{"keyword":"展覽"}""",
        };
        foreach (var user in new[] { "sales01", "manager01", "admin", Outsider })
            foreach (var scope in TaskBoardConfig.Scopes)
                foreach (var f in filters)
                    foreach (var sub in new bool?[] { null, false })
                    {
                        var because = $"{user} {scope} {f} showSubtasks={sub}";
                        var board = await Board(user, $$"""{"version":1,"groupBy":"status","scope":"{{scope}}"{{(f is null ? "" : $",\"filters\":{f}")}}{{(sub is null ? "" : ",\"showSubtasks\":false")}}}""");
                        var g = await Gantt(user, Config("parent", scope, f, sub), window: W(N(1), Dec(31)));
                        g.Rows.Concat(g.Unscheduled).Select(c => c.Id).Should()
                            .BeEquivalentTo(board.Columns.SelectMany(c => c.Cards).Select(c => c.Id), because);
                        g.ScopeAvailable.Should().Be(board.ScopeAvailable, because);
                    }

        var top = await Gantt("sales01", Config("parent", "visible", showSubtasks: false), window: W(N(1), Dec(31)));
        top.Rows.Should().OnlyContain(c => c.ParentTaskId == null);
        top.Spans.Should().ContainSingle().Which.Should().Be(new TaskGanttSpan(parent.Id, N(6), N(9)));
    }

    [Fact]
    public async Task G37_SubordinateScope_NoSubordinates_EmptyWithFlag()
    {
        await SeedTask("自己的", createdBy: "sales01", assignee: "sales01", due: N(20));

        var g = await Gantt("sales01", Config(scope: "subordinates"));

        g.ScopeAvailable.Should().BeFalse();
        g.Rows.Should().BeEmpty();
        g.Unscheduled.Should().BeEmpty();
        g.Total.Should().Be(0);
    }

    [Fact]
    public async Task G38_NoLeak_OutsiderAndMaskedRecord()
    {
        await SeedTask("業務一的機密任務", createdBy: "sales01", assignee: "sales01", due: N(20));
        await SeedTask("業務二沒日期", createdBy: "sales02", assignee: "sales02");
        var mine = await SeedTask("外部自己的", createdBy: Outsider, assignee: Outsider, due: N(21));

        var outsider = await Gantt(Outsider, Config(scope: "visible"));
        outsider.Rows.Concat(outsider.Unscheduled).Select(c => c.Id).Should().Equal(mine.Id);
        WireText(outsider).Should().NotContain("業務一的機密任務").And.NotContain("業務二沒日期");

        var t = await BuildTarget("secretmod");
        var rec = await NewRecord(t, "sales01", "機密案件");
        await SeedTask("掛在看不到的記錄上", createdBy: null, assignee: "keeper01", target: t, recordId: rec, due: N(19));

        var ops = await Gantt("ops01", Config(scope: "subordinates"));
        var card = ops.Rows.Single();
        card.Record!.Accessible.Should().BeFalse();
        card.Record.ModuleKey.Should().BeNull();
        card.Record.Title.Should().BeNull();
        WireText(ops).Should().NotContain("機密案件").And.NotContain("secretmod").And.NotContain("案件甲");
    }

    [Fact]
    public async Task G39_ParentTitles_FilteredOutOrOutsideWindowOrInRows()
    {
        var filtered = await SeedTask("被狀態擋掉的父任務", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.InProgress, due: N(20));
        await SeedChild(filtered, "子一", assignee: "sales01", due: N(19));
        var outside = await SeedTask("時間窗外的父任務", createdBy: "sales01", assignee: "sales01", start: Dec(1), due: Dec(20));
        await SeedChild(outside, "子二", assignee: "sales01", due: N(21));
        var undated = await SeedTask("沒日期的父任務", createdBy: "sales01", assignee: "sales01");
        await SeedChild(undated, "子三", assignee: "sales01", due: N(22));
        var inRows = await SeedTask("在列裡的父任務", createdBy: "sales01", assignee: "sales01", due: N(25));
        await SeedChild(inRows, "子四", assignee: "sales01", due: N(23));
        const string todoOnly = """{"statuses":["todo"]}""";

        var g = await Gantt("sales01", Config("parent", filters: todoOnly), window: W(N(16), N(29)));

        g.Rows.Select(r => r.Title).Should().Equal("子一", "子二", "子三", "子四", "在列裡的父任務");
        g.Parents.Select(p => p.Id).Should().Equal(filtered.Id, outside.Id, undated.Id);
        g.Parents[0].Should().Be(new TaskGanttParent(filtered.Id, "被狀態擋掉的父任務", true, null));
        g.Parents[1].Visible.Should().BeTrue();
        g.Parents[1].Row!.Id.Should().Be(outside.Id);
        g.Parents[1].Row!.StartDate.Should().Be(Dec(1));
        g.Parents[1].Row!.Progress.Should().NotBeNull();
        g.Parents[2].Row!.Id.Should().Be(undated.Id);
        g.Unscheduled.Select(u => u.Id).Should().Equal(undated.Id);
        g.Parents.Should().NotContain(p => p.Id == inRows.Id);
        g.Spans.Select(s => s.ParentId).Should().BeEquivalentTo([filtered.Id, outside.Id, undated.Id, inRows.Id]);
        g.Spans.Single(s => s.ParentId == outside.Id).Should().Be(new TaskGanttSpan(outside.Id, N(21), N(21)));

        var byAssignee = await Gantt("sales01", Config("assignee", filters: todoOnly), window: W(N(16), N(29)));
        byAssignee.Rows.Should().HaveCount(5);
        byAssignee.Parents.Should().BeEmpty();
        byAssignee.Spans.Should().BeEmpty();
    }

    /// <summary>G40、G41：sales01 的個人任務 P，C1 指派給 sales02（11/10–11/12）、C2 只有 sales01 參與（11/20–11/30）。</summary>
    private async Task<(TaskItem P, TaskItem C1, TaskItem C2)> SeedU13()
    {
        var p = await SeedTask("規劃活動內容", createdBy: "sales01", assignee: "sales01");
        var c1 = await SeedChild(p, "訂場地", createdBy: "sales01", assignee: "sales02", start: N(10), due: N(12));
        var c2 = await SeedChild(p, "寫新聞稿的祕密", createdBy: "sales01", assignee: "sales01", start: N(20), due: N(30));
        return (p, c1, c2);
    }

    [Fact]
    public async Task G40_OnlyChildVisible_TitleOnly_SpanFromRowsOnly()
    {
        var (p, c1, _) = await SeedU13();

        var g = await Gantt("sales02", Config("parent", "involved"), window: W(N(1), Dec(31)));

        g.Rows.Select(r => r.Id).Should().Equal(c1.Id);
        g.Parents.Should().Equal(new TaskGanttParent(p.Id, "規劃活動內容", false, null));
        g.Spans.Should().Equal(new TaskGanttSpan(p.Id, N(10), N(12)));
        WireText(g).Should().NotContain("寫新聞稿的祕密").And.NotContain("2026-11-30").And.NotContain("2026-11-20");
    }

    [Fact]
    public async Task G41_ParentVisible_SpanUsesAllChildren()
    {
        var (p, c1, c2) = await SeedU13();

        var g = await Gantt("sales01", Config("parent", "assigned"), window: W(N(1), N(30)));

        g.Rows.Select(r => r.Id).Should().Equal(c2.Id);
        g.Rows.Should().NotContain(r => r.Id == c1.Id);
        g.Parents.Should().ContainSingle().Which.Row!.Id.Should().Be(p.Id);
        g.Spans.Should().Equal(new TaskGanttSpan(p.Id, N(10), N(30)));
    }

    [Fact]
    public async Task G42_CancelledChildren_NotInSpan()
    {
        var (p, _, _) = await SeedU13();
        await SeedChild(p, "已取消的", assignee: "sales01", status: TaskItemStatus.Cancelled, start: Dec(1), due: Dec(20));
        await SeedChild(p, "完成的只有開始日", assignee: "sales01", status: TaskItemStatus.Done, start: N(8));
        var q = await SeedTask("全部取消或沒日期", createdBy: "sales01", assignee: "sales01", due: N(25));
        await SeedChild(q, "取消", assignee: "sales02", status: TaskItemStatus.Cancelled, due: N(20));
        await SeedChild(q, "沒日期", assignee: "sales02");

        var g = await Gantt("sales01", Config("parent", "visible"), window: W(N(1), Dec(31)));

        g.Spans.Single(s => s.ParentId == p.Id).Should().Be(new TaskGanttSpan(p.Id, N(8), N(30)), "已完成的算，已取消的不算");
        g.Spans.Should().NotContain(s => s.ParentId == q.Id);
        g.Rows.Should().Contain(r => r.Id == q.Id);
    }

    [Fact]
    public async Task G43_Caps_RowsAndUnscheduled()
    {
        var list = new List<TaskItem>();
        for (var i = 0; i < 501; i++) list.Add(NewTask($"列 {i:000}", N(20)));
        for (var i = 0; i < 101; i++) list.Add(NewTask($"未排程 {i:000}", null));
        await AddAll(list);

        var g = await Gantt("sales01", Flat, window: W(N(16), N(29)));

        g.Rows.Should().HaveCount(GetTaskGanttHandler.MaxRows);
        g.Total.Should().Be(501);
        g.Truncated.Should().BeTrue();
        g.Unscheduled.Should().HaveCount(GetTaskGanttHandler.MaxUnscheduled);
        g.UnscheduledTotal.Should().Be(101);
        g.UnscheduledTruncated.Should().BeTrue();
    }

    /// <summary>
    /// 父子分組遇到 500 列上限：排在 500 名之後、看得到又符合篩選的父任務仍出現在 parents 且帶 row；
    /// 子任務被截掉時，看得到的父任務跨度仍用資料庫全部子任務算（含被截掉的）。
    /// </summary>
    [Fact]
    public async Task G43_ParentGrouping_ParentRankedBeyondCap_StillHasRow()
    {
        var p = await SeedTask("排在 500 名後的父任務", createdBy: "sales01", assignee: "sales01", due: N(25));
        var inRows = await SeedChild(p, "在列裡的子任務", assignee: "sales01", due: N(16));
        var cut = await SeedChild(p, "被截掉的子任務", assignee: "sales01", due: N(28));
        var fillers = Enumerable.Range(0, GetTaskGanttHandler.MaxRows - 1).Select(i => NewTask($"填充 {i:000}", N(17))).ToList();
        await AddAll(fillers);

        var g = await Gantt("sales01", Config("parent"), window: W(N(16), N(29)));

        g.Rows.Should().HaveCount(GetTaskGanttHandler.MaxRows);
        g.Total.Should().Be(GetTaskGanttHandler.MaxRows + 2);
        g.Truncated.Should().BeTrue();
        g.Rows[0].Id.Should().Be(inRows.Id);
        g.Rows.Should().NotContain(r => r.Id == p.Id || r.Id == cut.Id, "父任務與被截掉的子任務都排在 500 名之後");
        var parent = g.Parents.Should().ContainSingle().Subject;
        parent.Id.Should().Be(p.Id);
        parent.Visible.Should().BeTrue();
        parent.Row!.Id.Should().Be(p.Id);
        g.Spans.Should().Equal(new TaskGanttSpan(p.Id, N(16), N(28))); // 看得到的父任務跨度含被截掉的子任務
    }

    /// <summary>父任務看不到：超過上限時 row 仍是 null、只有標題，跨度只用留在 rows 裡的子任務（被截掉的子任務日期不外洩）。</summary>
    [Fact]
    public async Task G43_ParentGrouping_InvisibleParent_RowNull_SpanExcludesCutChildren()
    {
        var p = await SeedTask("看不到的父任務", createdBy: "sales01", assignee: "sales01", due: N(25));
        var inRows = await SeedChild(p, "留在列裡", assignee: "sales02", due: N(16));
        var cut = await SeedChild(p, "被截掉的子任務", assignee: "sales02", due: N(28));
        var fillers = Enumerable.Range(0, GetTaskGanttHandler.MaxRows - 1).Select(i => NewTask($"填充 {i:000}", N(17), "sales02")).ToList();
        await AddAll(fillers);

        var g = await Gantt("sales02", Config("parent", "involved"), window: W(N(16), N(29)));

        g.Truncated.Should().BeTrue();
        g.Rows.Should().HaveCount(GetTaskGanttHandler.MaxRows);
        g.Rows.Should().Contain(r => r.Id == inRows.Id).And.NotContain(r => r.Id == cut.Id);
        g.Parents.Should().Equal(new TaskGanttParent(p.Id, "看不到的父任務", false, null));
        g.Spans.Should().Equal(new TaskGanttSpan(p.Id, N(16), N(16)));
        WireText(g).Should().NotContain("2026-11-28");
    }

    [Fact]
    public async Task G44_QueryCount_DoesNotGrowWithRows()
    {
        // 父任務（他人建立、sales01 看不到）與父任務（sales01 的、被狀態擋掉）各一半，子任務都在時間軸上
        const string config = """{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","filters":{"statuses":["todo"]}}""";
        async Task<int> Measure(int pairs)
        {
            await Db.Context.Set<TaskItem>().ExecuteDeleteAsync();
            for (var i = 0; i < pairs; i++)
            {
                var hidden = i % 2 == 0;
                var parent = await SeedTask($"父{i}", createdBy: hidden ? "sales02" : "sales01", assignee: hidden ? "sales02" : "sales01",
                    status: TaskItemStatus.InProgress, due: Dec(1));
                await SeedChild(parent, $"子{i}", createdBy: "sales02", assignee: "sales01", start: N(17), due: N(20));
                await SeedTask($"獨立{i}", createdBy: "sales01", assignee: "sales01", due: N(21));
                await SeedTask($"未排程{i}", createdBy: "sales01", assignee: "sales01");
            }
            Db.Context.ChangeTracker.Clear();
            await Gantt("sales01", config, window: W(N(16), N(29)));   // 暖身：政策、權限快取不算
            var before = Db.Counter.Count;
            var g = await Gantt("sales01", config, window: W(N(16), N(29)));
            var used = Db.Counter.Count - before;
            g.Rows.Should().HaveCount(pairs * 2);
            g.Parents.Should().HaveCount(pairs);
            g.Parents.Count(p => !p.Visible).Should().Be((pairs + 1) / 2);
            g.Spans.Should().HaveCount(pairs);
            return used;
        }

        var small = await Measure(25);    // 50 列
        var large = await Measure(200);   // 400 列

        large.Should().Be(small, "列、父任務、跨度、權限、留言數、進度都批次查");
    }

    [Fact]
    public async Task G45_Overdue_OnlyOpen()
    {
        var open = await SeedTask("未完成逾期", createdBy: "sales01", assignee: "sales01", due: N(17));
        var done = await SeedTask("已完成", createdBy: "sales01", assignee: "sales01", status: TaskItemStatus.Done, due: N(17));

        var g = await Gantt("sales01", window: W(N(16), N(29)));

        g.Rows.Single(r => r.Id == open.Id).Overdue.Should().BeTrue();
        g.Rows.Single(r => r.Id == done.Id).Overdue.Should().BeFalse();
    }

    [Fact]
    public async Task G46_Source_BoardIdOrHidden404_Both400_NoneDefault()
    {
        var board = await CreateView("sales01", "看板");
        var r = await Raw("sales01", viewId: board.Id);
        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("這個甘特圖已刪除或不再分享給你");
        (await Raw("sales01", viewId: 999_999)).Message.Should().Be("這個甘特圖已刪除或不再分享給你");

        var created = await SendAs("sales01", new CreateTaskViewRequest("gantt", "展覽", J(Config(scope: "visible"))));
        var gantt = (TaskViewDetail)created.Data!;
        (await Raw("sales02", viewId: gantt.Id)).Message.Should().Be("這個甘特圖已刪除或不再分享給你", "沒分享給他");
        (await Gantt("sales01", viewId: gantt.Id)).Config.GetProperty("scope").GetString().Should().Be("visible");

        var both = await Raw("sales01", Flat, gantt.Id);
        both.IsInvalid.Should().BeTrue();
        both.Errors["config"].Should().Equal("只能帶甘特圖 Id 或甘特圖設定其中一個");
        (await Raw("sales01", """{"version":1,"groupBy":"parent","scope":"assigned","scale":"week","filters":{"dueRange":{"kind":"thisWeek"}}}"""))
            .Errors["config"].Should().Equal("甘特圖用時間範圍，不用到期日區間");

        var def = await Gantt("sales01");
        def.Config.GetRawText().Should().Be(TaskGanttConfig.Default.Serialize());

        (await SendAs("sales01", new DeleteTaskViewRequest(gantt.Id))).IsSuccess.Should().BeTrue();
        (await Raw("sales01", viewId: gantt.Id)).Message.Should().Be("這個甘特圖已刪除或不再分享給你");
    }

    [Fact]
    public async Task G46_SharedGantt_EachViewerSeesOwnScope()
    {
        await SeedTask("業務一的", createdBy: "sales01", assignee: "sales01", due: N(20));
        await SeedTask("業務二的", createdBy: "sales02", assignee: "sales02", due: N(20));
        var created = await SendAs("manager01", new CreateTaskViewRequest("gantt", "大家的", J(Config(scope: "visible"))));
        var view = (TaskViewDetail)created.Data!;
        (await Share("manager01", view.Id, ("user", "sales01"))).IsSuccess.Should().BeTrue();

        (await Gantt("sales01", viewId: view.Id)).Rows.Select(r => r.Title).Should().Equal("業務一的");
        (await Gantt("manager01", viewId: view.Id)).Rows.Select(r => r.Title).Should().BeEquivalentTo(["業務一的", "業務二的"]);
    }

    [Fact]
    public async Task G33_ScheduleClearsBoth_ThenUnscheduled()
    {
        var task = await SeedTask("清空日期", createdBy: "sales01", assignee: "sales01", start: N(17), due: N(20));
        (await Gantt("sales01", Flat, window: W(N(16), N(29)))).Rows.Select(r => r.Id).Should().Equal(task.Id);

        var r = await SendAs("sales01", new ScheduleTaskRequest(task.Id, null, null, TaskVersion.Of(await Load(task.Id))));
        r.IsSuccess.Should().BeTrue(r.Message);

        var g = await Gantt("sales01", Flat, window: W(N(16), N(29)));
        g.Rows.Should().BeEmpty();
        g.Unscheduled.Select(u => u.Id).Should().Equal(task.Id);
    }

    private static TaskItem NewTask(string title, DateOnly? due, string user = "sales01")
    {
        var t = new TaskItem { Title = title, CreatedBy = user, AssigneeUserId = user, DueDate = due, CreatedTime = SeedTime };
        t.Participants.Add(new TaskParticipant { UserId = user, Reason = TaskParticipantReason.Creator });
        t.Participants.Add(new TaskParticipant { UserId = user, Reason = TaskParticipantReason.Assignee });
        return t;
    }

    private async Task AddAll(List<TaskItem> tasks)
    {
        Db.Context.AddRange(tasks);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
    }
}

/// <summary>G47 空系統：沒有任何應用程式、沒有部門 → 個人任務照常出現，部屬範圍 scopeAvailable=false。</summary>
[Trait("Category", "Postgres")]
public class TaskGanttEmptySystemTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    [Fact]
    public async Task G47_NoAppsNoDepartments_PersonalTasksStillListed()
    {
        var undated = await SeedTask("個人待辦", createdBy: "admin", assignee: "admin");

        var r = await Send(new GetTaskGanttRequest(null, null, null));
        r.IsSuccess.Should().BeTrue(r.Message);
        ((TaskGanttResponse)r.Data!).Unscheduled.Select(u => u.Id).Should().Equal(undated.Id);

        var sub = await Send(new GetTaskGanttRequest(null, J("""{"version":1,"groupBy":"parent","scope":"subordinates","scale":"week"}"""), null));
        sub.IsSuccess.Should().BeTrue(sub.Message);
        ((TaskGanttResponse)sub.Data!).ScopeAvailable.Should().BeFalse();
    }
}
