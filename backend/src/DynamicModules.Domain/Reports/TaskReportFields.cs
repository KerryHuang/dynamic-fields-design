using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using ModuleEntity = DynamicModules.Domain.Entities.Dynamic.Module;

namespace DynamicModules.Domain.Reports;

/// <summary>
/// 任務資料來源的欄位目錄（看板設計 §7.3）。欄位代碼是固定負數，不與動態欄位撞號（-16 層級、-17 開始日保留給 st1、gt1）；
/// <see cref="Build"/> 產生不入庫的虛擬模組，讓報表既有的驗證、條件正規化、標籤、排序、遮蔽不必分支。
/// 選項 Id 存進報表定義的條件值（opt_{id}），固定選項的 Id 不能改。
/// </summary>
public static class TaskReportFields
{
    public const long Title = -1, Status = -2, Assignee = -3, AssigneeDepartment = -4, Creator = -5, DueDate = -6,
        CreatedTime = -7, CompletedTime = -8, Source = -9, Module = -10, Rule = -11, Overdue = -12, Completion = -13,
        CompletionDays = -14, DelayDays = -15, EventKind = -18, EventDate = -19;

    public const long SourcePersonal = 1, SourceRecord = 2, SourceRule = 3;
    public const long CompletionOnTime = 1, CompletionLate = 2, CompletionNoDue = 3, CompletionNotDone = 4;
    public const long EventCreated = 1, EventCompleted = 2;

    public static long StatusOption(TaskItemStatus s) => (long)s + 1;

    private static readonly HashSet<long> TaskCodes = [.. Enumerable.Range(1, 15).Select(i => (long)-i)];
    private static readonly HashSet<long> EventCodes = [EventKind, EventDate, Status, Assignee, AssigneeDepartment, Creator, Source, Module, Rule];
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

    /// <summary>空值在分組、明細、下鑽、匯出顯示的字（B2）；條件「為空」照樣成立。</summary>
    public static IReadOnlyDictionary<long, I18nText> BlankLabels { get; } = new Dictionary<long, I18nText>
    {
        [Assignee] = T("未指派", "Unassigned", "ยังไม่มอบหมาย", "Chưa giao"),
        [AssigneeDepartment] = T("（無部門）", "(No department)", "(ไม่มีแผนก)", "(Không có phòng ban)"),
        [Creator] = T("系統", "System", "ระบบ", "Hệ thống"),
        [Module] = T("（無）", "(None)", "(ไม่มี)", "(Không có)"),
        [Rule] = T("（無）", "(None)", "(ไม่มี)", "(Không có)"),
    };

    private static readonly Dictionary<long, (FieldType Type, string Key, I18nText Label)> Definitions = new()
    {
        [Title] = (FieldType.Textarea, "title", T("標題", "Title", "ชื่องาน", "Tiêu đề")),
        [Status] = (FieldType.Enum, "status", T("狀態", "Status", "สถานะ", "Trạng thái")),
        [Assignee] = (FieldType.User, "assignee", T("負責人", "Assignee", "ผู้รับผิดชอบ", "Người phụ trách")),
        [AssigneeDepartment] = (FieldType.Enum, "assigneeDepartment", T("負責人部門", "Assignee department", "แผนกของผู้รับผิดชอบ", "Phòng ban người phụ trách")),
        [Creator] = (FieldType.User, "creator", T("建立者", "Created by", "ผู้สร้าง", "Người tạo")),
        [DueDate] = (FieldType.Date, "dueDate", T("到期日", "Due date", "วันครบกำหนด", "Ngày đến hạn")),
        [CreatedTime] = (FieldType.DateTime, "createdTime", T("建立時間", "Created at", "เวลาที่สร้าง", "Thời gian tạo")),
        [CompletedTime] = (FieldType.DateTime, "completedTime", T("完成時間", "Completed at", "เวลาที่เสร็จ", "Thời gian hoàn thành")),
        [Source] = (FieldType.Enum, "source", T("來源", "Source", "ที่มา", "Nguồn")),
        [Module] = (FieldType.Enum, "module", T("所屬模組", "Module", "โมดูล", "Mô-đun")),
        [Rule] = (FieldType.Enum, "rule", T("任務規則", "Task rule", "กฎงาน", "Quy tắc công việc")),
        [Overdue] = (FieldType.Boolean, "overdue", T("是否逾期", "Overdue", "เกินกำหนดหรือไม่", "Quá hạn")),
        [Completion] = (FieldType.Enum, "completion", T("完成狀況", "Completion", "สถานะการเสร็จ", "Tình trạng hoàn thành")),
        [CompletionDays] = (FieldType.Integer, "completionDays", T("完成天數", "Days to complete", "จำนวนวันที่ใช้", "Số ngày hoàn thành")),
        [DelayDays] = (FieldType.Integer, "delayDays", T("延遲天數", "Days late", "จำนวนวันที่ล่าช้า", "Số ngày trễ")),
        [EventKind] = (FieldType.Enum, "eventKind", T("事件種類", "Event type", "ประเภทเหตุการณ์", "Loại sự kiện")),
        [EventDate] = (FieldType.DateTime, "eventDate", T("事件日期", "Event date", "วันที่ของเหตุการณ์", "Ngày sự kiện")),
    };

    // 與前端 status.* 同字（K2），改時兩邊一起改
    private static readonly (long Id, I18nText Label)[] StatusOptions =
    [
        (StatusOption(TaskItemStatus.Todo), T("待辦", "To do", "รอดำเนินการ", "Cần làm")),
        (StatusOption(TaskItemStatus.InProgress), T("進行中", "In progress", "กำลังดำเนินการ", "Đang làm")),
        (StatusOption(TaskItemStatus.Done), T("已完成", "Done", "เสร็จแล้ว", "Hoàn thành")),
        (StatusOption(TaskItemStatus.Cancelled), T("已取消", "Cancelled", "ยกเลิกแล้ว", "Đã hủy")),
    ];

    private static readonly (long Id, I18nText Label)[] SourceOptions =
    [
        (SourcePersonal, T("個人任務", "Personal task", "งานส่วนตัว", "Công việc cá nhân")),
        (SourceRecord, T("記錄上建立", "Created on a record", "สร้างจากระเบียน", "Tạo trên bản ghi")),
        (SourceRule, T("規則產生", "Generated by a rule", "สร้างโดยกฎ", "Do quy tắc tạo")),
    ];

    private static readonly (long Id, I18nText Label)[] CompletionOptions =
    [
        (CompletionOnTime, T("準時完成", "Completed on time", "เสร็จตรงเวลา", "Hoàn thành đúng hạn")),
        (CompletionLate, T("逾期完成", "Completed late", "เสร็จล่าช้า", "Hoàn thành trễ hạn")),
        (CompletionNoDue, T("無到期日完成", "Completed, no due date", "เสร็จโดยไม่มีวันครบกำหนด", "Hoàn thành, không có hạn")),
        (CompletionNotDone, T("未完成", "Not completed", "ยังไม่เสร็จ", "Chưa hoàn thành")),
    ];

    private static readonly (long Id, I18nText Label)[] EventOptions =
    [
        (EventCreated, T("建立", "Created", "สร้าง", "Tạo")),
        (EventCompleted, T("完成", "Completed", "เสร็จ", "Hoàn thành")),
    ];

    /// <summary>資料來源名（目錄端點、「每列代表什麼」）。</summary>
    public static IReadOnlyDictionary<string, I18nText> DatasetNames { get; } = new Dictionary<string, I18nText>
    {
        [ReportDatasets.Tasks] = T("任務", "Task", "งาน", "Công việc"),
        [ReportDatasets.TaskEvents] = T("任務事件（建立或完成）", "Task event (created or completed)", "เหตุการณ์ของงาน (สร้างหรือเสร็จ)", "Sự kiện công việc (tạo hoặc hoàn thành)"),
    };

    /// <summary>
    /// 不入庫的模組：Id 0、Key ""、NameI18n 是資料來源名；欄位是 <see cref="Selectable"/> 與 <see cref="DrillColumns"/> 的聯集。
    /// 部門、模組、規則的選項依傳入的實體（呼叫端決定觀看者看得到哪些），選項 Id 就是實體 Id。
    /// </summary>
    public static ModuleEntity Build(string dataset, IReadOnlyList<Department> departments, IReadOnlyList<ModuleEntity> modules, IReadOnlyList<TaskRule> rules)
    {
        var codes = Selectable(dataset).Union(DrillColumns(dataset)).OrderByDescending(c => c).ToList();
        var fields = codes.Select((code, i) =>
        {
            var (type, key, label) = Definitions[code];
            return new Field
            {
                Id = code, Key = key, LabelI18n = label, Type = type, Sequence = i, Status = FieldStatus.Active,
                Options = code switch
                {
                    Status => Fixed(StatusOptions),
                    Source => Fixed(SourceOptions),
                    Completion => Fixed(CompletionOptions),
                    EventKind => Fixed(EventOptions),
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
            Id = 0, Key = "", NameI18n = DatasetNames.GetValueOrDefault(dataset) ?? new I18nText(), Status = ModuleStatus.Active,
            Blocks = [new Block { Fields = fields }],
        };
    }

    // FieldOption.Active 預設 false，固定選項要明設
    private static List<FieldOption> Fixed((long Id, I18nText Label)[] options) =>
        [.. options.Select((o, n) => new FieldOption { Id = o.Id, LabelI18n = o.Label, Sequence = n, Active = true })];

    private static I18nText T(string zh, string en, string th, string vi) =>
        new([KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en), KeyValuePair.Create("th", th), KeyValuePair.Create("vi", vi)]);
}
