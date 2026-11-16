using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;

/// <param name="Designer">從設計器畫面來的（控制器帶 true）：在共用主檔模組上照欄位的歸屬寫選項對照列與持久代號（共用主檔 §5.4）；內部重播不帶。</param>
public record AddOptionRequest(long FieldId, I18nText Label, string? Color, bool Designer = false)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(FieldId: FieldId)];

    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
