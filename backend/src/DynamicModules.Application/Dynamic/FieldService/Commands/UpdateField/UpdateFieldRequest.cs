using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;

/// <param name="Designer">從設計器畫面來的（控制器帶 true）：共用主檔上屬於貢獻者的欄位要合乎貢獻欄位的規則（共用主檔 §5.4、§15.2）；內部重播不帶。</param>
/// <param name="ExcludeFromAi">「不提供給 AI」：null＝不改（舊前端、套件、範本）；和現值不同時要權限設定（mcp1a 計畫判斷 4）。</param>
public record UpdateFieldRequest(long FieldId, FieldType Type, I18nText Label, FieldConfig Config, long BlockId, bool Designer = false,
    bool? ExcludeFromAi = null) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("field.update", AuditTargets.Field, FieldId);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(FieldId: FieldId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
