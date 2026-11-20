using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using ModuleEntity = DynamicModules.Domain.Entities.Dynamic.Module;

namespace DynamicModules.Domain.Reports;

/// <summary>
/// 任務資料來源的欄位目錄（看板設計 §7.3）。欄位代碼是固定負數，不與動態欄位撞號（-17 開始日保留給 gt1）；
/// <see cref="Build"/> 產生不入庫的虛擬模組，讓報表既有的驗證、條件正規化、標籤、排序、遮蔽不必分支。
/// 選項 Id 存進報表定義的條件值（opt_{id}），固定選項的 Id 不能改。
/// </summary>
public static class TaskReportFields
{
    public const long Title = -1, Status = -2, Assignee = -3, AssigneeDepartment = -4, Creator = -5, DueDate = -6,
        CreatedTime = -7, CompletedTime = -8, Source = -9, Module = -10, Rule = -11, Overdue = -12, Completion = -13,
        CompletionDays = -14, DelayDays = -15, Level = -16, EventKind = -18, EventDate = -19;

    public const long SourcePersonal = 1, SourceRecord = 2, SourceRule = 3;
    public const long CompletionOnTime = 1, CompletionLate = 2, CompletionNoDue = 3, CompletionNotDone = 4;
    public const long LevelStandalone = 1, LevelParent = 2, LevelChild = 3;
    public const long EventCreated = 1, EventCompleted = 2;

    public static long StatusOption(TaskItemStatus s) => (long)s + 1;

    /// <summary>
    /// 文字鍵（語系包 i4c 計畫判斷 9）：值就是後端訊息 catalog 的鍵，文字在 Application 的 <c>ReportText</c>。
    /// 前綴 <c>report.</c> 是語系包 <c>server</c> 段的命名空間；鍵只在這裡定義一次，Application 的表直接引用。
    /// </summary>
    public static class TextKeys
    {
        public const string
            FieldTitle = "report.TaskFieldTitle", FieldStatus = "report.TaskFieldStatus", FieldAssignee = "report.TaskFieldAssignee",
            FieldAssigneeDepartment = "report.TaskFieldAssigneeDepartment", FieldCreator = "report.TaskFieldCreator",
            FieldDueDate = "report.TaskFieldDueDate", FieldCreatedTime = "report.TaskFieldCreatedTime",
            FieldCompletedTime = "report.TaskFieldCompletedTime", FieldSource = "report.TaskFieldSource",
            FieldModule = "report.TaskFieldModule", FieldRule = "report.TaskFieldRule", FieldOverdue = "report.TaskFieldOverdue",
            FieldCompletion = "report.TaskFieldCompletion", FieldCompletionDays = "report.TaskFieldCompletionDays",
            FieldDelayDays = "report.TaskFieldDelayDays", FieldLevel = "report.TaskFieldLevel",
            FieldEventKind = "report.TaskFieldEventKind", FieldEventDate = "report.TaskFieldEventDate",
            StatusTodo = "report.TaskStatusTodo", StatusInProgress = "report.TaskStatusInProgress",
            StatusDone = "report.TaskStatusDone", StatusCancelled = "report.TaskStatusCancelled",
            SourcePersonal = "report.TaskSourcePersonal", SourceRecord = "report.TaskSourceRecord",
            SourceRule = "report.TaskSourceRule", CompletionOnTime = "report.TaskCompletionOnTime",
            CompletionLate = "report.TaskCompletionLate", CompletionNoDue = "report.TaskCompletionNoDue",
            CompletionNotDone = "report.TaskCompletionNotDone", LevelStandalone = "report.TaskLevelStandalone",
            LevelParent = "report.TaskLevelParent", LevelChild = "report.TaskLevelChild", EventCreated = "report.TaskEventCreated",
            EventCompleted = "report.TaskEventCompleted", BlankAssignee = "report.TaskBlankAssignee",
            BlankAssigneeDepartment = "report.TaskBlankAssigneeDepartment", BlankCreator = "report.TaskBlankCreator",
            BlankModule = "report.TaskBlankModule", BlankRule = "report.TaskBlankRule", DatasetTasks = "report.TaskDatasetTasks",
            DatasetTaskEvents = "report.TaskDatasetTaskEvents";
    }


    private static readonly HashSet<long> TaskCodes = [.. Enumerable.Range(1, 16).Select(i => (long)-i)];
    private static readonly HashSet<long> EventCodes = [EventKind, EventDate, Level, Status, Assignee, AssigneeDepartment, Creator, Source, Module, Rule];
    private static readonly long[] TaskDrill = [Title, Status, Assignee, DueDate, CompletedTime, Module];
    private static readonly long[] EventDrill = [EventKind, EventDate, Title, Status, Assignee, Module];

    /// <summary>報表定義可以引用的欄位；不認得的資料來源是空集合。</summary>
    public static IReadOnlySet<long> Selectable(string dataset) => dataset switch
    {
        ReportDatasets.Tasks => TaskCodes,
        ReportDatasets.TaskEvents => EventCodes,
        _ => new HashSet<long>(),
    };

    /// <summary>觀看者看不到那筆記錄時格子是 restricted 的欄位（看板設計 §7.5）。</summary>
    public static IReadOnlySet<long> Restrictable { get; } = new HashSet<long> { Module, Rule };

    /// <summary>下鑽固定欄（使用者裁決 B3：不給記錄標題）。</summary>
    public static IReadOnlyList<long> DrillColumns(string dataset) => dataset == ReportDatasets.TaskEvents ? EventDrill : TaskDrill;

    /// <summary>空值在分組、明細、下鑽、匯出顯示的字（B2）的文字鍵；條件「為空」照樣成立。</summary>
    public static IReadOnlyDictionary<long, string> BlankLabelKeys { get; } = new Dictionary<long, string>
    {
        [Assignee] = TextKeys.BlankAssignee,
        [AssigneeDepartment] = TextKeys.BlankAssigneeDepartment,
        [Creator] = TextKeys.BlankCreator,
        [Module] = TextKeys.BlankModule,
        [Rule] = TextKeys.BlankRule,
    };

    private static readonly Dictionary<long, (FieldType Type, string Key, string Label)> Definitions = new()
    {
        [Title] = (FieldType.Textarea, "title", TextKeys.FieldTitle),
        [Status] = (FieldType.Enum, "status", TextKeys.FieldStatus),
        [Assignee] = (FieldType.User, "assignee", TextKeys.FieldAssignee),
        [AssigneeDepartment] = (FieldType.Enum, "assigneeDepartment", TextKeys.FieldAssigneeDepartment),
        [Creator] = (FieldType.User, "creator", TextKeys.FieldCreator),
        [DueDate] = (FieldType.Date, "dueDate", TextKeys.FieldDueDate),
        [CreatedTime] = (FieldType.DateTime, "createdTime", TextKeys.FieldCreatedTime),
        [CompletedTime] = (FieldType.DateTime, "completedTime", TextKeys.FieldCompletedTime),
        [Source] = (FieldType.Enum, "source", TextKeys.FieldSource),
        [Module] = (FieldType.Enum, "module", TextKeys.FieldModule),
        [Rule] = (FieldType.Enum, "rule", TextKeys.FieldRule),
        [Overdue] = (FieldType.Boolean, "overdue", TextKeys.FieldOverdue),
        [Completion] = (FieldType.Enum, "completion", TextKeys.FieldCompletion),
        [CompletionDays] = (FieldType.Integer, "completionDays", TextKeys.FieldCompletionDays),
        [DelayDays] = (FieldType.Integer, "delayDays", TextKeys.FieldDelayDays),
        [Level] = (FieldType.Enum, "level", TextKeys.FieldLevel),
        [EventKind] = (FieldType.Enum, "eventKind", TextKeys.FieldEventKind),
        [EventDate] = (FieldType.DateTime, "eventDate", TextKeys.FieldEventDate),
    };

    private static readonly (long Id, string Label)[] StatusOptions =
    [
        (StatusOption(TaskItemStatus.Todo), TextKeys.StatusTodo),
        (StatusOption(TaskItemStatus.InProgress), TextKeys.StatusInProgress),
        (StatusOption(TaskItemStatus.Done), TextKeys.StatusDone),
        (StatusOption(TaskItemStatus.Cancelled), TextKeys.StatusCancelled),
    ];

    private static readonly (long Id, string Label)[] SourceOptions =
    [
        (SourcePersonal, TextKeys.SourcePersonal),
        (SourceRecord, TextKeys.SourceRecord),
        (SourceRule, TextKeys.SourceRule),
    ];

    private static readonly (long Id, string Label)[] CompletionOptions =
    [
        (CompletionOnTime, TextKeys.CompletionOnTime),
        (CompletionLate, TextKeys.CompletionLate),
        (CompletionNoDue, TextKeys.CompletionNoDue),
        (CompletionNotDone, TextKeys.CompletionNotDone),
    ];

    private static readonly (long Id, string Label)[] LevelOptions =
    [
        (LevelStandalone, TextKeys.LevelStandalone),
        (LevelParent, TextKeys.LevelParent),
        (LevelChild, TextKeys.LevelChild),
    ];

    private static readonly (long Id, string Label)[] EventOptions =
    [
        (EventCreated, TextKeys.EventCreated),
        (EventCompleted, TextKeys.EventCompleted),
    ];

    /// <summary>資料來源名（目錄端點、「每列代表什麼」）的文字鍵。</summary>
    public static IReadOnlyDictionary<string, string> DatasetNameKeys { get; } = new Dictionary<string, string>
    {
        [ReportDatasets.Tasks] = TextKeys.DatasetTasks,
        [ReportDatasets.TaskEvents] = TextKeys.DatasetTaskEvents,
    };

    /// <summary>全部文字鍵（欄位名、固定選項、空值字、資料來源名）；測試用來確認每個鍵都在 catalog。</summary>
    public static IReadOnlyList<string> AllTextKeys =>
    [
        .. Definitions.Values.Select(d => d.Label),
        .. new[] { StatusOptions, SourceOptions, CompletionOptions, LevelOptions, EventOptions }.SelectMany(o => o).Select(o => o.Label),
        .. BlankLabelKeys.Values, .. DatasetNameKeys.Values,
    ];

    /// <summary>
    /// 不入庫的模組：Id 0、Key ""、NameI18n 是資料來源名；欄位是 <see cref="Selectable"/> 與 <see cref="DrillColumns"/> 的聯集。
    /// 部門、模組、規則的選項依傳入的實體（呼叫端決定觀看者看得到哪些），選項 Id 就是實體 Id。
    /// 欄位名、固定選項、模組名稱都由 <paramref name="text"/> 依文字鍵（<see cref="TextKeys"/>）產生，Domain 不放文字。
    /// </summary>
    public static ModuleEntity Build(string dataset, IReadOnlyList<Department> departments, IReadOnlyList<ModuleEntity> modules, IReadOnlyList<TaskRule> rules,
        Func<string, I18nText> text)
    {
        var codes = Selectable(dataset).Union(DrillColumns(dataset)).OrderByDescending(c => c).ToList();
        var fields = codes.Select((code, i) =>
        {
            var (type, key, label) = Definitions[code];
            return new Field
            {
                Id = code, Key = key, LabelI18n = text(label), Type = type, Sequence = i, Status = FieldStatus.Active,
                Options = code switch
                {
                    Status => Fixed(StatusOptions, text),
                    Source => Fixed(SourceOptions, text),
                    Completion => Fixed(CompletionOptions, text),
                    Level => Fixed(LevelOptions, text),
                    EventKind => Fixed(EventOptions, text),
                    AssigneeDepartment => [.. departments.OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
                        .Select((d, n) => new FieldOption { Id = d.Id, LabelI18n = d.NameI18n, Sequence = n, Active = d.Active })],
                    Module => [.. modules.Select((m, n) => new FieldOption { Id = m.Id, LabelI18n = m.NameI18n, Sequence = n, Active = true })],
                    Rule => [.. rules.Select((r, n) => new FieldOption { Id = r.Id, LabelI18n = r.NameI18n, Sequence = n, Active = true })],
                    _ => [],
                },
            };
        }).ToList();
        return new ModuleEntity
        {
            Id = 0, Key = "", NameI18n = DatasetNameKeys.TryGetValue(dataset, out var name) ? text(name) : new I18nText(), Status = ModuleStatus.Active,
            Blocks = [new Block { Fields = fields }],
        };
    }

    // FieldOption.Active 預設 false，固定選項要明設
    private static List<FieldOption> Fixed((long Id, string Label)[] options, Func<string, I18nText> text) =>
        [.. options.Select((o, n) => new FieldOption { Id = o.Id, LabelI18n = text(o.Label), Sequence = n, Active = true })];
}
