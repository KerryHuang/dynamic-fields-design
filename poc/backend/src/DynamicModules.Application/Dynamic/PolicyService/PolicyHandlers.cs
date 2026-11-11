using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
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

public class UpdateSystemPolicyHandler(ISystemSettingRepository settings, IPolicyReader reader, IUnitOfWork uow)
    : IRequestHandler<UpdateSystemPolicyRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateSystemPolicyRequest request, CancellationToken ct)
    {
        if (PolicyCatalog.Find(request.Code) is not { } def) return ServiceResult.NotFound(PolicyMessages.NotFound);
        if (request.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value)
        {
            if (PolicyJson.FromJson(def, value) is not { } canonical) return PolicyValueInvalid(def);
            await settings.SetAsync(def.SettingKey, canonical, ct);
        }
        else
        {
            await settings.RemoveAsync(def.SettingKey, ct);
        }
        await uow.CommitAsync(ct);
        reader.Invalidate();
        return ServiceResult.Success(PolicyJson.System(def, await reader.ResolveAsync(def, null, ct)));
    }

    internal static ServiceResult PolicyValueInvalid(PolicyDefinition def) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["value"] = [PolicyMessages.Invalid(def)] });
}

public class GetModulePoliciesHandler(IModuleRepository modules, IPolicyReader reader)
    : IRequestHandler<GetModulePoliciesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetModulePoliciesRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound(PolicyMessages.ModuleNotFound);
        var items = new List<ModulePolicyResponse>();
        foreach (var def in PolicyCatalog.All.Where(d => d.Scope == PolicyScope.Module))
            items.Add(PolicyJson.Module(def, await reader.ResolveAsync(def, module.Id, ct)));
        return ServiceResult.Success(items);
    }
}

public class UpdateModulePolicyHandler(
    IModuleRepository modules, IModulePolicyRepository overrides, IPolicyReader reader, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateModulePolicyRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateModulePolicyRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound(PolicyMessages.ModuleNotFound);
        if (PolicyCatalog.Find(request.Code) is not { } def) return ServiceResult.NotFound(PolicyMessages.NotFound);
        if (def.Scope == PolicyScope.System)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["code"] = [PolicyMessages.SystemOnly] });

        var existing = await overrides.GetTrackedAsync(module.Id, def.Code, ct);
        if (request.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value)
        {
            if (PolicyJson.FromJson(def, value) is not { } canonical) return UpdateSystemPolicyHandler.PolicyValueInvalid(def);
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
        }
        await uow.CommitAsync(ct);
        reader.Invalidate();
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
        foreach (var def in PolicyCatalog.All)
            values[def.Code] = PolicyJson.ToJson(def, (await reader.ResolveAsync(def, moduleId, ct)).Effective);
        return ServiceResult.Success(new EffectivePoliciesResponse(request.ModuleKey, values));
    }
}
