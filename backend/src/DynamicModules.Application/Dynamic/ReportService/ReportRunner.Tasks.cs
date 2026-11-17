using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using F = DynamicModules.Domain.Reports.TaskReportFields;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>
/// 任務資料來源（看板設計 §7）：欄位是虛擬模組的負數代碼，列由 <see cref="TaskReportSource"/> 依觀看者的任務可見範圍載入。
/// 只走記憶體路徑（§7.7，Reports:Engine=SqlOnly 也一樣），結果組裝與模組來源共用。
/// </summary>
public partial class ReportRunner
{
    private const string DatasetMemoryOnly = "datasetMemoryOnly";

    private async Task<SourceLoad> LoadTasksAsync(ReportDefinition d, ReportRunOptions o, DateOnly basis, bool frozen, string locale, CancellationToken ct)
    {
        SourceLoad Fail(string state, string message) => new(FailResult(state, message, basis, frozen), null);
        var dataset = d.Dataset!;
        // 存檔驗證擋掉的子模組出現在這裡代表定義被直接改壞；不擋的話尾段會用 d.Child 算去重，數字沒有定義（審查 G2）
        if (d.Child is not null) return Fail(FieldMissingState, ReportText.Get(locale, ReportText.FieldGone));
        var side = new Side(await taskReports.CatalogAsync(dataset, ct), TaskReportSource.Permissions);

        var selectable = F.Selectable(dataset);
        var bound = new Dictionary<string, Bound>();
        foreach (var r in AllRefs(d))
        {
            var key = ReportCalculator.KeyOf(r);
            if (bound.ContainsKey(key)) continue;
            // 存檔驗證擋掉的形狀（子模組、帶出、不可選的欄位）出現在這裡代表定義被直接改壞，fail closed
            if (r.Source != "main" || r.ViaFieldId is not null || !selectable.Contains(r.FieldId))
                return Fail(FieldMissingState, ReportText.Get(locale, ReportText.FieldGone));
            var (b, problem) = Bind(r, key, side, null, new Dictionary<string, Side>(), locale);
            if (b is null) return Fail(FieldMissingState, problem!);
            bound[key] = b with { Restrictable = F.Restrictable.Contains(r.FieldId) };
        }
        // 下鑽的固定欄（B3）另外綁；taskEvents 的固定欄含標題，不在可選欄位裡，所以不走上面的可選檢查
        if (o.DrillGroupKeys is not null)
            foreach (var r in TaskDrillColumns(d))
            {
                var key = ReportCalculator.KeyOf(r);
                if (bound.ContainsKey(key)) continue;
                var (b, problem) = Bind(r, key, side, null, new Dictionary<string, Side>(), locale);
                if (b is null) return Fail(FieldMissingState, problem!);
                bound[key] = b with { Restrictable = F.Restrictable.Contains(r.FieldId) };
            }
        foreach (var b in bound.Values)
            if (UnusableMessage(b, locale) is { } message) return Fail(Restricted, message);
        if (FieldWithChangedType(d, bound) is { } changed)
            return Fail(FieldMissingState, ReportText.Get(locale, ReportText.TypeChanged, ColumnLabel(changed, locale)));

        var (_, resolved, applied) = await ResolveRangeAsync(d, o, basis, ct);
        // 多取一筆才分得出是否超過上限；資料庫端只用證明精確的條件縮小，載入後照舊重套全部條件（J7）。
        // MemoryOnly 是對照用的純記憶體路徑，不縮小（同模組來源，審查 G3）
        var narrowing = options.Engine == ReportEngineMode.MemoryOnly ? new TaskReportNarrowing(null, null, null, null) : Narrowing(d, resolved);
        var (items, overLimit) = await taskReports.LoadAsync(narrowing, options.MaxLoadRows + 1, ct);
        // 部門篩選在這道上限判斷之後才套用（記憶體過濾），所以選部門救不了載入量，訊息提示改縮小期間（kr2 審查）
        if (overLimit) return Fail(TooManyState, ReportText.Get(locale, ReportText.TooManyTasks));

        // 部門暫時篩選（§8.2）：只算負責人目前部門在所選範圍內的任務，未指派的排除；作用在列，總計、下鑽、匯出跟著列走
        var departmentScope = o.DepartmentIds is { Count: > 0 } selected
            ? await taskReports.ExpandDepartmentsAsync(selected, o.IncludeSubDepartments, ct) : null;
        if (departmentScope is not null) items = [.. items.Where(i => i.AssigneeDepartmentId is { } dept && departmentScope.Contains(dept))];

        var blanks = bound.Values.Where(b => F.BlankLabels.ContainsKey(b.Field.Id))
            .ToDictionary(b => b.Key, b => F.BlankLabels[b.Field.Id].Resolve(locale));
        var ctx = new RunContext(locale, await UserNamesAsync(bound.Values, ct), [], BucketsOf(d), blanks);
        // tasks 一個任務一列；taskEvents 每個任務一列「建立」，完成（且有完成時間，J15）再一列「完成」。
        // 事件列的 MainId 是 taskId*2（建立）、taskId*2+1（完成），每列不重複、去重照舊；TaskIdOf 對回任務 Id
        var events = dataset == ReportDatasets.TaskEvents;
        var rows = new List<ReportRow>();
        void Add(long mainId, TaskReportItem item, TaskEvent? ev)
        {
            var cells = new Dictionary<string, ReportCell>(bound.Count);
            foreach (var b in bound.Values) cells[b.Key] = ApplyBucket(b, TaskCell(b.Field.Id, item, side, basis, ev), ctx);
            if (!d.Filters.All(f => Matches(f, cells[ReportCalculator.KeyOf(f.Field)], basis))) return;
            if (resolved is { } window && !ReportDates.Contains(window, cells[ReportCalculator.KeyOf(d.PrimaryDateField!)])) return;
            rows.Add(new ReportRow(mainId, null, cells));
        }
        foreach (var item in items)
        {
            var t = item.Task;
            if (!events)
            {
                Add(t.Id, item, null);
                continue;
            }
            Add(t.Id * 2, item, new TaskEvent(F.EventCreated, t.CreatedTime));
            if (t.Status == TaskItemStatus.Done && t.CompletedTime is { } done) Add(t.Id * 2 + 1, item, new TaskEvent(F.EventCompleted, done));
        }
        if (rows.Count > options.MaxRows) return Fail(TooManyState, ReportText.Get(locale, ReportText.TooMany));
        Func<long, long> taskIdOf = events ? id => id / 2 : id => id;
        return new SourceLoad(null, new SourceRows(bound, rows, side, null, side.Module, ctx, applied, [DatasetMemoryOnly], taskIdOf));
    }

    /// <summary>任務資料來源下鑽的固定欄（使用者裁決 B3；taskEvents 另一組，Y7）。</summary>
    private static IEnumerable<ReportFieldRef> TaskDrillColumns(ReportDefinition d) =>
        F.DrillColumns(d.Dataset!).Select(code => new ReportFieldRef("main", code, null));

    /// <summary>taskEvents 的一個事件：種類（-18 的選項 Id）與發生時間（-19）。</summary>
    private sealed record TaskEvent(long Kind, DateTime At);

    /// <summary>
    /// 一個任務的一格。所屬模組（-10）與任務規則（-11）的判斷順序寫死：空 → restricted → missing → ok（審查 R3）。
    /// restricted 一定要先於「不在選項」：記錄看不到、但模組或規則屬於觀看者可讀的模組時，先查選項會變成 ok，帶出模組或規則名稱；
    /// 規則屬於不可讀的模組時則變成 missing，等於洩漏模組可讀性。個人任務（ModuleId 為 null）先判斷成空，不是 restricted。
    /// </summary>
    private static ReportCell TaskCell(long code, TaskReportItem item, Side side, DateOnly basis, TaskEvent? ev)
    {
        var t = item.Task;
        ReportCell Opt(long id) => new(Ok, null, $"opt_{id}", null, null);
        ReportCell Text(string? s) => string.IsNullOrEmpty(s) ? EmptyCell : new ReportCell(Ok, null, s, null, null);
        ReportCell Number(int? n) => n is { } v ? new ReportCell(Ok, v, null, null, null) : EmptyCell;
        ReportCell Record(long? id)
        {
            if (id is not { } value) return EmptyCell;
            if (!item.RecordAccessible) return RestrictedCell;
            if (side.Fields[code].Options.All(o => o.Id != value)) return MissingCell;
            return Opt(value);
        }
        var completed = t.Status == TaskItemStatus.Done ? t.CompletedTime : null;
        return code switch
        {
            F.Title => Text(t.Title),
            F.Status => Opt(F.StatusOption(t.Status)),
            F.Assignee => Text(t.AssigneeUserId),
            F.AssigneeDepartment => item.AssigneeDepartmentId is { } dept ? Opt(dept) : EmptyCell,
            F.Creator => Text(t.CreatedBy),
            F.DueDate => t.DueDate is { } due ? new ReportCell(Ok, null, null, due, null) : EmptyCell,
            F.CreatedTime => new TimedCell(t.CreatedTime),
            F.CompletedTime => completed is { } at ? new TimedCell(at) : EmptyCell,
            F.Source => Opt(TaskReportValues.SourceOf(t)),
            F.Module => Record(t.ModuleId),
            F.Rule => Record(t.RuleId),
            F.Overdue => new ReportCell(Ok, null, TaskReportValues.IsOverdue(t, basis) ? "true" : "false", null, null),
            F.Completion => TaskReportValues.CompletionOf(t) is { } c ? Opt(c) : EmptyCell,
            F.CompletionDays => Number(TaskReportValues.CompletionDays(t)),
            F.DelayDays => Number(TaskReportValues.DelayDays(t, basis)),
            F.EventKind => ev is null ? EmptyCell : Opt(ev.Kind),
            F.EventDate => ev is null ? EmptyCell : new TimedCell(ev.At),
            _ => EmptyCell,
        };
    }

    /// <summary>
    /// 資料庫端的縮小條件（J7）：狀態的 eq／in（多個條件取交集；不存在的選項值略過，in 全部無效時是空集合，J24），
    /// 與主要日期欄位（到期日、建立時間、完成時間；事件日期是建立或完成任一落在區間）的區間。兩者都只會多撈、不會少撈。
    /// </summary>
    private static TaskReportNarrowing Narrowing(ReportDefinition d, (DateOnly From, DateOnly To)? resolved)
    {
        HashSet<TaskItemStatus>? statuses = null;
        foreach (var f in d.Filters.Where(f => f.Field.FieldId == F.Status && f.Op is "eq" or "in"))
        {
            var values = f.Op == "eq" ? (f.Value is { } v ? [v] : []) : f.Value?.EnumerateArray().ToList() ?? [];
            var set = values.Select(StatusOf).Where(s => s is not null).Select(s => s!.Value).ToHashSet();
            statuses = statuses is null ? set : [.. statuses.Intersect(set)];
        }
        var dateField = resolved is null ? null : d.PrimaryDateField?.FieldId switch
        {
            F.DueDate => "due",
            F.CreatedTime => "created",
            F.CompletedTime => "completed",
            F.EventDate => "createdOrCompleted",
            _ => null,
        };
        return new TaskReportNarrowing(statuses, dateField, dateField is null ? null : resolved!.Value.From, dateField is null ? null : resolved!.Value.To);
    }

    private static TaskItemStatus? StatusOf(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.StartsWith("opt_", StringComparison.Ordinal)
        && long.TryParse(s.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && Enum.GetValues<TaskItemStatus>().Where(x => F.StatusOption(x) == id).ToList() is [var status]
            ? status : null;
}
