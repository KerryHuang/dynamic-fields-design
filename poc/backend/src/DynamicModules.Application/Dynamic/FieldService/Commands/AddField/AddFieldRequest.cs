using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.AddField;

/// <param name="Designer">從設計器畫面來的（控制器帶 true）：在共用主檔模組上依所在區塊決定歸屬、寫對照列與持久代號（共用主檔 §5.4）；內部重播不帶。</param>
/// <param name="MasterBase">再匯入共用主檔時補的基本欄位（內部合併帶）：主檔關閉時公式欄位也照補，新欄位的值照算；
/// 凍結的貢獻欄位仍由寫入管線最底層的鎖保留舊值（共用主檔 §7、§9），所以不擋。</param>
public record AddFieldRequest(string ModuleKey, long BlockId, FieldType Type, I18nText Label, FieldConfig Config, bool Designer = false, bool MasterBase = false)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest
{
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(ModuleKey: ModuleKey)];

    public PermissionAction RequiredDesignerAction => PermissionAction.Add;
}
