using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>個人代理設定（設計 §7.7、§13.5；計畫 U2、J15）。Active：今天（營運日）在起訖之內；Upcoming：起日在今天之後。</summary>
public sealed record ApprovalDelegateView(string DelegateUserId, string DelegateName, DateOnly StartDate, DateOnly EndDate, bool Active, bool Upcoming);

/// <summary>
/// GET me/approval-delegate：Delegate 沒有設定時是 null；Today 是伺服器營運日（前端的預設日期與提示都用它，不用瀏覽器日期）；
/// SecondsToNextDay：距下一個營運日的秒數，前端到點重新取得。
/// </summary>
public sealed record MyApprovalDelegateResponse(ApprovalDelegateView? Delegate, DateOnly Today, int SecondsToNextDay);

/// <summary>自己的代理設定與伺服器營運日（MyApprovalDelegateResponse；沒有設定時 Delegate 為 null）。</summary>
public record GetMyApprovalDelegateRequest : IRequest<ServiceResult>;

/// <summary>設定自己的代理（已有就覆寫）；只能設自己的，沒有 userId 參數。設定當下不搬既有任務。</summary>
public record SaveMyApprovalDelegateRequest(string DelegateUserId, DateOnly StartDate, DateOnly EndDate) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>清除自己的代理設定；沒有設定時也回成功。</summary>
public record ClearMyApprovalDelegateRequest : IRequest<ServiceResult>, ITransactionalRequest;

internal static class ApprovalDelegateViews
{
    public static async Task<ApprovalDelegateView> ViewAsync(ApprovalDelegate d, IPermissionRepository permissions, DateOnly today, CancellationToken ct)
    {
        var names = await ApprovalViews.UserNamesAsync(permissions, ct);
        return new ApprovalDelegateView(d.DelegateUserId, names.GetValueOrDefault(d.DelegateUserId) ?? d.DelegateUserId, d.StartDate, d.EndDate,
            d.StartDate <= today && today <= d.EndDate, today < d.StartDate);
    }
}

public class GetMyApprovalDelegateHandler(IApprovalDelegateRepository delegates, IPermissionRepository permissions, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<GetMyApprovalDelegateRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMyApprovalDelegateRequest request, CancellationToken ct)
    {
        var now = time.Now;   // 只取樣一次：active／upcoming、today 與秒數出自同一個時間點，跨午夜的請求不會回舊狀態
        var today = DateOnly.FromDateTime(now);
        var view = await delegates.GetAsync(user.UserId, ct) is { } d ? await ApprovalDelegateViews.ViewAsync(d, permissions, today, ct) : null;
        var toNextDay = (int)Math.Ceiling((today.AddDays(1).ToDateTime(TimeOnly.MinValue) - now).TotalSeconds);
        return ServiceResult.Success(new MyApprovalDelegateResponse(view, today, toNextDay));
    }
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
        return ServiceResult.Success(await ApprovalDelegateViews.ViewAsync(d, permissions, time.Today, ct));
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
