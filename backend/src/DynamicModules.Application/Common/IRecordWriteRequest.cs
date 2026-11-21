namespace DynamicModules.Application.Common;

/// <summary>
/// 會寫入記錄的請求（新增、修改、刪除、匯入、轉換、沖銷）。Idempotency 與 RecordEffects 管線行為只處理這類請求，
/// 設計器、帳號等其他請求直接放行。也是 <see cref="IIdempotentRequest"/>（MCP 寫入稽核以它認寫入請求）。
/// </summary>
public interface IRecordWriteRequest : IIdempotentRequest { }
