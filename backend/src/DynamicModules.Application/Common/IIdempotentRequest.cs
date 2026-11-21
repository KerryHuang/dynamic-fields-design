namespace DynamicModules.Application.Common;

/// <summary>
/// 可以做請求層冪等的寫入請求（MCP mcp2a 計畫判斷 3、5）：所有 <see cref="IRecordWriteRequest"/>，加上任務與留言。
/// MCP 通道上，一次工具呼叫只能送一個最外層的這類請求，寫入稽核由 <c>McpWriteAuditBehavior</c> 在同一交易寫。
/// </summary>
public interface IIdempotentRequest : ITransactionalRequest { }
