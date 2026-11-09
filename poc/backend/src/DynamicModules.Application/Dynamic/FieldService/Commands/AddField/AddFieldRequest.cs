using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddField;

/// <param name="Designer">從設計器畫面來的（控制器帶 true）：在共用主檔模組上依所在區塊決定歸屬、寫對照列與持久代號（共用主檔 §5.4）；內部重播不帶。</param>
public record AddFieldRequest(string ModuleKey, long BlockId, FieldType Type, I18nText Label, FieldConfig Config, bool Designer = false)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];

    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
