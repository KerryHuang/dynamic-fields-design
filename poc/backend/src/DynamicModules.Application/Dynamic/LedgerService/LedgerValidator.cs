using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LedgerService;

/// <summary>分錄設定的結構驗證（設計 §6.1、§6.4）；錯誤鍵是 LedgerInput 的屬性名（camelCase）與 moduleKey。</summary>
public class LedgerValidator(IModuleRepository modules)
{
    private static readonly FieldType[] Numeric = [FieldType.Integer, FieldType.Decimal, FieldType.Currency];

    public async Task<ServiceResult?> ValidateAsync(Module module, LedgerInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        if (!module.IsWritable() || module.Kind != ModuleKind.Standalone || ModuleRelations.IsDetail(module))
            Err("moduleKey", "分錄必須是啟用中的獨立模組，不能是明細或擴充模組");
        if (module.Workflow is not null) Err("moduleKey", "有狀態機的模組不能設為分錄（分錄建立後不能修改，狀態轉移沒有意義）");

        var details = (await modules.ListDetailModulesAsync(module.Key, ct)).Select(d => d.Child).ToList();
        var own = ActiveFields(module);
        var all = own.Values.Concat(details.SelectMany(ActiveFields).Select(kv => kv.Value)).ToDictionary(f => f.Id);

        var rev = own.GetValueOrDefault(input.ReversalFieldId);
        if (rev is not { Type: FieldType.Reference } || rev.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } rt || rt.Key != module.Key
            || rev.Config.Relation == ReferenceRelation.Detail || !rev.Config.ReadOnly || rev.Config.Required
            || rev.Config.OnDelete is not (null or ReferenceOnDelete.Restrict))
            Err("reversalFieldId", "沖銷欄位必須是本模組指向自己、設為唯讀、非必填、刪除時禁止（restrict）的參照欄位");

        var negate = input.Negate ?? [];
        foreach (var id in negate)
            if (!all.TryGetValue(id, out var f) || !Numeric.Contains(f.Type)) { Err("negate", "取負號的欄位必須是本模組或其明細模組的整數、小數或金額欄位"); break; }
        foreach (var pair in input.Swap ?? [])
        {
            // 一對必須在同一個模組：本模組，或同一個明細模組（傳票的借貸明細）
            var home = details.Select(ActiveFields).Prepend(own).FirstOrDefault(m => pair is { Count: 2 } && m.ContainsKey(pair[0]) && m.ContainsKey(pair[1]));
            if (pair is not { Count: 2 } || pair[0] == pair[1] || home is null
                || home[pair[0]].Type != home[pair[1]].Type || !Numeric.Contains(home[pair[0]].Type))
            {
                Err("swap", "互換的欄位必須是本模組內、同型別的兩個不同數字欄位");
                break;
            }
        }
        var used = negate.Concat((input.Swap ?? []).SelectMany(p => p)).ToList();
        if (used.Distinct().Count() != used.Count) Err("negate", "同一個欄位不能同時出現在多處（取負號、互換）");

        // 一般參照的刪除行為必須 restrict；明細到表頭的參照例外（AddField 強制 Cascade，表頭不可刪所以不會執行）
        foreach (var m in details.Prepend(module))
            foreach (var f in ActiveFields(m).Values.Where(f => f is { Type: FieldType.Reference } && f.Config.Relation != ReferenceRelation.Detail))
                if (f.Config.OnDelete is not (null or ReferenceOnDelete.Restrict))
                {
                    Err("moduleKey", $"參照欄位「{f.LabelI18n.Resolve("zh-TW")}」的刪除行為必須是禁止（restrict）；分錄不能被連帶清空或刪除");
                    break;
                }

        // 分錄不能是彙總的父記錄（重算會改寫它）；明細彙總到表頭是允許的
        var detailKeys = details.Select(d => d.Key).ToHashSet();
        foreach (var f in own.Values.Where(f => f.Type == FieldType.Rollup && f.Config.Rollup is not null))
            if (await modules.GetByFieldIdAsync(f.Config.Rollup!.SourceFieldId, ct) is { } src && !detailKeys.Contains(src.Key))
            {
                Err("moduleKey", $"彙總欄位「{f.LabelI18n.Resolve("zh-TW")}」會由其他模組的記錄重算，分錄不能有這類欄位");
                break;
            }

        return errors.Count > 0 ? ServiceResult.Invalid(errors) : null;
    }

    private static Dictionary<long, Field> ActiveFields(Module m) =>
        m.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active).ToDictionary(f => f.Id);
}
