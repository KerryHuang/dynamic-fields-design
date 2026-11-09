using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.CheckService;

public record CheckInput(string Key, I18nText Name, string ModuleKey, bool Active, CheckDefinition Definition);

public record CheckResponse(string Key, I18nText Name, string ModuleKey, bool Active, CheckDefinition Definition, IReadOnlyList<string> Notices);

public static class CheckMapping
{
    public static CheckResponse ToResponse(this ModuleCheck c, string moduleKey, IReadOnlyList<string>? notices = null) =>
        new(c.Key, c.NameI18n, moduleKey, c.Active, c.Definition, notices ?? []);
}

public record CreateCheckRequest(CheckInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: Input.ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>更新時代碼與模組不能改。</summary>
public record UpdateCheckRequest(string Key, CheckInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(CheckKey: Key), new(ModuleKey: Input.ModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record DeleteCheckRequest(string Key) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(CheckKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record GetChecksRequest(string? Key = null, string? ModuleKey = null) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

/// <summary>試跑：目前已違規的組（至多 50 組），只回報不阻擋。Truncated 表示還有沒掃完的部分。</summary>
public record CheckViolation(decimal Value, decimal? Limit, string Message);

public record CheckScanResponse(IReadOnlyList<CheckViolation> Violations, bool Truncated);

public record ScanCheckRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}
