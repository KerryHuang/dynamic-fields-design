using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>哪些欄位不能當簽核關卡的可編輯欄位；流程定義驗證與狀態機回應的候選清單共用同一條規則。</summary>
public sealed class ApprovalEditableFields(IConversionRepository conversions, ILedgerRepository ledgers)
{
    /// <summary>不能當可編輯欄位的欄位：計算欄位、狀態欄位、轉換追溯欄位、分錄沖銷欄位、lock 帶值的目的欄位。</summary>
    public async Task<HashSet<long>> NonEditableAsync(Module module, Workflow wf, CancellationToken ct)
    {
        var all = module.Blocks.SelectMany(b => b.Fields).ToList();
        var ids = all.Where(f => f.Type is FieldType.Formula or FieldType.Rollup or FieldType.AutoNumber).Select(f => f.Id).ToHashSet();
        ids.Add(wf.StatusFieldId);
        foreach (var f in all.Where(f => f.Config.Fill is { Mode: FillConfig.Lock }))
            ids.UnionWith(f.Config.Fill!.Map.Select(m => m.To));
        foreach (var c in await conversions.ListTouchingAsync(module.Id, ct))
            if (c.TargetModuleId == module.Id) ids.Add(c.Definition.Header.SourceFieldId);
        if (await ledgers.GetByModuleAsync(module.Id, ct) is { } ledger) ids.Add(ledger.Definition.ReversalFieldId);
        return ids;
    }

    /// <summary>設計器可選的可編輯欄位：本模組啟用中的欄位扣掉不可編輯集合，依 Id 排序。</summary>
    public async Task<IReadOnlyList<long>> CandidatesAsync(Module module, Workflow wf, CancellationToken ct)
    {
        var nonEditable = await NonEditableAsync(module, wf, ct);
        return [.. module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active && !nonEditable.Contains(f.Id))
            .Select(f => f.Id).Order()];
    }
}
