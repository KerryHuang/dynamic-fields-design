using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.CheckService;

/// <summary>
/// 被檢查引用的欄位不能移除索引，加總欄位也不能改小數位數（設計 §5.3 第 4 點）：舊記錄的 ScaledValue 是依舊小數位算的。
/// 要改請先刪除檢查，改完再重建（重建會回填）。停用的檢查也算：它隨時可能重新啟用。
/// </summary>
public class CheckFieldGuard(ICheckRepository checks)
{
    public async Task<ServiceResult?> RejectAsync(Field field, FieldConfig newConfig, CancellationToken ct)
    {
        var stillIndexed = newConfig.IsFilterable || newConfig.IsSortable || field.Type is FieldType.MultiEnum or FieldType.Reference;
        var scaleChanged = ScaledNumber.ScaleOf(field.Type, field.Config) != ScaledNumber.ScaleOf(field.Type, newConfig);
        if (stillIndexed && !scaleChanged) return null;

        foreach (var check in await checks.ListByModuleAsync(field.ModuleId, ct))
        {
            if (!CheckIndexer.IndexedFieldIds(check.Definition).Contains(field.Id)) continue;
            if (!stillIndexed)
                return ServiceResult.Conflict($"欄位被跨記錄檢查「{check.Key}」引用，不能取消可篩選與可排序；請先刪除該檢查");
            if (scaleChanged && check.Definition.FieldId == field.Id)
                return ServiceResult.Conflict($"欄位是跨記錄檢查「{check.Key}」的加總欄位，不能改小數位數；請先刪除該檢查，改完再重建");
        }
        return null;
    }
}
