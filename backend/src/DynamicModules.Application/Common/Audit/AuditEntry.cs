using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 呼叫端要寫的一筆稽核（維運設計 §17.2.1；與 <see cref="AuditLogEntry"/> 一一對應）。行為者、行為者種類、來源管道、權杖 id、
/// request id、來源 IP 由 <see cref="IAuditLog"/> 從環境補，呼叫端不填；只有 <see cref="ActorOverride"/> 例外。
/// </summary>
/// <param name="Summary">由 <see cref="AuditSummary"/> 組成的摘要 JSON；不自動序列化請求物件。</param>
/// <param name="ActorOverride">只在環境沒有身分時可給（例外：登入的 <see cref="AuditOrigin.BeginSignIn"/> 範圍）。</param>
public sealed record AuditEntry(
    string Action, AuditResult Result,
    string? TargetKind = null, string? TargetId = null, string? TargetLabel = null,
    string? ReasonCode = null, string? Summary = null,
    string? ArgumentsHash = null, int? ResultCount = null,
    long? ConfirmationId = null, AuditConfirmationOutcome? ConfirmationOutcome = null,
    ActorOverride? ActorOverride = null);

public sealed record ActorOverride(string UserId, AuditActorKind Kind = AuditActorKind.User);

/// <summary><see cref="IAuditLog.WriteNowAsync"/> 寫不進去（MCP 的 fail closed 看得到這個失敗）。</summary>
public sealed class AuditWriteException(Exception inner) : Exception("稽核寫入失敗", inner);
