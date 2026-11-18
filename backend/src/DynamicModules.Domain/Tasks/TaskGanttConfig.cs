using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Tasks;

/// <summary>
/// 甘特圖設定（存進 Dynamic_TaskView.ConfigJson，Kind=gantt；看板設計 §11.5.2.7，GT-T8）。寫入嚴格、讀取寬鬆、升版規則同
/// <see cref="TaskBoardConfig"/>（§6.1）。篩選沿用 <see cref="TaskBoardFilters"/>，但不收 dueRange（甘特圖用時間範圍）。
/// 錯誤只帶 <see cref="TaskGanttConfigErrors"/> 的鍵與參數，文字在 Application 的 MessageText（四語）。
/// 收合狀態不存設定（GT-T19）；排序固定，沒有排序選項（GT-T14）。
/// </summary>
/// <param name="GroupBy">none、parent（父子）、assignee。</param>
/// <param name="Scope">同看板：assigned、involved、subordinates、visible。</param>
/// <param name="Scale">day、week、month。</param>
/// <param name="ShowSubtasks">是否列出子任務；null 等於 true。</param>
public sealed record TaskGanttConfig(int Version, string GroupBy, string Scope, TaskBoardFilters? Filters, string Scale, bool? ShowSubtasks = null)
{
    public const int CurrentVersion = 1;

    public const string ByNone = "none", ByParent = "parent", ByAssignee = "assignee";
    public const string ScaleDay = "day", ScaleWeek = "week", ScaleMonth = "month";

    public static readonly IReadOnlyList<string> GroupBys = [ByNone, ByParent, ByAssignee];
    public static readonly IReadOnlyList<string> Scales = [ScaleDay, ScaleWeek, ScaleMonth];

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

    /// <summary>預設甘特圖：依父子分組、指派給我、不顯示取消、以週為刻度（§11.5.2.7）。不存在資料表、不能分享。</summary>
    public static TaskGanttConfig Default { get; } = new(CurrentVersion, ByParent, TaskBoardConfig.ScopeAssigned,
        new TaskBoardFilters(Statuses: [.. new[] { TaskItemStatus.Todo, TaskItemStatus.InProgress, TaskItemStatus.Done }.Select(TaskBoardConfig.StatusKey)]),
        ScaleWeek);

    public string Serialize() => JsonSerializer.Serialize(this, Lenient);

    /// <summary>寫入前的解析與驗證；有錯時 Config 是 null。</summary>
    public static (TaskGanttConfig? Config, List<TaskGanttConfigError> Errors) Parse(string json)
    {
        TaskGanttConfig? config;
        try { config = JsonSerializer.Deserialize<TaskGanttConfig>(json, Strict); }
        catch (JsonException e) { return (null, [new(TaskGanttConfigErrors.Format, e.Path ?? "$")]); }
        if (config is null) return (null, [new(TaskGanttConfigErrors.Required)]);
        var errors = Validate(config);
        // dueRange 是 TaskBoardFilters 的已知屬性，帶 null 也會被模型接受；看原始 JSON 有沒有這個鍵（不論值、不分大小寫，同反序列化）
        if (HasDueRangeKey(json)) errors.Add(new(TaskGanttConfigErrors.NoDueRange));
        return errors.Count > 0 ? (null, errors) : (config, errors);
    }

    /// <summary>讀取存過的設定：忽略未知屬性，不合法的值退回預設，解析不了就是預設甘特圖。dueRange 一律丟掉。</summary>
    public static TaskGanttConfig Read(string json)
    {
        TaskGanttConfig? config;
        try { config = JsonSerializer.Deserialize<TaskGanttConfig>(json, Lenient); }
        catch (JsonException) { return Default; }
        if (config is null) return Default;
        var f = config.Filters;
        var filters = f is null ? null : f with
        {
            Statuses = f.Statuses?.Where(s => TaskBoardConfig.TryParseStatus(s, out _)).ToList(),
            Sources = f.Sources?.Where(TaskBoardConfig.Sources.Contains).ToList(),
            DueRange = null,
            Keyword = f.Keyword is { } k && k.Trim().Length is > 0 and <= TaskBoardConfig.MaxKeyword ? k.Trim() : null,
        };
        return config with
        {
            Version = CurrentVersion,
            GroupBy = GroupBys.Contains(config.GroupBy) ? config.GroupBy : Default.GroupBy,
            Scope = TaskBoardConfig.Scopes.Contains(config.Scope) ? config.Scope : Default.Scope,
            Scale = Scales.Contains(config.Scale) ? config.Scale : Default.Scale,
            Filters = filters,
        };
    }

    private static bool HasDueRangeKey(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var filters = doc.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("filters", StringComparison.OrdinalIgnoreCase)).Value;
        return filters.ValueKind == JsonValueKind.Object
            && filters.EnumerateObject().Any(p => p.Name.Equals("dueRange", StringComparison.OrdinalIgnoreCase));
    }

    private static List<TaskGanttConfigError> Validate(TaskGanttConfig c)
    {
        var errors = new List<TaskGanttConfigError>();
        if (c.Version != CurrentVersion) errors.Add(new(TaskGanttConfigErrors.Version, CurrentVersion));
        if (c.GroupBy is null || !GroupBys.Contains(c.GroupBy)) errors.Add(new(TaskGanttConfigErrors.GroupBy));
        if (c.Scope is null || !TaskBoardConfig.Scopes.Contains(c.Scope)) errors.Add(new(TaskGanttConfigErrors.Scope));
        if (c.Scale is null || !Scales.Contains(c.Scale)) errors.Add(new(TaskGanttConfigErrors.Scale));
        if (c.Filters is { } f)
        {
            if (f.Statuses?.Any(s => !TaskBoardConfig.TryParseStatus(s, out _)) == true) errors.Add(new(TaskGanttConfigErrors.Statuses));
            if (f.Sources?.Any(s => !TaskBoardConfig.Sources.Contains(s)) == true) errors.Add(new(TaskGanttConfigErrors.Sources));
            if (f.AssigneeUserIds?.Any(string.IsNullOrWhiteSpace) == true || f.CreatorUserIds?.Any(string.IsNullOrWhiteSpace) == true)
                errors.Add(new(TaskGanttConfigErrors.PeopleBlank));
            if (f.Keyword is { } k && k.Trim().Length > TaskBoardConfig.MaxKeyword) errors.Add(new(TaskGanttConfigErrors.KeywordTooLong, TaskBoardConfig.MaxKeyword));
        }
        return errors;
    }
}

/// <summary>甘特設定的驗證錯誤：MessageText 的鍵加 string.Format 參數。</summary>
public sealed record TaskGanttConfigError(string Key, params object[] Args);

/// <summary>甘特設定錯誤的訊息鍵；每個鍵都必須在 MessageText 有四語文字（MessageTextTests 會檢查）。</summary>
public static class TaskGanttConfigErrors
{
    public const string
        Format = "TaskGanttConfigFormat", Required = "TaskGanttConfigRequired", Version = "TaskGanttConfigVersion",
        GroupBy = "TaskGanttGroupByInvalid", Scope = "TaskGanttScopeInvalid", Scale = "TaskGanttScaleInvalid",
        Statuses = "TaskGanttStatusesInvalid", Sources = "TaskGanttSourcesInvalid", PeopleBlank = "TaskGanttPeopleBlank",
        NoDueRange = "TaskGanttNoDueRange", KeywordTooLong = "TaskGanttKeywordTooLong";
}
