using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Mcp;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// AI 連線（MCP）回給使用者的訊息（mcp1a 計畫判斷 24）：文字在 <see cref="MessageText"/> 四語表，這裡只組鍵與參數。
/// 機器碼（<c>ServiceResult.Code</c>、401／403 本文的 <c>code</c>）也集中在這裡。
/// </summary>
public static class McpMessages
{
    /// <summary>帳號、站台與政策不符時的 403 機器碼（判斷 7、8）。</summary>
    public const string CodeDisabled = "mcpDisabled", CodeNotAllowed = "mcpNotAllowed", CodeDemoSite = "mcpDemoSite",
        CodeAccountBlocked = "mcpAccountBlocked", CodeTokenLimit = "mcpTokenLimit", CodePolicyInvalid = "mcpPolicyInvalid";

    /// <summary>建立權杖重新輸入密碼被登入降速擋下（429）。</summary>
    public const string CodePasswordThrottled = "mcpPasswordThrottled";

    /// <summary>連線層（<c>/mcp</c>）的機器碼。</summary>
    public const string CodeUnauthorized = "mcpUnauthorized", CodeTokenInQuery = "mcpTokenInQuery", CodeOriginDenied = "mcpOriginDenied",
        CodeHttpsRequired = "mcpHttpsRequired";

    /// <summary><c>/mcp</c> 認證失敗太多次被 IP 降速（429）；訊息沿用 <see cref="Unauthorized"/>。</summary>
    public const string CodeAuthThrottled = "mcpAuthThrottled";

    /// <summary>建立權杖的欄位錯誤鍵（400 的 <c>errors.*</c>）。</summary>
    public const string NameField = "name", ScopesField = "scopes", ExpiresOnField = "expiresOn", PasswordField = "password";

    /// <summary>403 機器碼對應的訊息。</summary>
    public static MessageRef Denial(string code) => code switch
    {
        CodeDisabled => MessageRef.Of(MessageText.McpDisabled),
        CodeNotAllowed => MessageRef.Of(MessageText.McpNotAllowed),
        CodeDemoSite => MessageRef.Of(MessageText.McpDemoSite),
        CodeAccountBlocked => MessageRef.Of(MessageText.McpAccountBlocked),
        CodePolicyInvalid => MessageRef.Of(MessageText.McpPolicyInvalid),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "不是 MCP 的 403 機器碼"),
    };

    public static MessageRef TokenNameInvalid => MessageRef.Of(MessageText.McpTokenNameInvalid, McpLimits.TokenNameMax);
    public static MessageRef TokenScopeInvalid => MessageRef.Of(MessageText.McpTokenScopeInvalid);

    public static MessageRef TokenExpiryInvalid(DateOnly earliest, DateOnly latest) => MessageRef.Of(MessageText.McpTokenExpiryInvalid,
        earliest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), latest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public static MessageRef TokenLimitReached(int max) => MessageRef.Of(MessageText.McpTokenLimitReached, max);
    public static MessageRef TokenNotFound => MessageRef.Of(MessageText.McpTokenNotFound);
    public static MessageRef PasswordWrong => MessageRef.Of(MessageText.McpPasswordWrong);
    /// <summary>降速沿用登入的訊息（判斷 16）。</summary>
    public static MessageRef PasswordThrottled => MessageRef.Of(MessageText.LoginFailed);

    public static MessageRef ScopeDenied => MessageRef.Of(MessageText.McpScopeDenied);
    public static MessageRef RateLimitedMinute(int max) => MessageRef.Of(MessageText.McpRateLimitedMinute, max);
    public static MessageRef RateLimitedDay(int max) => MessageRef.Of(MessageText.McpRateLimitedDay, max);
    public static MessageRef ConfirmationInvalid => MessageRef.Of(MessageText.McpConfirmationInvalid);
    public static MessageRef AuditUnavailable => MessageRef.Of(MessageText.McpAuditUnavailable);
    public static MessageRef ResultTooLarge => MessageRef.Of(MessageText.McpResultTooLarge, McpLimits.MaxResultChars);
    public static MessageRef UnknownTool => MessageRef.Of(MessageText.McpUnknownTool);
    public static MessageRef InvalidArgument(string name) => MessageRef.Of(MessageText.McpInvalidArgument, name);
    /// <summary>寫入工具的 <c>idempotencyKey</c> 沒帶或格式不符（mcp2a 判斷 5、13）。</summary>
    public static MessageRef IdempotencyKeyRequired => MessageRef.Of(MessageText.McpIdempotencyKeyRequired);
    public static MessageRef InternalError => MessageRef.Of(MessageText.McpInternalError);
    public static MessageRef Unauthorized => MessageRef.Of(MessageText.McpUnauthorized);
    public static MessageRef TokenInQuery => MessageRef.Of(MessageText.McpTokenInQuery);
    public static MessageRef OriginDenied => MessageRef.Of(MessageText.McpOriginDenied);
    public static MessageRef HttpsRequired => MessageRef.Of(MessageText.McpHttpsRequired);
}
