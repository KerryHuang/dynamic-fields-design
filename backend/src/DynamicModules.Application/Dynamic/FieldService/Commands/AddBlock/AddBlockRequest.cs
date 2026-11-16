using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;

/// <param name="Designer">從設計器畫面來的（控制器帶 true）：在共用主檔模組上要寫歸屬對照列與持久代號（共用主檔 §5.4）；內部重播（開啟套件、整合、匯入合併）不帶。</param>
public record AddBlockRequest(string ModuleKey, I18nText Label, bool Designer = false) : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];

    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
