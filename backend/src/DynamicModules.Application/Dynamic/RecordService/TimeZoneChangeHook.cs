using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>
/// 改營運時區（A17）時，在同一個交易裡把日期時間欄位的索引平移「新偏移減舊偏移」，讓列表篩選與報表日期切點跟著新時區；
/// 日期欄位、公式與彙總的日期結果不動，記錄原值字串（帶偏移的絕對時間）也不動。
/// 先檢查邊界：平移後超出 DateTime 範圍的列存在時整次拒絕（回 400，帶筆數與範圍），不發平移的 SQL
/// （平移出界時資料庫可能報錯把交易標成失效，也可能寫進讀不回來的值，不能靠事後接例外）。
/// ShiftDateTimeIndexAsync 是交易內的 ExecuteUpdate（PostgreSQL 的全庫寫入鎖在交易開始時已取）。
/// </summary>
public sealed class TimeZoneChangeHook(IRecordRepository records) : IPolicyChangeHook
{
    public async Task<ServiceResult?> BeforeCommitAsync(PolicyDefinition def, string oldEffective, string newEffective, CancellationToken ct)
    {
        if (def.Code != PolicyCatalog.LocaleTimeZone.Code) return null;
        var delta = PolicyCatalog.UtcOffsetOf(newEffective) - PolicyCatalog.UtcOffsetOf(oldEffective);
        if (delta == TimeSpan.Zero) return null;
        var (count, min, max) = await records.DateTimeIndexOutOfShiftRangeAsync(delta, ct);
        if (count > 0)
            return ServiceResult.Invalid(new Dictionary<string, string[]>
            {
                ["value"] = [PolicyMessages.TimeZoneShiftOutOfRange(count, min!.Value, max!.Value)]
            });
        await records.ShiftDateTimeIndexAsync(delta, ct);
        return null;
    }
}
