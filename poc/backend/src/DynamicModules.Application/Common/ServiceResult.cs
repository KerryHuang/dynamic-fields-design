using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

public sealed class ServiceResult
{
    private static readonly IReadOnlyDictionary<string, MessageRef[]> NoErrors = new Dictionary<string, MessageRef[]>();

    private ServiceResult() { }

    public bool IsSuccess { get; private init; }
    public bool IsCreated { get; private init; }
    public bool IsNotFound { get; private init; }
    public bool IsConflict { get; private init; }
    public bool IsForbidden { get; private init; }
    /// <summary>422：請求本身合法，但與先前同一把冪等金鑰的內容不同。</summary>
    public bool IsUnprocessable { get; private init; }
    public bool IsUnauthorized { get; private init; }
    private MessageRef? MessageSource { get; init; }
    /// <summary>繁中的訊息；回給使用者的文字由 WebAPI 依請求語系用 <see cref="MessageIn"/> 解析。</summary>
    public string? Message => MessageIn(I18nText.FallbackLocale);
    /// <summary>給前端判斷用的機器代碼（如 stale）；沒有時是 null。</summary>
    public string? Code { get; private init; }
    /// <summary>與 <see cref="Code"/> 搭配的參數（如 { count }）。</summary>
    public object? Params { get; private init; }
    public object? Data { get; private init; }
    private IReadOnlyDictionary<string, MessageRef[]> ErrorRefs { get; init; } = NoErrors;
    /// <summary>繁中的欄位錯誤；回給使用者的由 <see cref="ErrorsIn"/> 解析。</summary>
    public IReadOnlyDictionary<string, string[]> Errors => ErrorsIn(I18nText.FallbackLocale);

    /// <summary>驗證失敗（400）：沒有其他狀態旗標，但帶有欄位錯誤。</summary>
    public bool IsInvalid => ErrorRefs.Count > 0;

    public string? MessageIn(string? locale) => MessageSource?.Resolve(locale, translateLiteral: true);

    public IReadOnlyDictionary<string, string[]> ErrorsIn(string? locale) =>
        ErrorRefs.ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => m.Resolve(locale)).ToArray());

    public static ServiceResult Success(object? data = null) => new() { IsSuccess = true, Data = data };

    public static ServiceResult Created(object data) => new() { IsSuccess = true, IsCreated = true, Data = data };

    public static ServiceResult NotFound(MessageRef? message) => new() { IsNotFound = true, MessageSource = message };

    public static ServiceResult Conflict(MessageRef? message, string? code = null, object? @params = null) =>
        new() { IsConflict = true, MessageSource = message, Code = code, Params = @params };

    /// <summary>RowVersion 不符：資料已被其他人修改（code stale）。</summary>
    public static ServiceResult Stale() => Conflict(MessageRef.Of(MessageText.Stale), "stale");

    /// <summary>409 並帶額外資料；WebAPI 把 data 的屬性展開到 message 旁邊。</summary>
    public static ServiceResult ConflictWith(MessageRef? message, object data) =>
        new() { IsConflict = true, MessageSource = message, Data = data };

    public static ServiceResult Unprocessable(MessageRef? message) => new() { IsUnprocessable = true, MessageSource = message };

    public static ServiceResult Forbidden() => new() { IsForbidden = true, MessageSource = MessageRef.Of(MessageText.Forbidden) };

    /// <summary>403 並說明原因（例如「你以主管身分只能改負責人」）。</summary>
    public static ServiceResult Forbidden(MessageRef message) => new() { IsForbidden = true, MessageSource = message };

    public static ServiceResult Unauthorized(MessageRef? message) => new() { IsUnauthorized = true, MessageSource = message };

    public static ServiceResult Invalid(IDictionary<string, string[]> errors) =>
        new() { ErrorRefs = errors.ToDictionary(kv => kv.Key, kv => kv.Value.Select(MessageRef.Literal).ToArray()) };

    public static ServiceResult Invalid(IDictionary<string, MessageRef[]> errors) =>
        new() { ErrorRefs = new Dictionary<string, MessageRef[]>(errors) };

    public static ServiceResult Invalid(string key, MessageRef message) =>
        new() { ErrorRefs = new Dictionary<string, MessageRef[]> { [key] = [message] } };
}
