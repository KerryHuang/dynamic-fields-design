using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data.Configurations;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Audit;

/// <summary>
/// 稽核清單（維運設計 §17.2.1「稽核頁讀取」、§17.5 <c>GET system/ops/audit</c>）：只給系統管理員。
/// 時間區間預設最近 7 天、最長 366 天；Id 遞減的 keyset 分頁，每頁最多 100 列。動作分組由伺服器展開成代碼清單（O2-T18）。
/// </summary>
public sealed record ListAuditRequest(
    DateTime? FromUtc = null, DateTime? ToUtc = null, string? ActorUserId = null, string? Action = null, string? ActionGroup = null,
    string? Channel = null, string? Result = null, string? TargetKind = null, string? TargetId = null, string? Cursor = null, int? Limit = null)
    : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record GetAuditRequest(long Id) : IRequest<ServiceResult>, ISystemAdminRequest;

/// <summary>動作目錄（分組、標籤鍵、風險級別），給篩選用。</summary>
public sealed record GetAuditActionsRequest : IRequest<ServiceResult>, ISystemAdminRequest;

/// <summary>清單的一列；開 Demo 登入時 <see cref="ClientIp"/> 一律為 null（不輸出，O2-U10）。</summary>
public record AuditSummaryResponse(
    long Id, DateTime OccurredUtc, string? ActorUserId, string? ActorName, AuditActorKind ActorKind, AuditChannel Channel, long? TokenId,
    string Action, AuditRiskLevel RiskLevel, string? TargetKind, string? TargetId, string? TargetLabel, AuditResult Result,
    string? ReasonCode, string? RequestId, string? ClientIp);

public sealed record AuditDetailResponse(
    long Id, DateTime OccurredUtc, string? ActorUserId, string? ActorName, AuditActorKind ActorKind, AuditChannel Channel, long? TokenId,
    string Action, AuditRiskLevel RiskLevel, string? TargetKind, string? TargetId, string? TargetLabel, AuditResult Result,
    string? ReasonCode, string? RequestId, string? ClientIp,
    JsonElement? Summary, string? ArgumentsHash, int? ResultCount, long? ConfirmationId, AuditConfirmationOutcome? ConfirmationOutcome)
    : AuditSummaryResponse(Id, OccurredUtc, ActorUserId, ActorName, ActorKind, Channel, TokenId, Action, RiskLevel, TargetKind, TargetId,
        TargetLabel, Result, ReasonCode, RequestId, ClientIp);

public sealed record AuditListResponse(List<AuditSummaryResponse> Items, string? NextCursor);

public sealed record AuditActionResponse(string Code, string Group, string LabelKey, AuditRiskLevel RiskLevel);

/// <summary>稽核頁的技術限制（維運設計 §17.9）。</summary>
public static class AuditQueryLimits
{
    public const int DefaultDays = 7, MaxDays = 366, MaxLimit = 100;
}

public class ListAuditHandler(IAuditLogRepository auditLog, IAccountRepository accounts, AuthSettings auth, TimeProvider time)
    : IRequestHandler<ListAuditRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ListAuditRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var to = request.ToUtc?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime;
        var from = request.FromUtc?.ToUniversalTime() ?? to.AddDays(-AuditQueryLimits.DefaultDays);
        if (from > to) errors["fromUtc"] = [OpsMessages.AuditRangeReversed];
        else if (to - from > TimeSpan.FromDays(AuditQueryLimits.MaxDays)) errors["toUtc"] = [OpsMessages.AuditRangeTooLong(AuditQueryLimits.MaxDays)];
        var limit = request.Limit ?? AuditQueryLimits.MaxLimit;
        if (limit is < 1 or > AuditQueryLimits.MaxLimit) errors["limit"] = [OpsMessages.AuditLimitOutOfRange(AuditQueryLimits.MaxLimit)];
        long? before = null;
        if (request.Cursor is { } cursor)
        {
            if (long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) before = id;
            else errors["cursor"] = [OpsMessages.AuditCursorInvalid];
        }
        AuditChannel? channel = null;
        if (request.Channel is { } c)
        {
            if (CamelEnum.TryParse<AuditChannel>(c, out var parsed)) channel = parsed;
            else errors["channel"] = [OpsMessages.AuditFilterInvalid];
        }
        AuditResult? result = null;
        if (request.Result is { } r)
        {
            if (CamelEnum.TryParse<AuditResult>(r, out var parsed)) result = parsed;
            else errors["result"] = [OpsMessages.AuditFilterInvalid];
        }
        if (request.ActionGroup is { } g && !AuditActions.Groups.Contains(g)) errors["actionGroup"] = [OpsMessages.AuditFilterInvalid];
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        // 動作與動作分組同時給時取交集
        IReadOnlyCollection<string>? actions = request.ActionGroup is { } group ? AuditActions.CodesOf(group) : null;
        if (request.Action is { } action) actions = actions is null || actions.Contains(action) ? [action] : [];

        var rows = await auditLog.PageAsync(new AuditLogQuery(from, to, before, request.ActorUserId, actions, channel, result,
            request.TargetKind, request.TargetId), limit + 1, ct);
        var page = rows.Take(limit).ToList();
        var names = await accounts.GetDisplayNamesAsync([.. page.Where(a => a.ActorUserId is not null).Select(a => a.ActorUserId!)], ct);
        var items = page.Select(a => AuditResponses.Summary(a, names, auth.DemoLoginEnabled)).ToList();
        return ServiceResult.Success(new AuditListResponse(items,
            rows.Count > limit ? page[^1].Id.ToString(CultureInfo.InvariantCulture) : null));
    }
}

public class GetAuditHandler(IAuditLogRepository auditLog, IAccountRepository accounts, AuthSettings auth)
    : IRequestHandler<GetAuditRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAuditRequest request, CancellationToken ct)
    {
        if (await auditLog.GetAsync(request.Id, ct) is not { } a) return ServiceResult.NotFound(OpsMessages.AuditNotFound);
        var names = a.ActorUserId is { } actor ? await accounts.GetDisplayNamesAsync([actor], ct) : [];
        var s = AuditResponses.Summary(a, names, auth.DemoLoginEnabled);
        JsonElement? summary = a.SummaryJson is { } json ? JsonDocument.Parse(json).RootElement.Clone() : null;
        return ServiceResult.Success(new AuditDetailResponse(
            s.Id, s.OccurredUtc, s.ActorUserId, s.ActorName, s.ActorKind, s.Channel, s.TokenId, s.Action, s.RiskLevel, s.TargetKind,
            s.TargetId, s.TargetLabel, s.Result, s.ReasonCode, s.RequestId, s.ClientIp,
            summary, a.ArgumentsHash, a.ResultCount, a.ConfirmationId, a.ConfirmationOutcome));
    }
}

public class GetAuditActionsHandler : IRequestHandler<GetAuditActionsRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(GetAuditActionsRequest request, CancellationToken ct) =>
        Task.FromResult(ServiceResult.Success(AuditActions.All.Select(e => new AuditActionResponse(e.Code, e.Group, e.LabelKey, e.Risk)).ToList()));
}

internal static class AuditResponses
{
    /// <param name="hideIp">開 Demo 登入（公開 Demo 的訪客彼此看得到稽核）時一律不回來源 IP（§17.9 安全、O2-U10）。</param>
    public static AuditSummaryResponse Summary(AuditLogEntry a, IReadOnlyDictionary<string, string> names, bool hideIp) =>
        new(a.Id, a.OccurredUtc, a.ActorUserId, a.ActorUserId is { } id ? names.GetValueOrDefault(id) : null, a.ActorKind, a.Channel, a.TokenId,
            a.Action, a.RiskLevel, a.TargetKind, a.TargetId, a.TargetLabel, a.Result, a.ReasonCode, a.RequestId, hideIp ? null : a.ClientIp);
}
