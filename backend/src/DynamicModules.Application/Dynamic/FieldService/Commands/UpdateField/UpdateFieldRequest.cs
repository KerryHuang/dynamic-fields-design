using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;

/// <param name="Designer">從設計器畫面來的（控制器帶 true）：共用主檔上屬於貢獻者的欄位要合乎貢獻欄位的規則（共用主檔 §5.4、§15.2）；內部重播不帶。</param>
public record UpdateFieldRequest(long FieldId, FieldType Type, I18nText Label, FieldConfig Config, long BlockId, bool Designer = false) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(FieldId: FieldId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
