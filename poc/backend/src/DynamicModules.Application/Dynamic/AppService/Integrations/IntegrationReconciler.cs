using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Integrations;

/// <summary>
/// 整合的調和函式（設計 §2.3、§5）：冪等，狀態只由當下條件推導。安裝與恢復以整個整合為單位，在同一個 savepoint 內進行，
/// 任何一步失敗整組回滾並標 Blocked；失敗不往外傳，夥伴或擁有者的開啟不會因為整合有問題而失敗。
/// 呼叫端必須在自己的 SaveChanges 之後才呼叫（回滾會清掉 change tracker）。
/// </summary>
public class IntegrationReconciler(
    IAppRepository apps, IModuleRepository modules, IEventRepository events, ICheckRepository checks, ISender sender,
    IUnitOfWork uow, IWorkSavepoints savepoints, ICurrentUser user, IBusinessTime time)
{
    /// <summary>調和全部整合；<paramref name="only"/> 指定時只調和那一個；<paramref name="retry"/> 為真時缺的項目補建（管理員確認過）。回傳要給使用者看的警告。</summary>
    public async Task<List<string>> ReconcileAsync(CancellationToken ct, (string AppKey, string Code)? only = null, bool retry = false)
    {
        var warnings = new List<string>();
        var keys = (await apps.ListIntegrationsAsync(ct)).Select(r => (r.AppId, r.Code)).ToList();
        if (keys.Count == 0) return warnings;
        var appKeys = (await apps.ListAsync(ct)).ToDictionary(a => a.Id, a => a.Key);
        foreach (var (appId, code) in keys)
        {
            if (only is { } o && (appKeys[appId] != o.AppKey || code != o.Code)) continue;
            if (await ReconcileOneAsync(appId, code, retry, ct) is { } warning) warnings.Add(warning);
        }
        return warnings;
    }

    private async Task<string?> ReconcileOneAsync(long appId, string code, bool retry, CancellationToken ct)
    {
        var row = (await apps.ListIntegrationsAsync(ct)).First(r => r.AppId == appId && r.Code == code);
        var appList = await apps.ListAsync(ct);
        var owner = appList.First(a => a.Id == appId);
        var decl = IntegrationDeclarations.Find(owner, code);
        if (decl is null)
        {
            row.Status = IntegrationStatus.Blocked;
            row.Reason = "unbound: 找不到整合宣告";
            await uow.CommitAsync(ct);
            return null;
        }
        var previous = row.Status;
        var partners = await PartnersAsync(decl, appList, ct);
        var items = IntegrationItems.Parse(row.ItemsJson);
        var target = IntegrationState.Derive(row.Enabled, row.Installed, owner.Status == AppStatus.Open, partners.Present, decl.OnPartnerClosed == "block");

        // 上一次已判定被阻擋（含客戶單獨停用某項）：調和不自動啟用項目、不解除阻擋，只有 retry（管理員）能修復；停用整合照常
        if (previous == IntegrationStatus.Blocked && row.Installed && !retry && target != IntegrationTarget.Disabled)
        {
            await SetActiveAsync(items, decl, false, ct);
            return null;
        }

        switch (target)
        {
            case IntegrationTarget.Disabled:
                if (row.Installed) await SuspendAsync(row, items, decl, ct);
                return await SetStatusAsync(row, IntegrationStatus.Disabled, null, previous, decl, ct);
            case IntegrationTarget.Waiting:
                return await SetStatusAsync(row, IntegrationStatus.Waiting, partners.Missing is null ? null : $"waiting: 等待 {partners.Missing}", previous, decl, ct);
            case IntegrationTarget.Suspended:
                await SuspendAsync(row, items, decl, ct);
                return await SetStatusAsync(row, IntegrationStatus.Suspended,
                    owner.Status == AppStatus.Open ? $"suspended: 夥伴 {partners.Missing} 不在" : "suspended: 擁有者已關閉", previous, decl, ct);
            case IntegrationTarget.Held:
                await SetActiveAsync(items, decl, true, ct);
                return await SetStatusAsync(row, IntegrationStatus.Held, $"held: 夥伴 {partners.Missing} 不在，來源寫入會被擋", previous, decl, ct);
            default:
                return await EnsureInSavepointAsync(owner, row, decl, items, appList, allowCreate: target == IntegrationTarget.Install || retry, ct);
        }
    }

    private async Task<string?> SetStatusAsync(AppIntegration row, string status, string? reason, string previous, AppIntegrationDecl decl, CancellationToken ct)
    {
        row.Status = status;
        row.Reason = reason;
        await uow.CommitAsync(ct);
        return status == IntegrationStatus.Suspended && previous is IntegrationStatus.Active or IntegrationStatus.Held
            ? $"整合「{decl.Name.Resolve("zh-TW")}」已暫停，暫停期間的來源記錄不會補入"
            : null;
    }

    /// <summary>夥伴都在：應用程式 Open，且用到的模組都是 Active。Missing 列出不在的。</summary>
    internal async Task<(bool Present, string? Missing)> PartnersAsync(AppIntegrationDecl decl, List<App> appList, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var w in decl.With)
        {
            var app = appList.FirstOrDefault(a => a.Key == w.Id);
            if (app is null || app.Status != AppStatus.Open) { missing.Add(w.Id); continue; }
            foreach (var code in w.Modules)
            {
                var member = app.Modules.FirstOrDefault(m => m.Code == code);
                var module = member is null ? null : await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct);
                if (!module.IsWritable()) missing.Add($"{w.Id}/{code}");
            }
        }
        return (missing.Count == 0, missing.Count == 0 ? null : string.Join("、", missing));
    }

    /// <summary>整合自己停用全部項目，先把當下啟用的記進 suspendedActive（與先前記的合併：已停用的重複調和不會洗掉紀錄）。</summary>
    private async Task SuspendAsync(AppIntegration row, IntegrationItems items, AppIntegrationDecl decl, CancellationToken ct)
    {
        var activeEvents = new List<string>(items.SuspendedActive?.Events ?? []);
        foreach (var key in items.Events)
            if (!activeEvents.Contains(key) && await events.GetByKeyAsync(key, ct) is { Active: true }) activeEvents.Add(key);
        var activeChecks = new List<string>(items.SuspendedActive?.Checks ?? []);
        foreach (var key in items.Checks)
            if (!activeChecks.Contains(key) && await checks.GetByKeyAsync(key, ct) is { Active: true }) activeChecks.Add(key);
        row.ItemsJson = (items with { SuspendedActive = new IntegrationSuspendedItems(activeEvents, activeChecks) }).ToJson();
        await SetActiveAsync(items, decl, false, ct);
    }

    /// <summary>直接切旗標、不驗證：停用永遠要做得到（來源模組可能已經 Closed）；on 時只恢復宣告為啟用的項目。</summary>
    private async Task SetActiveAsync(IntegrationItems items, AppIntegrationDecl decl, bool on, CancellationToken ct)
    {
        foreach (var key in items.Events)
            if (await events.GetByKeyAsync(key, ct) is { } e)
            {
                var want = on && (decl.Events.FirstOrDefault(x => x.Code == key)?.Active ?? false);
                if (e.Active == want) continue;
                e.Active = want;
                e.StampModified(user.UserId, time.Now);
            }
        foreach (var key in items.Checks)
            if (await checks.GetByKeyAsync(key, ct) is { } c)
            {
                var want = on && (decl.Checks.FirstOrDefault(x => x.Code == key)?.Active ?? false);
                if (c.Active == want) continue;
                c.Active = want;
                c.StampModified(user.UserId, time.Now);
            }
        await uow.CommitAsync(ct);
    }

    private async Task<string?> EnsureInSavepointAsync(
        App owner, AppIntegration row, AppIntegrationDecl decl, IntegrationItems items, List<App> appList, bool allowCreate, CancellationToken ct)
    {
        await uow.CommitAsync(ct);
        var ownerId = owner.Id;
        var savepoint = await savepoints.CreateAsync(ct);
        string? failure;
        try
        {
            failure = await EnsureAsync(owner, row, decl, items, appList, allowCreate, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"error: {ex.Message}";
        }
        if (failure is null)
        {
            await savepoint.ReleaseAsync(ct);
            return null;
        }

        await savepoint.RollbackAsync(ct);
        var fresh = (await apps.ListIntegrationsAsync(ct)).First(r => r.AppId == ownerId && r.Code == decl.Code);
        fresh.Status = IntegrationStatus.Blocked;
        fresh.Reason = failure;
        await uow.CommitAsync(ct);
        await SetActiveAsync(IntegrationItems.Parse(fresh.ItemsJson), decl, false, ct);
        return $"整合「{decl.Name.Resolve("zh-TW")}」無法啟用：{failure}";
    }

    /// <summary>回 null 代表成功（已寫入 ItemsJson、Installed、Status），否則是失敗原因（呼叫端負責回滾）。</summary>
    private async Task<string?> EnsureAsync(
        App owner, AppIntegration row, AppIntegrationDecl decl, IntegrationItems old, List<App> appList, bool allowCreate, CancellationToken ct)
    {
        var (world, problem) = await IntegrationWorld.LoadAsync(owner, decl, appList, apps, modules, ct);
        if (world is null) return $"unbound: {problem}";

        var created = IntegrationItems.Empty;

        // ---- 欄位 ----
        foreach (var f in decl.Fields)
        {
            var name = $"{f.Module}.{f.Code}";
            var config = ConfigOf(f, world);
            if (config is null) return $"unbound: 欄位 {name} 的夥伴模組 {ConfigString(f, "app")}/{ConfigString(f, "key")} 解析不到";
            var existing = old.Fields.FirstOrDefault(x => x.Module == f.Module && x.Code == f.Code);
            if (existing is not null && world.Fields.TryGetValue(existing.FieldId, out var field))
            {
                if (field.Status != FieldStatus.Active) return $"itemDisabled: 欄位 {name} 已被停用";
                if (field.Type != f.Type || JsonSerializer.Serialize(field.Config.RefTarget) != JsonSerializer.Serialize(config.RefTarget))
                    return $"itemConflict: 欄位 {name} 的型別或參照目標被改過；請還原成宣告的定義（保留原欄位）後重試";
                created.Fields.Add(existing);
                world.FieldIds[name] = existing.FieldId;
                world.FieldTypes[name] = f.Type;
                continue;
            }
            if (!allowCreate) return $"itemMissing: 欄位 {name} 不見了；確認要重建後請重試整合";
            if (!world.OwnerBlocks.TryGetValue($"{f.Module}.{f.Block}", out var blockId)) return $"unbound: 區塊 {f.Module}.{f.Block} 解析不到";
            var added = await sender.Send(new AddFieldRequest(world.ModuleKeys[f.Module], blockId, f.Type, f.Label, config), ct);
            if (!added.IsSuccess) return $"欄位 {name}：{Describe(added)}";
            var id = ((FieldResponse)added.Data!).Id;
            created.Fields.Add(new IntegrationFieldItem(f.Module, f.Code, id));
            world.FieldIds[name] = id;
            world.FieldTypes[name] = f.Type;
        }

        // ---- 事件（沖銷別人的排在後面）----
        foreach (var e in AppFlowInstaller.InDependencyOrder(decl.Events))
        {
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(e, world.Lookups(used));
            if (input is null) return $"unbound: 事件 {e.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 事件 {e.Code} 用到已停用或不存在的 {inactive}";
            var existing = await events.GetByKeyAsync(e.Code, ct);
            if (existing is null)
            {
                if (old.Events.Contains(e.Code) && !allowCreate) return $"itemMissing: 事件 {e.Code} 不見了；確認要重建後請重試整合";
                var made = await sender.Send(new CreateEventRequest(input), ct);
                if (!made.IsSuccess) return $"事件 {e.Code}：{Describe(made)}";
            }
            else
            {
                if (!old.Events.Contains(e.Code)) return $"codeConflict: 事件代碼 {e.Code} 已被其他來源使用";
                if (Serialize(existing.Definition) != Serialize(input.Definition) || existing.SourceModuleId != world.OwnerModules[e.Source].Id)
                    return $"itemConflict: 事件 {e.Code} 的定義被改過；請還原成宣告的定義後重試";
                if (!existing.Active && input.Active)
                {
                    // 只還原整合自己停用的；客戶停用的（不在 suspendedActive）要管理員 retry
                    if (!allowCreate && old.SuspendedActive?.Events.Contains(e.Code) != true) return $"itemDisabled: 事件 {e.Code} 被停用";
                    var enabled = await sender.Send(new UpdateEventRequest(e.Code, input), ct);
                    if (!enabled.IsSuccess) return $"事件 {e.Code}：{Describe(enabled)}";
                }
            }
            created.Events.Add(e.Code);
        }

        // ---- 檢查 ----
        foreach (var c in decl.Checks)
        {
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(c, world.Lookups(used));
            if (input is null) return $"unbound: 檢查 {c.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 檢查 {c.Code} 用到已停用或不存在的 {inactive}";
            var existing = await checks.GetByKeyAsync(c.Code, ct);
            if (existing is null)
            {
                if (old.Checks.Contains(c.Code) && !allowCreate) return $"itemMissing: 檢查 {c.Code} 不見了；確認要重建後請重試整合";
                var made = await sender.Send(new CreateCheckRequest(input), ct);
                if (!made.IsSuccess) return $"檢查 {c.Code}：{Describe(made)}";
            }
            else
            {
                if (!old.Checks.Contains(c.Code)) return $"codeConflict: 檢查代碼 {c.Code} 已被其他來源使用";
                if (Serialize(existing.Definition) != Serialize(input.Definition)) return $"itemConflict: 檢查 {c.Code} 的定義被改過；請還原成宣告的定義後重試";
                if (!existing.Active && input.Active)
                {
                    if (!allowCreate && old.SuspendedActive?.Checks.Contains(c.Code) != true) return $"itemDisabled: 檢查 {c.Code} 被停用";
                    var enabled = await sender.Send(new UpdateCheckRequest(c.Code, input), ct);
                    if (!enabled.IsSuccess) return $"檢查 {c.Code}：{Describe(enabled)}";
                }
            }
            created.Checks.Add(c.Code);
        }

        var fresh = (await apps.ListIntegrationsAsync(ct)).First(r => r.AppId == owner.Id && r.Code == decl.Code);
        fresh.ItemsJson = created.ToJson();
        fresh.Installed = true;
        fresh.Status = IntegrationStatus.Active;
        fresh.Reason = null;
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>
    /// 輕量完整性檢查（入口守衛用，不寫任何東西）：整合建立的欄位、事件、檢查都在、為啟用，夥伴符號解析得到且欄位啟用。
    /// 回 null 代表完整；否則是原因。
    /// </summary>
    public async Task<string?> CheckIntegrityAsync(App owner, AppIntegration row, AppIntegrationDecl decl, List<App> appList, CancellationToken ct)
    {
        var items = IntegrationItems.Parse(row.ItemsJson);
        var (world, problem) = await IntegrationWorld.LoadAsync(owner, decl, appList, apps, modules, ct);
        if (world is null) return $"unbound: {problem}";
        foreach (var f in decl.Fields)
        {
            var name = $"{f.Module}.{f.Code}";
            var item = items.Fields.FirstOrDefault(x => x.Module == f.Module && x.Code == f.Code);
            if (item is null || !world.Fields.TryGetValue(item.FieldId, out var field)) return $"itemMissing: 欄位 {name} 不見了";
            if (field.Status != FieldStatus.Active) return $"itemDisabled: 欄位 {name} 已被停用";
            world.FieldIds[name] = item.FieldId;
            world.FieldTypes[name] = f.Type;
        }
        foreach (var e in decl.Events)
        {
            var existing = await events.GetByKeyAsync(e.Code, ct);
            if (existing is null || !items.Events.Contains(e.Code)) return $"itemMissing: 事件 {e.Code} 不見了";
            if (e.Active && !existing.Active) return $"itemDisabled: 事件 {e.Code} 被停用";
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(e, world.Lookups(used));
            if (input is null) return $"unbound: 事件 {e.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 事件 {e.Code} 用到已停用或不存在的 {inactive}";
        }
        foreach (var c in decl.Checks)
        {
            var existing = await checks.GetByKeyAsync(c.Code, ct);
            if (existing is null || !items.Checks.Contains(c.Code)) return $"itemMissing: 檢查 {c.Code} 不見了";
            if (c.Active && !existing.Active) return $"itemDisabled: 檢查 {c.Code} 被停用";
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(c, world.Lookups(used));
            if (input is null) return $"unbound: 檢查 {c.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 檢查 {c.Code} 用到已停用或不存在的 {inactive}";
        }
        return null;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    private static string? ConfigString(AppIntegrationField f, string name) =>
        f.Config.ValueKind == JsonValueKind.Object && f.Config.TryGetProperty("refTarget", out var t) && t.ValueKind == JsonValueKind.Object
            && t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>整合欄位的 config：refTarget.app 換成夥伴模組的實際鍵。夥伴模組解析不到回 null。</summary>
    private static FieldConfig? ConfigOf(AppIntegrationField f, IntegrationWorld world)
    {
        var node = f.Config.ValueKind == JsonValueKind.Object ? JsonNode.Parse(f.Config.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();
        if (node["refTarget"] is JsonObject target && target["app"]?.GetValue<string>() is { } app)
        {
            target.Remove("app");
            if (!world.ModuleKeys.TryGetValue($"{app}/{target["key"]?.GetValue<string>()}", out var key)) return null;
            target["key"] = key;
        }
        return node.Deserialize<FieldConfig>(TemplateJson.Options);
    }

    internal static string Describe(ServiceResult r) =>
        r.Errors.Count > 0 ? string.Join("；", r.Errors.SelectMany(e => e.Value)) : r.Message ?? "失敗";
}
