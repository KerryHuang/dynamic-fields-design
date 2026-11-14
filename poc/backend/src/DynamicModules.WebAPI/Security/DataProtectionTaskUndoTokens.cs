using System.Security.Cryptography;
using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;
using Microsoft.AspNetCore.DataProtection;

namespace DynamicModules.WebAPI.Security;

/// <summary>以 Data Protection 加密、簽章的復原憑證（purpose task-undo）；金鑰與登入 cookie 同一套，重開站台後仍可解。</summary>
public sealed class DataProtectionTaskUndoTokens(IDataProtectionProvider provider) : ITaskUndoTokens
{
    private readonly IDataProtector _protector = provider.CreateProtector("task-undo");

    public string Issue(TaskUndoTicket ticket) =>
        _protector.Protect(JsonSerializer.Serialize(ticket));

    public TaskUndoTicket? Read(string token)
    {
        try
        {
            var plain = _protector.Unprotect(token);
            return JsonSerializer.Deserialize<TaskUndoTicket>(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }
}
