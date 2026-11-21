using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Upkeep;

/// <summary>
/// 一批確認碼清理（mcp1a 計畫判斷 22）：結束（使用、作廢、過期）早於 cutoff 的列。沒用過的（作廢或逾時）刪之前先寫一列
/// <see cref="AuditConfirmationOutcome.Expired"/> 稽核，管道 MCP、權杖是碼的權杖、行為者是碼的擁有者；同一交易刪列。回傳刪除數。
/// </summary>
public sealed record PurgeMcpConfirmationsBatchRequest(JobLeaseFence Fence, DateTime CutoffUtc, int BatchSize)
    : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class PurgeMcpConfirmationsBatchHandler(
    IScheduledJobRepository jobs, IMcpConfirmationRepository confirmations, IAuditLog audit, IUnitOfWork uow)
    : IRequestHandler<PurgeMcpConfirmationsBatchRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PurgeMcpConfirmationsBatchRequest request, CancellationToken ct)
    {
        var fence = request.Fence;
        if (!await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct)) return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
        var rows = await confirmations.ListPurgeBatchTrackedAsync(request.CutoffUtc, request.BatchSize, ct);
        foreach (var row in rows.Where(r => r.UsedUtc is null))
            using (AuditOrigin.Begin(AuditChannel.Mcp, row.TokenId))
                // 正式目錄沒有這個工具（例如測試工具）時記成 mcp.unknownTool，免得稽核寫不進去擋住清理
                audit.Add(new AuditEntry(McpToolCatalog.Find(row.ToolName)?.AuditCode ?? McpToolCatalog.UnknownToolAuditCode,
                    AuditResult.Invalid, TargetLabel: row.ToolName, ReasonCode: "expired", ArgumentsHash: row.ArgumentsHash,
                    ConfirmationId: row.Id, ConfirmationOutcome: AuditConfirmationOutcome.Expired, ActorOverride: new ActorOverride(row.UserId)));
        confirmations.RemoveRange(rows);
        if (rows.Count > 0) await uow.CommitAsync(ct);
        return ServiceResult.Success(rows.Count);
    }
}

/// <summary>一批權杖清理（判斷 22）：到期或撤銷早於 cutoff（稽核保留天數）的權杖列。回傳刪除數。</summary>
public sealed record PurgeMcpTokensBatchRequest(JobLeaseFence Fence, DateTime CutoffUtc, int BatchSize) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class PurgeMcpTokensBatchHandler(IScheduledJobRepository jobs, IMcpTokenRepository tokens, IUnitOfWork uow)
    : IRequestHandler<PurgeMcpTokensBatchRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PurgeMcpTokensBatchRequest request, CancellationToken ct)
    {
        var fence = request.Fence;
        if (!await jobs.HoldsLeaseAsync(fence.JobKey, fence.Owner, fence.NowUtc, ct)) return ServiceResult.Conflict(null, JobLeaseFence.LostCode);
        var rows = await tokens.ListPurgeBatchTrackedAsync(request.CutoffUtc, request.BatchSize, ct);
        tokens.RemoveRange(rows);
        if (rows.Count > 0) await uow.CommitAsync(ct);
        return ServiceResult.Success(rows.Count);
    }
}
