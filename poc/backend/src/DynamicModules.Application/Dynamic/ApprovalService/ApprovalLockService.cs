using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 簽核鎖定的單一來源（設計 §8.5、§9.3）：依模組算出鎖定狀態、鎖定欄位（含附件）、明細列是否一起鎖。
/// 寫入後盾、記錄更新、設計器顯示都問這裡。沒有狀態機或沒有需簽核轉移時回 <see cref="ApprovalLock.None"/>，不查資料庫。
/// scoped：沒傳候選狀態機時依模組 Id 在一個請求內快取。
/// </summary>
public sealed class ApprovalLockService(IApprovalFlowRepository flows, IModuleRepository modules)
{
    private readonly Dictionary<long, ApprovalLock> _cache = [];

    /// <param name="wf">候選或剛存的狀態機；null 用 <c>module.Workflow</c>（並快取）。有傳時不快取。</param>
    public async Task<ApprovalLock> ForModuleAsync(Module module, Workflow? wf, CancellationToken ct)
    {
        var cacheable = wf is null;
        if (cacheable && _cache.TryGetValue(module.Id, out var cached)) return cached;
        var result = await ComputeAsync(module, wf ?? module.Workflow, ct);
        if (cacheable) _cache[module.Id] = result;
        return result;
    }

    private async Task<ApprovalLock> ComputeAsync(Module module, Workflow? wf, CancellationToken ct)
    {
        if (wf is null) return ApprovalLock.None;
        var targets = ApprovalTopology.ApprovalTargets(wf);
        if (targets.Count == 0) return ApprovalLock.None;
        var defs = await flows.ListAllVersionDefinitionsAsync(module.Id, ct, [.. targets]);

        // 彙總的來源是「本單明細」才算（判斷 5）：來源參照欄位正好是來源模組指回表頭的明細欄位
        var own = new HashSet<long>();
        foreach (var rollup in module.Blocks.SelectMany(b => b.Fields)
                     .Where(f => f is { Type: FieldType.Rollup, Status: FieldStatus.Active, Config.Rollup: not null }))
        {
            var sourceId = rollup.Config.Rollup!.SourceFieldId;
            if (await modules.GetByFieldIdAsync(sourceId, ct) is { } source && ModuleRelations.DetailFieldOf(source)?.Id == sourceId)
                own.Add(rollup.Id);
        }
        return ApprovalTopology.Lock(module, wf, defs, f => own.Contains(f.Id));
    }
}
