using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 設計器的定義變更遇到簽核中的實例（設計 §8.6）：會改寫記錄值的定義在有實例 pending 時回 409，
/// 避免簽核人看到的內容在簽核途中被定義變更改掉。判斷只看實例狀態，不看使用者身分；與 ClosedDataGuard、ImmutableDataGuard 掛在同一組檢查點。
/// </summary>
public sealed class PendingApprovalGuard(IApprovalInstanceRepository instances, IModuleRepository modules)
{
    /// <summary>
    /// 改寫值的範圍：本模組、以它為來源的彙總所在的父模組（公式重算會連帶重算），以及本模組是明細時的表頭模組。
    /// 有實例 pending 就回 409，否則 null。
    /// </summary>
    public async Task<ServiceResult?> RejectValueRewriteAsync(Module module, CancellationToken ct)
    {
        var ids = new HashSet<long> { module.Id };
        foreach (var (parent, _, _) in await modules.ListRollupsBySourceModuleAsync(module.Id, ct)) ids.Add(parent.Id);
        if (ModuleRelations.ParentKeyOf(module) is { } parentKey
            && await modules.GetByKeyAsync(parentKey, includeChildren: false, ct) is { } header)
            ids.Add(header.Id);
        var pending = await instances.ListPendingByModulesAsync(ids, ct);
        return pending.Count == 0 ? null : ServiceResult.Conflict(ApprovalMessages.DefinitionBlockedByPending(Titles(pending), pending.Count));
    }

    private static List<string> Titles(IEnumerable<PendingInstanceRef> list) => [.. list.Take(5).Select(p => p.TitleSnapshot)];
}
