using MediatR;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 只確認呼叫者是啟用中的系統管理員（判定在 <see cref="Behaviors.SystemAdminBehavior{TReq,TRes}"/>，不另寫一套）：
/// 要先擋人再讀上傳主體的端點用（語系包預覽，審查建議 2），通過回成功、沒有資料。
/// </summary>
public sealed record CheckSystemAdminRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class CheckSystemAdminHandler : IRequestHandler<CheckSystemAdminRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(CheckSystemAdminRequest request, CancellationToken ct) => Task.FromResult(ServiceResult.Success());
}
