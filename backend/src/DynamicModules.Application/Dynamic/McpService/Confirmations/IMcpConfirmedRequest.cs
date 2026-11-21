using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.McpService.Tools;

namespace DynamicModules.Application.Dynamic.McpService.Confirmations;

/// <summary>
/// 高風險工具執行時送的請求（mcp1a 計畫判斷 18）：必須同時是 <see cref="ITransactionalRequest"/>、不得同時是
/// <c>IAuditedRequest</c>（架構測試與 <see cref="McpConfirmationBehavior{TReq,TRes}"/> 入口都鎖住）。
/// handler 讀到目標後要呼叫 <see cref="IMcpConfirmationScope.ConsumeAsync"/>，沒消耗就回成功會整筆回滾。
/// </summary>
public interface IMcpConfirmedRequest
{
    /// <summary>MCP 通道一定要帶；畫面通道一定是 null。</summary>
    McpConfirmationTicket? Confirmation { get; }
}

/// <summary>
/// 執行器交給高風險工具的確認資訊：確認碼明文（只在記憶體）、權杖、工具名稱與參數雜湊（參數不含確認碼）。
/// <paramref name="AuditCode"/> 與 <paramref name="AuditSummary"/> 是確認成功那一列稽核的動作代碼與摘要（由 behavior 在交易內寫）。
/// </summary>
public sealed record McpConfirmationTicket(string Code, long TokenId, string ToolName, string ArgumentsHash, string AuditCode, string? AuditSummary)
{
    // 明文不進 log 與例外訊息
    public override string ToString() => $"McpConfirmationTicket {{ TokenId = {TokenId}, ToolName = {ToolName} }}";
}

/// <summary>MCP 通道中確認請求出現在另一個交易型請求之內（計畫判斷 18 巢狀）：一律丟出，由最外層交易回滾。</summary>
public sealed class McpConfirmationNestingException()
    : InvalidOperationException("MCP 通道的確認請求不能在另一個交易型請求之內執行");

public static class McpConfirmationResults
{
    /// <summary>確認碼不符（不說明哪一項）：執行器對照成 <c>confirmation_invalid</c>。</summary>
    public static ServiceResult Invalid() =>
        ServiceResult.Invalid(McpConfirmationArgs.Code, McpMessages.ConfirmationInvalid, McpErrorCodes.ConfirmationInvalid);
}

/// <summary>高風險工具的參數名稱。</summary>
public static class McpConfirmationArgs
{
    public const string Code = "confirmationCode";
}
