using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>個人代理設定（設計 §7.7、§13.5；計畫 U2、J15）。Active：今天（營運日）在起訖之內。</summary>
public sealed record ApprovalDelegateView(string DelegateUserId, string DelegateName, DateOnly StartDate, DateOnly EndDate, bool Active);

/// <summary>自己的代理設定；沒有設定時 Data 為 null。</summary>
public record GetMyApprovalDelegateRequest : IRequest<ServiceResult>;

/// <summary>設定自己的代理（已有就覆寫）；只能設自己的，沒有 userId 參數。設定當下不搬既有任務。</summary>
public record SaveMyApprovalDelegateRequest(string DelegateUserId, DateOnly StartDate, DateOnly EndDate) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>清除自己的代理設定；沒有設定時也回成功。</summary>
public record ClearMyApprovalDelegateRequest : IRequest<ServiceResult>, ITransactionalRequest;

internal static class ApprovalDelegateViews
{
    public static async Task<ApprovalDelegateView> ViewAsync(ApprovalDelegate d, IPermissionRepository permissions, IBusinessTime time, CancellationToken ct)
    {
        var names = await ApprovalViews.UserNamesAsync(permissions, ct);
        var today = time.Today;
        return new ApprovalDelegateView(d.DelegateUserId, names.GetValueOrDefault(d.DelegateUserId) ?? d.DelegateUserId, d.StartDate, d.EndDate,
            d.StartDate <= today && today <= d.EndDate);
    }
}

public class GetMyApprovalDelegateHandler(IApprovalDelegateRepository delegates, IPermissionRepository permissions, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<GetMyApprovalDelegateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMyApprovalDelegateRequest request, CancellationToken ct) =>
        ServiceResult.Success(await delegates.GetAsync(user.UserId, ct) is { } d ? await ApprovalDelegateViews.ViewAsync(d, permissions, time, ct) : null);
}

/// <summary>代理人存在且啟用、不是自己；起日 ≤ 迄日；迄日不早於今天（營運日）。</summary>
public class SaveMyApprovalDelegateHandler(
    IApprovalDelegateRepository delegates, IPermissionRepository permissions, ICurrentUser user, IBusinessTime time, IUnitOfWork uow)
    : IRequestHandler<SaveMyApprovalDelegateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SaveMyApprovalDelegateRequest request, CancellationToken ct)
    {
        if (request.DelegateUserId == user.UserId) return ServiceResult.Invalid("delegateUserId", ApprovalMessages.DelegateSelf);
        if (!(await permissions.ListUsersAsync(ct)).Any(u => u.UserId == request.DelegateUserId && u.IsActive))
            return ServiceResult.Invalid("delegateUserId", ApprovalMessages.DelegateInactive);
        if (request.StartDate > request.EndDate) return ServiceResult.Invalid("startDate", ApprovalMessages.DelegatePeriod);
        if (request.EndDate < time.Today) return ServiceResult.Invalid("endDate", ApprovalMessages.DelegateEnded);

        var d = await delegates.GetAsync(user.UserId, ct);
        if (d is null)
        {
            d = new ApprovalDelegate { UserId = user.UserId };
            delegates.Add(d);
        }
        d.DelegateUserId = request.DelegateUserId;
        d.StartDate = request.StartDate;
        d.EndDate = request.EndDate;
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await ApprovalDelegateViews.ViewAsync(d, permissions, time, ct));
    }
}

public class ClearMyApprovalDelegateHandler(IApprovalDelegateRepository delegates, ICurrentUser user, IUnitOfWork uow)
    : IRequestHandler<ClearMyApprovalDelegateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ClearMyApprovalDelegateRequest request, CancellationToken ct)
    {
        if (await delegates.GetAsync(user.UserId, ct) is { } d)
        {
            delegates.Remove(d);
            await uow.CommitAsync(ct);
        }
        return ServiceResult.Success();
    }
}
