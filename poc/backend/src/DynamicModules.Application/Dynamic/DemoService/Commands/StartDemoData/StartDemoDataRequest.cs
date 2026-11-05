using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService.Commands.StartDemoData;

/// <summary>在背景開始產生 Demo 資料；需要設計器的 Add 與 Edit（Edit 由 handler 另外檢查）。</summary>
public record StartDemoDataRequest : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
