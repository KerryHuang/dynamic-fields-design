using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>系統管理員看的維運資訊：版本、實例、資料庫、排程工作狀態。</summary>
public sealed record GetOpsInfoRequest : IRequest<ServiceResult>, ISystemAdminRequest;

/// <summary>前端錯誤收件：不需登入；UserId 只來自 cookie（登入且帳號有效時），不信 body。</summary>
public sealed record ReportClientErrorRequest(ClientErrorReport Report, string? UserId) : IRequest<ServiceResult>;
