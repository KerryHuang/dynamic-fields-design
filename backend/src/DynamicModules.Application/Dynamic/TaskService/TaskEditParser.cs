using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// PATCH 主體解析：沒送的欄位（Undefined）不改，送 null 代表清除。status 用 camelCase 字串，dueDate、startDate 用 yyyy-MM-dd。
/// 錯誤鍵是小寫開頭的欄位名。負責人是否為啟用中的使用者由呼叫端檢查。cascade 只在父任務改成完成、還有未完成的子任務時有作用，
/// 其他情況由寫入端忽略（ST-T4）。
/// </summary>
public static class TaskEditParser
{
    public const int TitleMax = 200;
    public const int DescriptionMax = 4000;
    private const string BadType = "格式不正確";

    public static (TaskEdit? Edit, Dictionary<string, string[]> Errors) Parse(JsonElement patch)
    {
        var errors = new Dictionary<string, string[]>();
        if (patch.ValueKind != JsonValueKind.Object)
            return (null, new Dictionary<string, string[]> { ["body"] = ["請求內容必須是物件"] });

        string? title = null;
        if (Get(patch, "title") is { } t)
        {
            if (t.ValueKind == JsonValueKind.String) title = t.GetString()!.Trim();
            if (TitleError(t.ValueKind == JsonValueKind.String ? title : null) is { } e) errors["title"] = [e];
        }

        var setDescription = false;
        string? description = null;
        if (Get(patch, "description") is { } d)
        {
            setDescription = true;
            if (d.ValueKind == JsonValueKind.String) description = NullIfBlank(d.GetString());
            else if (d.ValueKind != JsonValueKind.Null) errors["description"] = [BadType];
            if (description is { Length: > DescriptionMax }) errors["description"] = [TaskMessages.DescriptionTooLong];
        }

        var setAssignee = false;
        string? assignee = null;
        if (Get(patch, "assigneeUserId") is { } a)
        {
            setAssignee = true;
            if (a.ValueKind == JsonValueKind.String) assignee = NullIfBlank(a.GetString());
            else if (a.ValueKind != JsonValueKind.Null) errors["assigneeUserId"] = [BadType];
        }

        var setDue = false;
        DateOnly? due = null;
        if (Get(patch, "dueDate") is { } dd)
        {
            setDue = true;
            if (dd.ValueKind == JsonValueKind.String && TryParseDate(dd.GetString(), out var parsed)) due = parsed;
            else if (dd.ValueKind != JsonValueKind.Null) errors["dueDate"] = [TaskMessages.BadDate];
        }

        var setStart = false;
        DateOnly? start = null;
        if (Get(patch, "startDate") is { } sd)
        {
            setStart = true;
            if (sd.ValueKind == JsonValueKind.String && TryParseDate(sd.GetString(), out var parsed)) start = parsed;
            else if (sd.ValueKind != JsonValueKind.Null) errors["startDate"] = [TaskMessages.BadDate];
        }

        TaskItemStatus? status = null;
        if (Get(patch, "status") is { } s)
        {
            if (s.ValueKind == JsonValueKind.String && TryParseStatus(s.GetString(), out var parsed)) status = parsed;
            else errors["status"] = [TaskMessages.BadStatus];
        }

        TaskCascade? cascade = null;
        if (Get(patch, "cascade") is { } c)
        {
            if (c.ValueKind == JsonValueKind.String && TryParseCascade(c.GetString(), out var parsed)) cascade = parsed;
            else if (c.ValueKind != JsonValueKind.Null) errors["cascade"] = [TaskMessages.BadCascade];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new TaskEdit(title, setDescription, description, setAssignee, assignee, setDue, due, status, cascade, setStart, start), errors);
    }

    /// <summary>合法的 status 字串（MCP 工具的參數列舉據此產生，與 <see cref="TryParseStatus"/> 同步）。</summary>
    public static IReadOnlyList<string> StatusValues { get; } = Enum.GetValues<TaskItemStatus>().Select(TaskWriter.StatusText).ToList();

    /// <summary>合法的 cascade 字串（同上，與 <see cref="TryParseCascade"/> 同步）。</summary>
    public static IReadOnlyList<string> CascadeValues { get; } = ["complete", "cancel"];

    /// <summary>連動方式（看板設計 §10.7.2）：只認 complete、cancel，其他（含空字串）都是錯；沒帶由呼叫端自己判斷。</summary>
    public static bool TryParseCascade(string? s, out TaskCascade cascade)
    {
        cascade = s == "cancel" ? TaskCascade.Cancel : TaskCascade.Complete;
        return s is not null && CascadeValues.Contains(s);
    }

    /// <summary>null 代表標題合法；傳入的標題應已去頭尾空白。</summary>
    public static string? TitleError(string? title) =>
        string.IsNullOrEmpty(title) ? TaskMessages.TitleRequired
        : title.Length > TitleMax ? TaskMessages.TitleTooLong
        : null;

    public static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>開始日與到期日都有值、開始日晚於到期日（看板設計 §11.5.2.2）。</summary>
    public static bool StartAfterDue(DateOnly? start, DateOnly? due) => start is { } s && due is { } d && s > d;

    public static bool TryParseDate(string? s, out DateOnly date) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static bool TryParseStatus(string? s, out TaskItemStatus status)
    {
        foreach (var v in Enum.GetValues<TaskItemStatus>())
            if (TaskWriter.StatusText(v) == s)
            {
                status = v;
                return true;
            }
        status = default;
        return false;
    }

    private static JsonElement? Get(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v : null;
}
