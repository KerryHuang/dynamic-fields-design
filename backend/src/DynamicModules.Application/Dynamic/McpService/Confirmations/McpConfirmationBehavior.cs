using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Confirmations;

/// <summary>
/// 高風險請求的確認碼檢查（mcp1a 計畫判斷 18；寫死清單：安全、資料完整性）。登記在 <c>TransactionBehavior</c> 之後、
/// <c>AuditBehavior</c> 之前，所以在交易內：標記已用、業務寫入、確認稽核同一交易，任何一步失敗一起回滾。
/// <para>
/// 入口順序：不是確認請求就放行 → 與 <see cref="IAuditedRequest"/> 並用、不是交易型就丟例外 → 畫面通道（帶碼丟例外、沒帶放行）
/// → MCP 通道在另一個交易之內（交易深度大於 1）就丟例外 → 才驗碼。驗碼任一項不符回 <c>confirmation_invalid</c>，不說明哪一項。
/// </para>
/// </summary>
public sealed class McpConfirmationBehavior<TReq, TRes>(
    IRequestChannel channel, ICurrentUser current, McpConfirmationScope scope, IMcpConfirmationRepository confirmations,
    IAuditLog audit, IUnitOfWork uow, AfterCommitActions afterCommit, TimeProvider time) : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not IMcpConfirmedRequest confirmed) return await next();
        if (request is IAuditedRequest)
            throw new InvalidOperationException($"{typeof(TReq).Name} 是確認請求，不能同時有稽核宣告（一次呼叫會寫兩列）");
        if (request is not ITransactionalRequest || typeof(TRes) != typeof(ServiceResult))
            throw new InvalidOperationException($"{typeof(TReq).Name} 是確認請求，必須是交易型、回傳 ServiceResult");

        if (channel.Channel != RequestChannel.Mcp)
        {
            if (confirmed.Confirmation is not null) throw new InvalidOperationException("畫面通道的請求不能帶確認碼");
            scope.BeginNotRequired();
            return await next();
        }

        // 巢狀時交易管理回的是 Noop handle，回失敗結果不會回滾外層；只能丟例外一路退出最外層交易
        if (afterCommit.TransactionDepth > 1) throw new McpConfirmationNestingException();

        var ticket = confirmed.Confirmation;
        var row = ticket is null ? null : await ValidAsync(ticket, ct);
        if (ticket is null || row is null) return (TRes)(object)McpConfirmationResults.Invalid();

        var call = scope.BeginRequired(row);
        var response = await next();
        if (response is not ServiceResult { IsSuccess: true }) return response;
        if (!scope.Consumed)
            throw new InvalidOperationException($"{typeof(TReq).Name} 回了成功卻沒有消耗確認碼");

        // Add 只加進追蹤，外層交易提交時不會自動存檔；存檔失敗丟出，業務異動與已用標記一起回滾（寫法照 AuditBehavior）
        audit.Add(new AuditEntry(ticket.AuditCode, AuditResult.Success, TargetLabel: ticket.ToolName, Summary: ticket.AuditSummary,
            ArgumentsHash: ticket.ArgumentsHash, ConfirmationId: row.Id, ConfirmationOutcome: AuditConfirmationOutcome.Confirmed));
        await uow.CommitAsync(ct);
        afterCommit.Add("mcp.confirmed", scope.CommitMarker(call));
        return response;
    }

    private async Task<McpConfirmation?> ValidAsync(McpConfirmationTicket ticket, CancellationToken ct)
    {
        var hash = McpSecrets.Hash(ticket.Code);
        var row = await confirmations.FindByHashTrackedAsync(hash, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var ok = row is not null && McpSecrets.HashEquals(row.CodeHash, hash)
            && row.UsedUtc is null && row.VoidedUtc is null && row.ExpiresUtc > now
            && channel.TokenId is { } tokenId && row.TokenId == tokenId && ticket.TokenId == tokenId
            && string.Equals(row.UserId, current.UserId, StringComparison.Ordinal)
            && string.Equals(row.ToolName, ticket.ToolName, StringComparison.Ordinal)
            && string.Equals(row.ArgumentsHash, ticket.ArgumentsHash, StringComparison.Ordinal);
        return ok ? row : null;
    }
}
