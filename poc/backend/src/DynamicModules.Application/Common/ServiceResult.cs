namespace DynamicModules.Application.Common;

public sealed class ServiceResult
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    private ServiceResult() { }

    public bool IsSuccess { get; private init; }
    public bool IsCreated { get; private init; }
    public bool IsNotFound { get; private init; }
    public bool IsConflict { get; private init; }
    public bool IsForbidden { get; private init; }
    public bool IsUnauthorized { get; private init; }
    public string? Message { get; private init; }
    public object? Data { get; private init; }
    public IReadOnlyDictionary<string, string[]> Errors { get; private init; } = NoErrors;

    public static ServiceResult Success(object? data = null) => new() { IsSuccess = true, Data = data };

    public static ServiceResult Created(object data) => new() { IsSuccess = true, IsCreated = true, Data = data };

    public static ServiceResult NotFound(string message) => new() { IsNotFound = true, Message = message };

    public static ServiceResult Conflict(string message) => new() { IsConflict = true, Message = message };

    /// <summary>409 並帶額外資料；WebAPI 把 data 的屬性展開到 message 旁邊。</summary>
    public static ServiceResult ConflictWith(string message, object data) =>
        new() { IsConflict = true, Message = message, Data = data };

    public static ServiceResult Forbidden() => new() { IsForbidden = true, Message = "沒有權限執行此操作" };

    public static ServiceResult Unauthorized(string message) => new() { IsUnauthorized = true, Message = message };

    public static ServiceResult Invalid(IDictionary<string, string[]> errors) =>
        new() { Errors = new Dictionary<string, string[]>(errors) };
}
