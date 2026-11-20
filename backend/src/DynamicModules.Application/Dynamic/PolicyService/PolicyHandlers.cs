using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PolicyService;

// 政策設定 API（設計 docs/superpowers/specs/2026-11-11-policy-settings-design.md §7）。
// 權限由 pipeline 負責：系統政策走 SystemAdminBehavior，模組政策走設計權（共用主檔另要系統管理員）。

public class GetSystemPoliciesHandler(IPolicyReader reader) : IRequestHandler<GetSystemPoliciesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetSystemPoliciesRequest request, CancellationToken ct)
    {
        var items = new List<SystemPolicyResponse>();
        foreach (var def in PolicyCatalog.All)
            items.Add(PolicyJson.System(def, await reader.ResolveAsync(def, null, ct)));
        return ServiceResult.Success(items);
    }
}

public class UpdateSystemPolicyHandler(
    ISystemSettingRepository settings, IPolicyReader reader, IUnitOfWork uow, IAccountRepository accounts, IPermissionRepository permissions,
    AuditContext audit, IEnumerable<IPolicyChangeHook> hooks, CachedSystemPolicy cache, AfterCommitActions afterCommit)
    : IRequestHandler<UpdateSystemPolicyRequest, ServiceResult>
{
    /// <summary>
    /// 交易內（<c>ITransactionalRequest</c>）的順序：讀舊生效值 → 寫設定 → SaveChanges → 讀新生效值 → hooks → 登記快照更新。
    /// 設定的讀取是 AsNoTracking，SaveChanges 之前查不到新值，所以 hooks 在 SaveChanges 之後；hook 拒絕時外層交易整個回滾，
    /// 快照的更新登記在 <see cref="AfterCommitActions"/>，提交後才套用、回滾就丟掉（計畫判斷 20）。
    /// </summary>
    public async Task<ServiceResult> Handle(UpdateSystemPolicyRequest request, CancellationToken ct)
    {
        if (PolicyCatalog.Find(request.Code) is not { } def) return ServiceResult.NotFound(PolicyMessages.NotFound);
        // 稽核的舊值與新值：null＝出廠值（維運設計 §17.2.1）
        var old = await settings.GetValueAsync(def.SettingKey, ct);
        var oldEffective = (await reader.ResolveAsync(def, null, ct)).Effective;
        string? canonicalValue = null;
        if (request.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value)
        {
            if (PolicyJson.FromJson(def, value) is not { } canonical) return PolicyValueInvalid(def, value);
            if (await RecipientCheck.MissingAsync(def, canonical, accounts, permissions, ct) is { Count: > 0 } missing)
                return ServiceResult.Invalid(new Dictionary<string, string[]> { ["value"] = [PolicyMessages.RecipientsMissing(missing)] });
            await settings.SetAsync(def.SettingKey, canonical, ct);
            canonicalValue = canonical;
        }
        else
        {
            await settings.RemoveAsync(def.SettingKey, ct);
        }
        await uow.CommitAsync(ct);
        reader.Invalidate();
        var resolution = await reader.ResolveAsync(def, null, ct);
        var newEffective = resolution.Effective;
        if (newEffective != oldEffective)
            foreach (var hook in hooks)
                if (await hook.BeforeCommitAsync(def, oldEffective, newEffective, ct) is { } rejected) return rejected;
        if (def.Scope == PolicyScope.System)
        {
            // 交易內（已持有寫入鎖）配發序號：序號順序等於提交順序
            var seq = cache.NextSequence();
            afterCommit.Add("policy-cache", () => cache.Apply(def, newEffective, seq));
        }
        audit.Describe(new { Value = old }, new { Value = canonicalValue });
        return ServiceResult.Success(PolicyJson.System(def, resolution));
    }

    internal static ServiceResult PolicyValueInvalid(PolicyDefinition def, JsonElement value) =>
        ServiceResult.Invalid(new Dictionary<string, string[]>
        {
            ["value"] = [PolicyJson.ForbiddenItems(def, value) is { Count: > 0 } rejected ? PolicyMessages.Forbidden(rejected) : PolicyMessages.Invalid(def)]
        });
}

public class GetModulePoliciesHandler(IModuleRepository modules, IPolicyReader reader)
    : IRequestHandler<GetModulePoliciesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModulePoliciesRequest request, CancellationToken ct)
    {
        // 要載入欄位：明細模組看啟用中的明細關聯欄位判斷
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(PolicyMessages.ModuleNotFound);
        var isDetail = ModuleRelations.IsDetail(module);
        var items = new List<ModulePolicyResponse>();
        foreach (var def in PolicyCatalog.All.Where(d => d.Scope == PolicyScope.Module && d.AppliesTo(isDetail)))
            items.Add(PolicyJson.Module(def, await reader.ResolveAsync(def, module.Id, ct)));
        return ServiceResult.Success(items);
    }
}

public class UpdateModulePolicyHandler(
    IModuleRepository modules, IModulePolicyRepository overrides, IPolicyReader reader, IUnitOfWork uow, ICurrentUser user, IBusinessTime time,
    IAccountRepository accounts, IPermissionRepository permissions, AuditContext audit)
    : IRequestHandler<UpdateModulePolicyRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateModulePolicyRequest request, CancellationToken ct)
    {
        // 要載入欄位：明細模組看啟用中的明細關聯欄位判斷
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(PolicyMessages.ModuleNotFound);
        if (PolicyCatalog.Find(request.Code) is not { } def) return ServiceResult.NotFound(PolicyMessages.NotFound);
        if (def.Scope == PolicyScope.System)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["code"] = [PolicyMessages.SystemOnly] });
        if (!def.AppliesTo(ModuleRelations.IsDetail(module)))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["code"] = [PolicyMessages.NotForThisModule] });

        var existing = await overrides.GetTrackedAsync(module.Id, def.Code, ct);
        var old = existing?.Value;
        if (request.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value)
        {
            if (PolicyJson.FromJson(def, value) is not { } canonical) return UpdateSystemPolicyHandler.PolicyValueInvalid(def, value);
            if (await RecipientCheck.MissingAsync(def, canonical, accounts, permissions, ct) is { Count: > 0 } missing)
                return ServiceResult.Invalid(new Dictionary<string, string[]> { ["value"] = [PolicyMessages.RecipientsMissing(missing)] });
            if (existing is null)
            {
                existing = new ModulePolicy { ModuleId = module.Id, Code = def.Code };
                overrides.Add(existing);
            }
            existing.Value = canonical;
            existing.ModifiedUserId = user.UserId;
            existing.ModifiedTime = time.Now;
        }
        else if (existing is not null)
        {
            overrides.Remove(existing);
            existing = null;
        }
        await uow.CommitAsync(ct);
        reader.Invalidate();
        audit.Describe(new { Value = old }, new { Value = existing?.Value });
        return ServiceResult.Success(PolicyJson.Module(def, await reader.ResolveAsync(def, module.Id, ct)));
    }
}

public class GetEffectivePoliciesHandler(IModuleRepository modules, IPolicyReader reader)
    : IRequestHandler<GetEffectivePoliciesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetEffectivePoliciesRequest request, CancellationToken ct)
    {
        long? moduleId = null;
        if (request.ModuleKey is { } key)
        {
            var module = await modules.GetByKeyAsync(key, includeChildren: false, ct);
            if (module is null) return ServiceResult.NotFound(PolicyMessages.ModuleNotFound);
            moduleId = module.Id;
        }
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        // 收件人類政策不出現：前端執行畫面用不到，回應還會帶出使用者帳號
        foreach (var def in PolicyCatalog.All.Where(d => d.Kind != PolicyKind.Recipients))
            values[def.Code] = PolicyJson.ToJson(def, (await reader.ResolveAsync(def, moduleId, ct)).Effective);
        return ServiceResult.Success(new EffectivePoliciesResponse(request.ModuleKey, values));
    }
}

/// <summary>收件人政策存檔時檢查：群組存在、使用者存在且啟用；回傳找不到或已停用的項目。</summary>
internal static class RecipientCheck
{
    public static async Task<List<string>> MissingAsync(
        PolicyDefinition def, string canonical, IAccountRepository accounts, IPermissionRepository permissions, CancellationToken ct)
    {
        var missing = new List<string>();
        if (def.Kind != PolicyKind.Recipients) return missing;
        foreach (var item in PolicyDefinition.RecipientItems(canonical))
        {
            if (item.StartsWith("group:", StringComparison.Ordinal))
            {
                if (!await accounts.GroupsExistAsync([long.Parse(item[6..])], ct)) missing.Add(item);
            }
            else if (item.StartsWith("user:", StringComparison.Ordinal))
            {
                if (await permissions.GetUserAsync(item[5..], ct) is not { IsActive: true }) missing.Add(item);
            }
        }
        return missing;
    }
}
