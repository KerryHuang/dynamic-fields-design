using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Domain.Tasks;

/// <summary>
/// 看板設定（存進 Dynamic_TaskView.ConfigJson，camelCase，看板設計 §5.7、§6.1）。
/// 寫入用 <see cref="Parse"/>：未知屬性、型別錯誤、值不合法都回錯誤（擋打錯字）。讀取用 <see cref="Read"/>：未知屬性忽略、
/// 不合法的值退回預設（舊程式讀到新版本不壞）。新增選填欄位不升 <see cref="Version"/>，語意改變才升。
/// </summary>
/// <param name="GroupBy">status、assignee、dueBucket。</param>
/// <param name="Scope">assigned（負責人是我）、involved（我是參與者）、subordinates（部屬的任務）、visible（全部看得到的）。</param>
public sealed record TaskBoardConfig(
    int Version, string GroupBy, string Scope, TaskBoardFilters? Filters, TaskBoardColumns? Columns, TaskBoardSort? Sort)
{
    public const int CurrentVersion = 1;
    /// <summary>明列的負責人欄上限（技術限制：欄數決定查詢次數，可寫死清單）。</summary>
    public const int MaxExplicitAssignees = 50;
    /// <summary>關鍵字長度（格式）。</summary>
    public const int MaxKeyword = 100;

    public const string ByStatus = "status", ByAssignee = "assignee", ByDueBucket = "dueBucket";
    public const string ScopeAssigned = "assigned", ScopeInvolved = "involved", ScopeSubordinates = "subordinates", ScopeVisible = "visible";
    public const string SourcePersonal = "personal", SourceRecord = "record", SourceRule = "rule";
    /// <summary>負責人看板的「未指派」欄；其他人的欄鍵是 user:&lt;帳號&gt;。</summary>
    public const string Unassigned = "unassigned";
    public const string UserColumnPrefix = "user:";

    public static readonly IReadOnlyList<string> GroupBys = [ByStatus, ByAssignee, ByDueBucket];
    public static readonly IReadOnlyList<string> Scopes = [ScopeAssigned, ScopeInvolved, ScopeSubordinates, ScopeVisible];
    public static readonly IReadOnlyList<string> Sources = [SourcePersonal, SourceRecord, SourceRule];
    public static readonly IReadOnlyList<string> SortKeys = ["dueDate", "createdTime", "title"];
    /// <summary>狀態欄的預設順序。</summary>
    public static readonly IReadOnlyList<string> StatusColumns =
        Enum.GetValues<TaskItemStatus>().Select(StatusKey).ToList();

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>預設看板：依狀態、指派給我、隱藏「取消」欄、到期日遞增（§5.8）。不存在資料表。</summary>
    public static TaskBoardConfig Default { get; } = new(CurrentVersion, ByStatus, ScopeAssigned, null,
        new TaskBoardColumns(null, [StatusKey(TaskItemStatus.Cancelled)], null), new TaskBoardSort("dueDate", "asc"));

    public static string StatusKey(TaskItemStatus s) => JsonNamingPolicy.CamelCase.ConvertName(s.ToString());

    public static bool TryParseStatus(string? key, out TaskItemStatus status)
    {
        foreach (var s in Enum.GetValues<TaskItemStatus>())
            if (StatusKey(s) == key)
            {
                status = s;
                return true;
            }
        status = default;
        return false;
    }

    public string SortKey => Sort?.Key is { } k && SortKeys.Contains(k) ? k : "dueDate";
    public bool SortDescending => Sort?.Dir == "desc";
    public bool AutoAppendAssignees => Columns?.AutoAppendAssignees ?? true;

    /// <summary>狀態篩選；null 代表不篩。到期區間看板固定只取未完成，忽略設定裡的狀態（U5）。</summary>
    public IReadOnlyList<TaskItemStatus>? StatusFilter()
    {
        if (GroupBy == ByDueBucket) return [TaskItemStatus.Todo, TaskItemStatus.InProgress];
        if (Filters?.Statuses is not { Count: > 0 } keys) return null;
        return keys.Select(k => TryParseStatus(k, out var s) ? s : (TaskItemStatus?)null).OfType<TaskItemStatus>().Distinct().ToList();
    }

    /// <summary>這種分欄依據的固定欄鍵（負責人看板只有「未指派」）。</summary>
    public static IReadOnlyList<string> FixedColumnsOf(string groupBy) => groupBy switch
    {
        ByStatus => StatusColumns,
        ByDueBucket => TaskDueBuckets.Keys,
        _ => [Unassigned],
    };

    /// <summary>狀態、到期區間看板要顯示的欄：先照 columns.order，其餘照預設順序補在後面，再拿掉隱藏的。</summary>
    public IReadOnlyList<string> VisibleFixedColumns()
    {
        var all = FixedColumnsOf(GroupBy);
        var order = (Columns?.Order ?? []).Where(all.Contains).Distinct().ToList();
        var hidden = (Columns?.Hidden ?? []).ToHashSet();
        return order.Concat(all.Where(k => !order.Contains(k))).Where(k => !hidden.Contains(k)).ToList();
    }

    public bool Hides(string column) => Columns?.Hidden?.Contains(column) == true;

    public string Serialize() => JsonSerializer.Serialize(this, Lenient);

    /// <summary>寫入前的解析與驗證；有錯時 Config 是 null。</summary>
    public static (TaskBoardConfig? Config, List<string> Errors) Parse(string json)
    {
        TaskBoardConfig? config;
        try { config = JsonSerializer.Deserialize<TaskBoardConfig>(json, Strict); }
        catch (JsonException e) { return (null, [$"看板設定有不認得的屬性或格式不正確（{e.Path}）"]); }
        if (config is null) return (null, ["看板設定必填"]);
        var errors = Validate(config);
        return errors.Count > 0 ? (null, errors) : (Normalize(config), errors);
    }

    /// <summary>讀取存過的設定：忽略未知屬性，不合法的值退回預設，解析不了就是預設看板。</summary>
    public static TaskBoardConfig Read(string json)
    {
        TaskBoardConfig? config;
        try { config = JsonSerializer.Deserialize<TaskBoardConfig>(json, Lenient); }
        catch (JsonException) { return Default; }
        if (config is null) return Default;
        var groupBy = GroupBys.Contains(config.GroupBy) ? config.GroupBy : Default.GroupBy;
        var scope = Scopes.Contains(config.Scope) ? config.Scope : Default.Scope;
        var f = config.Filters;
        var filters = f is null ? null : f with
        {
            Statuses = f.Statuses?.Where(s => TryParseStatus(s, out _)).ToList(),
            Sources = f.Sources?.Where(Sources.Contains).ToList(),
            DueRange = f.DueRange is { } r && RangeError(r) is null ? r : null,
            Keyword = f.Keyword is { } k && k.Trim().Length is > 0 and <= MaxKeyword ? k.Trim() : null,
        };
        var c = config.Columns;
        var fixedKeys = FixedColumnsOf(groupBy);
        var columns = c is null ? null : c with
        {
            Order = c.Order?.Where(fixedKeys.Contains).ToList(),
            Hidden = c.Hidden?.Where(fixedKeys.Contains).ToList(),
            Assignees = c.Assignees?.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().Take(MaxExplicitAssignees).ToList(),
        };
        return Normalize(config with { Version = CurrentVersion, GroupBy = groupBy, Scope = scope, Filters = filters, Columns = columns });
    }

    private static TaskBoardConfig Normalize(TaskBoardConfig c) =>
        c with { Sort = new TaskBoardSort(c.SortKey, c.SortDescending ? "desc" : "asc") };

    private static List<string> Validate(TaskBoardConfig c)
    {
        var errors = new List<string>();
        if (c.Version != CurrentVersion) errors.Add($"看板設定版本必須是 {CurrentVersion}");
        if (c.GroupBy is null || !GroupBys.Contains(c.GroupBy)) errors.Add("分欄依據不正確");
        if (c.Scope is null || !Scopes.Contains(c.Scope)) errors.Add("範圍不正確");
        if (c.Sort is { } sort)
        {
            if (sort.Key is not null && !SortKeys.Contains(sort.Key)) errors.Add("排序欄位不正確");
            if (sort.Dir is not null && sort.Dir is not ("asc" or "desc")) errors.Add("排序方向不正確");
        }
        if (c.Filters is { } f)
        {
            if (f.Statuses?.Any(s => !TryParseStatus(s, out _)) == true) errors.Add("狀態篩選不正確");
            if (f.Sources?.Any(s => !Sources.Contains(s)) == true) errors.Add("來源篩選不正確");
            if (f.AssigneeUserIds?.Any(string.IsNullOrWhiteSpace) == true || f.CreatorUserIds?.Any(string.IsNullOrWhiteSpace) == true)
                errors.Add("人員篩選不能有空白");
            if (f.DueRange is { } r && RangeError(r) is { } re) errors.Add(re);
            if (f.Keyword is { } k && k.Trim().Length > MaxKeyword) errors.Add($"關鍵字最多 {MaxKeyword} 字");
        }
        if (c.Columns is { } cols)
        {
            if (cols.Assignees is { } a)
            {
                if (a.Any(string.IsNullOrWhiteSpace)) errors.Add("明列的負責人不能有空白");
                if (a.Distinct().Count() > MaxExplicitAssignees) errors.Add($"明列的負責人最多 {MaxExplicitAssignees} 人");
            }
            if (c.GroupBy is not null && GroupBys.Contains(c.GroupBy))
            {
                var keys = FixedColumnsOf(c.GroupBy);
                if ((cols.Order ?? []).Concat(cols.Hidden ?? []).Any(k => !keys.Contains(k))) errors.Add("欄順序或隱藏的欄不正確");
            }
        }
        return errors;
    }

    /// <summary>到期日區間沿用報表的相對區間（以今天解析）；null 代表合法。</summary>
    private static string? RangeError(ReportDateRange r)
    {
        if (r.N is { } n && (n < 1 || n > ReportDateRange.MaxN)) return $"到期日區間的 N 必須介於 1 到 {ReportDateRange.MaxN}";
        try { ReportDates.Resolve(r, new DateOnly(2026, 1, 1), DayOfWeek.Monday); }
        catch (ArgumentException) { return "到期日區間種類不正確"; }
        if (r.N is null && r.Kind is "lastDays" or "lastWeeks" or "lastMonths") return "到期日區間需要 N";
        return null;
    }
}

/// <param name="IncludeUnassigned">有列負責人時，true 代表另外含未指派；沒列負責人時，false 代表排除未指派。</param>
/// <param name="Sources">personal（個人任務）、record（記錄上、非規則）、rule（規則產生）。</param>
/// <param name="Keyword">標題包含（不分大小寫）。</param>
public sealed record TaskBoardFilters(
    List<string>? Statuses = null, List<string>? AssigneeUserIds = null, bool? IncludeUnassigned = null,
    List<string>? CreatorUserIds = null, List<string>? Sources = null, ReportDateRange? DueRange = null, string? Keyword = null);

/// <param name="Order">狀態、到期區間看板的欄順序（欄鍵）；沒列的照預設順序補在後面。</param>
/// <param name="Hidden">隱藏的欄鍵；負責人看板只能隱藏「未指派」。</param>
/// <param name="Assignees">負責人看板明列的人（依序，最多 50 人，停用者也照列）。</param>
/// <param name="AutoAppendAssignees">負責人看板是否自動附加篩選結果裡有任務的其他人。</param>
public sealed record TaskBoardColumns(
    List<string>? Order = null, List<string>? Hidden = null, List<string>? Assignees = null, bool AutoAppendAssignees = true);

/// <param name="Key">dueDate（空的排最後）、createdTime、title。</param>
/// <param name="Dir">asc、desc。</param>
public sealed record TaskBoardSort(string? Key = null, string? Dir = null);
