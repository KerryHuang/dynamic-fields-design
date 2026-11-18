using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.EventService;

public record CreateEventRequest(EventInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("event.create", AuditTargets.Event, Input?.Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: Input.SourceModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

/// <summary>更新時代碼與來源模組不能改（事件事實用代碼追溯）。</summary>
public record UpdateEventRequest(string Key, EventInput Input) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("event.update", AuditTargets.Event, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(EventKey: Key), new(ModuleKey: Input.SourceModuleKey)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record DeleteEventRequest(string Key) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("event.delete", AuditTargets.Event, Key);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(EventKey: Key)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public record GetEventsRequest(string? Key = null, string? SourceModuleKey = null) : IRequest<ServiceResult>, IDesignerRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}
