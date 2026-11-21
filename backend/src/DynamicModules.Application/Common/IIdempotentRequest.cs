namespace DynamicModules.Application.Common;

/// <summary>
/// 可以做請求層冪等的寫入請求（MCP mcp2a 計畫判斷 3、5）：所有 <see cref="IRecordWriteRequest"/>，加上任務與留言。
/// 帶 Idempotency-Key（畫面的標頭或 MCP 工具的 <c>idempotencyKey</c>）時由 <c>IdempotencyBehavior</c> 接手。
/// MCP 通道上，一次工具呼叫只能送一個最外層的這類請求，寫入稽核由 <c>McpWriteAuditBehavior</c> 在同一交易寫。
/// </summary>
public interface IIdempotentRequest : ITransactionalRequest
{
    /// <summary>
    /// 這一次要不要做請求層冪等（預設要）。請求內容不能可靠雜湊時覆寫成 false，例如帶附件的留言（<c>Stream</c>，寫死清單：技術限制）；
    /// 覆寫的屬性要標 <c>[JsonIgnore]</c>，不進請求雜湊。
    /// </summary>
    bool IdempotencyApplies => true;
}
