using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService;

/// <summary>系統管理員看的維運資訊：版本、實例、資料庫、排程工作狀態。</summary>
public sealed record GetOpsInfoRequest : IRequest<ServiceResult>, ISystemAdminRequest;
