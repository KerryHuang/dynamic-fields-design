using System.Text;
using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>測試用的復原憑證：Base64 的 JSON（不加密；真正的 Data Protection 實作在 WebAPI 有自己的測試）。壞掉的字串讀不出來就回 null。</summary>
public sealed class TestTaskUndoTokens : ITaskUndoTokens
{
    public string Issue(TaskUndoTicket ticket) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(ticket));

    public TaskUndoTicket? Read(string token)
    {
        try { return JsonSerializer.Deserialize<TaskUndoTicket>(Encoding.UTF8.GetString(Convert.FromBase64String(token))); }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException) { return null; }
    }
}
