using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.SharedMasters;

/// <summary>
/// 共用主檔的狀態只能推得（設計 §7）：主檔 M 是 Open ⇔ 至少一個 Open 的應用程式（Kind = App）直接或經其他主檔使用 M。
/// 每次應用程式開啟、關閉、重開、刪除之後，在同一個交易內呼叫 <see cref="RecomputeAsync"/>；用閉包判斷，與判斷順序無關。
/// </summary>
public sealed class MasterLifecycle(
    IAppRepository apps, ISharedMasterRepository shared, IModuleRepository modules, IProgramRepository programs,
    MasterGrants grants, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
{
    /// <summary>使用者閉包：從 Open 的應用程式沿「使用」的邊往上找到的主檔。主檔本身不算使用者。</summary>
    public static HashSet<long> ShouldBeOpen(IEnumerable<App> all, IEnumerable<AppMasterUse> uses) =>
        Reachable(all.Where(a => a is { Kind: AppKind.App, Status: AppStatus.Open }).Select(a => a.Id), uses);

    /// <summary>從這些應用程式（或主檔）沿「使用」的邊直接或間接用到的主檔。</summary>
    public static HashSet<long> Reachable(IEnumerable<long> from, IEnumerable<AppMasterUse> uses)
    {
        var edges = uses.GroupBy(u => u.AppId).ToDictionary(g => g.Key, g => g.Select(u => u.MasterAppId).ToList());
        var found = new HashSet<long>();
        var stack = new Stack<long>(from);
        while (stack.Count > 0)
            foreach (var master in edges.GetValueOrDefault(stack.Pop()) ?? [])
                if (found.Add(master)) stack.Push(master);
        return found;
    }

    /// <summary>重算所有主檔的狀態與主檔模組的權限；回傳狀態有變的主檔。</summary>
    public async Task<IReadOnlyList<App>> RecomputeAsync(CancellationToken ct)
    {
        var all = await apps.ListAsync(ct);
        var open = ShouldBeOpen(all, await shared.ListUsesAsync(ct));
        var changed = new List<App>();
        foreach (var master in all.Where(a => a.Kind == AppKind.Master))
        {
            var want = open.Contains(master.Id) ? AppStatus.Open : AppStatus.Closed;
            if (master.Status == want) continue;
            await SetStatusAsync(master, want, ct);
            changed.Add(master);
        }
        await uow.CommitAsync(ct);
        await grants.RecalcAllAsync(ct);
        await uow.CommitAsync(ct);
        return changed;
    }

    /// <summary>
    /// 翻轉主檔的狀態：關閉時 Active 的成員模組改 Closed，重開時 Closed 改回 Active 並確保程式可用（同一般應用程式的關閉與重開）。
    /// 呼叫端負責提交。
    /// </summary>
    public async Task SetStatusAsync(App master, AppStatus status, CancellationToken ct)
    {
        foreach (var member in master.Modules)
        {
            var module = await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct);
            if (module is null) continue;
            if (status == AppStatus.Closed && module.Status == ModuleStatus.Active) module.Status = ModuleStatus.Closed;
            else if (status == AppStatus.Open && module.Status == ModuleStatus.Closed)
            {
                module.Status = ModuleStatus.Active;
                if (await programs.GetAsync(module.ProgramCode, ct) is { } program) program.Active = true;
            }
            else continue;
            module.TouchVersion(user.UserId, time.Now);
        }
        master.Status = status;
        master.TouchRowVersion();
        master.StampModified(user.UserId, time.Now);
    }

    /// <summary>
    /// 主檔表單的區塊順序固定（設計 §4.3）：基本區塊在前（維持原順序），貢獻區塊在後、依貢獻者 id 字典序，與開啟順序無關。
    /// 孤兒區塊以前擁有者的 id 排。呼叫端負責提交。
    /// </summary>
    public async Task ResequenceBlocksAsync(long moduleId, CancellationToken ct)
    {
        var module = await modules.GetByIdAsync(moduleId, includeChildren: true, ct);
        if (module is null) return;
        var owners = (await shared.ListBlockOwnersAsync(moduleId, ct)).GroupBy(o => o.BlockId).ToDictionary(g => g.Key, g => g.First().OwnerKey);
        var ordered = module.Blocks.Where(b => !owners.ContainsKey(b.Id)).OrderBy(b => b.Sequence).ThenBy(b => b.Id)
            .Concat(module.Blocks.Where(b => owners.ContainsKey(b.Id))
                .OrderBy(b => owners[b.Id], StringComparer.Ordinal).ThenBy(b => b.Sequence).ThenBy(b => b.Id))
            .ToList();
        var touched = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Sequence == i + 1) continue;
            ordered[i].Sequence = i + 1;
            touched = true;
        }
        if (touched) module.TouchVersion(user.UserId, time.Now);
    }

    /// <summary>
    /// 完整性檢查（設計 §7、§8）：主檔狀態符合使用者閉包，主檔模組的權限列等於來源記錄重算的結果。
    /// 回傳不成立的項目（人看的）；全部成立回空清單。供測試與啟動時記錄用。
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken ct)
    {
        var problems = new List<string>();
        var all = await apps.ListAsync(ct);
        var uses = await shared.ListUsesAsync(ct);
        var open = ShouldBeOpen(all, uses);
        foreach (var master in all.Where(a => a.Kind == AppKind.Master))
        {
            var want = open.Contains(master.Id) ? AppStatus.Open : AppStatus.Closed;
            if (master.Status != want) problems.Add($"共用主檔 {master.Key} 應為 {want}，實際是 {master.Status}");
        }
        foreach (var use in uses.Where(u => all.All(a => a.Id != u.MasterAppId) || all.First(a => a.Id == u.MasterAppId).Kind != AppKind.Master))
            problems.Add($"使用關係 {use.AppId} → {use.MasterAppId} 指向的不是共用主檔");
        foreach (var master in all.Where(a => a.Kind == AppKind.Master))
            foreach (var member in master.Modules)
                problems.AddRange(await grants.CheckAsync(member.ModuleId, ct));
        return problems;
    }
}
