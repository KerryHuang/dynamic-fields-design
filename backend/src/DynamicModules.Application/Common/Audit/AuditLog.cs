using System.Diagnostics;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 稽核寫入（維運設計 §17.2.1、O2-T25）。三個寫法依「寫不進去時要怎樣」分：
/// <see cref="Add"/> 跟著呼叫端同一交易、<see cref="WriteNowAsync"/> 另開 scope 立刻寫且失敗丟例外（MCP 的 fail closed）、
/// <see cref="TryWriteNowAsync"/> 同上但失敗只記 Error log（登入）。
/// </summary>
public interface IAuditLog
{
    /// <summary>加進 DbContext 的追蹤，跟著呼叫端下一次 SaveChanges 寫進同一個交易；寫不進去整筆失敗。回傳追蹤中的列（存檔後才有 Id）。</summary>
    AuditLogEntry Add(AuditEntry entry);

    /// <summary>另開 DI scope 立刻寫；寫不進去丟 <see cref="AuditWriteException"/>。交易內呼叫丟 <see cref="InvalidOperationException"/>。</summary>
    Task WriteNowAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>同 <see cref="WriteNowAsync"/>，寫不進去只記 Error log、回 false；交易內呼叫一樣丟 <see cref="InvalidOperationException"/>。</summary>
    Task<bool> TryWriteNowAsync(AuditEntry entry, CancellationToken ct);
}

public sealed class AuditLog(
    IAuditLogRepository repository, ICurrentUser currentUser, IRequestOrigin origin, TimeProvider time,
    ITransactionManager transactions, IServiceScopeFactory scopes, ILogger<AuditLog> logger) : IAuditLog
{
    public AuditLogEntry Add(AuditEntry entry)
    {
        var row = Build(entry);
        repository.Add(row);
        return row;
    }

    public async Task WriteNowAsync(AuditEntry entry, CancellationToken ct)
    {
        // 第一步、建新 scope 之前：讀的是呼叫端 scope 的交易管理器（新 scope 裡一定是「沒有交易」，在那裡檢查永遠不會擋下）。
        // PostgreSQL 上同一請求另開寫入交易會等自己外層的閘門與鎖（pg0 §15 第 10 條）。
        GuardOutsideTransaction();
        var row = Build(entry);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IAuditLogRepository>().Add(row);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AuditWriteException(e);
        }
    }

    public async Task<bool> TryWriteNowAsync(AuditEntry entry, CancellationToken ct)
    {
        // 交易內呼叫是呼叫端的程式錯誤，照樣丟出、不吞成 false
        GuardOutsideTransaction();
        try
        {
            await WriteNowAsync(entry, ct);
            return true;
        }
        catch (AuditWriteException e)
        {
            logger.LogError(e.InnerException, "稽核寫不進去：{AuditAction}", entry.Action);
            return false;
        }
    }

    private void GuardOutsideTransaction()
    {
        if (transactions.IsActive)
            throw new InvalidOperationException("WriteNowAsync／TryWriteNowAsync 只能在交易外呼叫；交易內請用 Add");
    }

    private AuditLogEntry Build(AuditEntry e)
    {
        var (actor, kind) = ResolveActor(e);
        var channel = AuditOrigin.Channel;
        return new AuditLogEntry
        {
            OccurredUtc = time.GetUtcNow().UtcDateTime,
            ActorUserId = actor,
            ActorKind = kind,
            Channel = channel,
            TokenId = AuditOrigin.TokenId,
            Action = e.Action,
            RiskLevel = AuditActions.Find(e.Action)?.Risk
                ?? throw new InvalidOperationException($"稽核動作 {e.Action} 沒有登記在 AuditActions"),
            TargetKind = Cut(e.TargetKind, AuditLogEntry.TargetKindMax),
            TargetId = Cut(e.TargetId, AuditLogEntry.TargetIdMax),
            TargetLabel = Cut(e.TargetLabel, AuditLogEntry.TargetLabelMax),
            ArgumentsHash = e.ArgumentsHash,
            ResultCount = e.ResultCount,
            SummaryJson = e.Summary,
            ConfirmationId = e.ConfirmationId,
            ConfirmationOutcome = e.ConfirmationOutcome,
            Result = e.Result,
            ReasonCode = Cut(e.ReasonCode, AuditLogEntry.ReasonCodeMax),
            // 背景工作沒有 request id（§17.2.1）
            RequestId = channel == AuditChannel.Job ? null : Activity.Current?.TraceId.ToHexString(),
            ClientIp = origin.ClientIp,
        };
    }

    private (string? Actor, AuditActorKind Kind) ResolveActor(AuditEntry e)
    {
        // 登入稽核：行為者是 handler 明確傳入的帳號，不看請求帶的 cookie（§17.2.1）
        if (e.SignIn is { } signIn)
        {
            if (e.ActorOverride is not null) throw new InvalidOperationException("登入稽核的行為者只能由 SignInAudit 給");
            return signIn.UserId is { } signedIn ? (signedIn, AuditActorKind.User) : (null, AuditActorKind.System);
        }

        var identity = currentUser.UserId;
        var hasIdentity = !string.IsNullOrEmpty(identity);
        if (e.ActorOverride is { } o)
        {
            if (hasIdentity) throw new InvalidOperationException("環境已有身分時不能覆寫稽核的行為者（避免冒用）");
            return (o.UserId, o.Kind);
        }
        // 排程工作與沒有身分的寫入：行為者 null、種類「系統」（B3）
        return hasIdentity ? (identity, AuditOrigin.ActorKind ?? AuditActorKind.User) : (null, AuditActorKind.System);
    }

    private static string? Cut(string? value, int max) => value is { Length: var n } && n > max ? value[..max] : value;
}
