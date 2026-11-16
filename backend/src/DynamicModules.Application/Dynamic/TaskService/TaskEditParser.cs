using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TaskService;

/// <summary>
/// PATCH 主體解析：沒送的欄位（Undefined）不改，送 null 代表清除。status 用 camelCase 字串，dueDate 用 yyyy-MM-dd。
/// 錯誤鍵是小寫開頭的欄位名。負責人是否為啟用中的使用者由呼叫端檢查。
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

        TaskItemStatus? status = null;
        if (Get(patch, "status") is { } s)
        {
            if (s.ValueKind == JsonValueKind.String && TryParseStatus(s.GetString(), out var parsed)) status = parsed;
            else errors["status"] = [TaskMessages.BadStatus];
        }

        return errors.Count > 0
            ? (null, errors)
            : (new TaskEdit(title, setDescription, description, setAssignee, assignee, setDue, due, status), errors);
    }

    /// <summary>null 代表標題合法；傳入的標題應已去頭尾空白。</summary>
    public static string? TitleError(string? title) =>
        string.IsNullOrEmpty(title) ? TaskMessages.TitleRequired
        : title.Length > TitleMax ? TaskMessages.TitleTooLong
        : null;

    public static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

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
