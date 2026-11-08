using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.Integrations;

/// <summary>
/// 整合入口守衛（設計 §5.8）：在事件選取之前，依「保存的整合宣告」（不是當下仍存在的事件）判斷這次寫入會不會觸發整合的項目，
/// 再依整合當下推導的狀態決定放行或 409，避免連動被靜默略過：關閉夥伴後取消已扣庫存的出貨、整合欄位被停用後新出貨照常出貨。
/// 沒有已安裝的整合時只多一次小查詢。
/// </summary>
public class IntegrationGuardStage(
    IWriteJournal journal, IAppRepository apps, IModuleRepository modules, IEventFactRepository facts, IntegrationReconciler reconciler) : IWriteStage
{
    public int Order => 7;

    public Task<ServiceResult?> RunAsync(CancellationToken ct) => CheckAsync(JournalView.Merge(journal.Entries), ct);

    /// <summary>
    /// 對一批（已合併的）寫入日誌做守衛判定。入口（第 0 輪）由管線呼叫；事件效果寫入的後續各輪由 <see cref="EventStage"/> 在處理該輪之前呼叫，
    /// 經事件連鎖改到的來源記錄同樣受守衛約束。
    /// </summary>
    public async Task<ServiceResult?> CheckAsync(IEnumerable<JournalEntry> merged, CancellationToken ct)
    {
        var entries = merged.Where(e => !e.Silent && !e.AfterDeleted).ToList();
        if (entries.Count == 0) return null;
        var rows = (await apps.ListIntegrationsAsync(ct)).Where(r => r.Installed).ToList();
        if (rows.Count == 0) return null;

        var appList = await apps.ListAsync(ct);
        foreach (var row in rows)
        {
            var owner = appList.First(a => a.Id == row.AppId);
            var decl = IntegrationDeclarations.Find(owner, row.Code);
            if (decl is null) continue;
            var (forward, reverse) = await TriggeredAsync(owner, row, decl, entries, ct);
            if (!forward && !reverse) continue;

            var partners = await reconciler.PartnersAsync(decl, appList, ct);
            var name = decl.Name.Resolve("zh-TW");
            var target = IntegrationState.Derive(row.Enabled, true, owner.Status == AppStatus.Open, partners.Present, decl.OnPartnerClosed == "block");
            if (target == IntegrationTarget.Disabled)
            {
                if (reverse) return ServiceResult.Conflict(Unlinked(name));
                continue;
            }
            // 上一次調和已判定被阻擋：不論夥伴在不在，正向反向都擋（連動可能壞了，不能靜默略過）
            if (row.Status == IntegrationStatus.Blocked) return ServiceResult.Conflict(Blocked(name, row.Reason));
            switch (target)
            {
                case IntegrationTarget.Suspended when reverse:
                    return ServiceResult.Conflict(Unlinked(name));
                case IntegrationTarget.Held:
                    return ServiceResult.Conflict($"整合「{name}」要求夥伴 {partners.Missing} 開著才能操作；請先重新開啟夥伴應用程式");
                case IntegrationTarget.Run:
                    if (await reconciler.CheckIntegrityAsync(owner, row, decl, appList, ct) is { } problem)
                        return ServiceResult.Conflict(Blocked(name, problem));
                    break;
            }
        }
        return null;
    }

    private static string Unlinked(string name) =>
        $"整合「{name}」目前沒有運作，這筆記錄已經連動過，取消會讓兩邊對不上；請先讓整合恢復運作（重新開啟夥伴應用程式或啟用整合）再操作";

    private static string Blocked(string name, string? reason) =>
        $"整合「{name}」目前被阻擋（{reason}），為避免連動被略過，這個操作暫時不能做；請管理員修復整合";

    /// <summary>這次寫入會觸發整合的哪些項目：正向（建立連動）與反向（沖銷，且這筆來源記錄確實已經連動過）。</summary>
    private async Task<(bool Forward, bool Reverse)> TriggeredAsync(
        App owner, AppIntegration row, AppIntegrationDecl decl, List<JournalEntry> entries, CancellationToken ct)
    {
        // 絕大多數寫入跟這個整合無關：先看有沒有任何一筆落在事件的來源模組，沒有就不載入對照表
        var sourceModules = decl.Events.Select(e => owner.Modules.FirstOrDefault(m => m.Code == e.Source)?.ModuleId).OfType<long>().ToHashSet();
        if (!entries.Any(x => sourceModules.Contains(x.ModuleId))) return (false, false);

        var bindings = await apps.GetBindingsAsync(owner.Id, ct);
        var items = IntegrationItems.Parse(row.ItemsJson);
        long? Field(string name) =>
            bindings.Fields.TryGetValue(name, out var id) ? id
            : name.Split('.') is [var m, var c] && items.Fields.FirstOrDefault(f => f.Module == m && f.Code == c) is { } item ? item.FieldId : null;

        var forward = false;
        var reverse = false;
        foreach (var e in decl.Events)
        {
            var member = owner.Modules.FirstOrDefault(m => m.Code == e.Source);
            if (member is null) continue;
            foreach (var entry in entries.Where(x => x.ModuleId == member.ModuleId))
            {
                var module = await modules.GetByIdAsync(entry.ModuleId, includeChildren: true, ct);
                if (module is null) continue;
                var watch = e.Trigger.Watch?.Select(Field).ToList();
                var trigger = new EventTrigger(
                    e.Trigger.On,
                    e.Trigger.From?.Select(s => bindings.Options.GetValueOrDefault($"{e.Source}.{s}")).ToList(),
                    e.Trigger.To is null ? null : bindings.Options.GetValueOrDefault($"{e.Source}.{e.Trigger.To}"),
                    watch is null || watch.Any(w => w is null) ? null : watch.Select(w => w!.Value).ToList());
                var transient = new ModuleEvent { Key = e.Code, Definition = new EventDefinition(trigger, null, []) };
                var after = RecordValueProcessor.Parse(entry.AfterValues);
                var before = entry.BeforeValues is null || entry.Kind == JournalKind.Added ? null : RecordValueProcessor.Parse(entry.BeforeValues);
                if (!EventStage.Triggers(transient, module, before, after, entry.Kind)) continue;

                if (e.Effects.Any(x => x.Kind == "reverse"))
                {
                    var linked = await facts.ListBySourceAsync(entry.RecordId, ct);
                    if (linked.Any(f => e.Effects.Any(x => x.Kind == "reverse" && x.OfEvent == f.EventKey))) reverse = true;
                }
                else forward = true;
            }
        }
        return (forward, reverse);
    }
}
