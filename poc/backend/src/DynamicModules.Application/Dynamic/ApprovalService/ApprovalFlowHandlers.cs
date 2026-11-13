using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>流程增改共用的驗證：模組、代碼、名稱、目標狀態、優先序、結構驗證、入口約束（設計 §4.4、§5.2）。</summary>
public sealed class ApprovalFlowEditor(IApprovalFlowRepository flows, ApprovalDefinitionValidator validator)
{
    public const string UsedCannotDelete = "流程已被使用，只能停用";
    public const string UsedCannotRetarget = "被使用過的流程不能改目標狀態，請另建流程";

    /// <summary>和 approval-flows 底下的字面路由段衝突的代碼（PUT approval-flows/order 是排序端點）。</summary>
    public static readonly IReadOnlySet<string> ReservedKeys = new HashSet<string> { "order" };

    public static ServiceResult? CheckModule(Module module) =>
        module.Kind != ModuleKind.Standalone || ModuleRelations.IsDetail(module) || module.Workflow is null
            ? ServiceResult.Invalid(new Dictionary<string, string[]> { ["moduleKey"] = ["簽核流程只能掛在有狀態機的獨立模組上"] })
            : null;

    /// <summary>self 是更新中的流程（新增時為 null）；通過時回 null。</summary>
    public async Task<ServiceResult?> ValidateAsync(Module module, ApprovalFlowInput input, ApprovalFlow? self, CancellationToken ct)
    {
        var wf = module.Workflow!;
        var errors = new Dictionary<string, string[]>();
        void Err(string key, string message) => errors.TryAdd(key, [message]);

        if (!Regex.IsMatch(input.Key ?? "", ModuleRules.ModuleKeyPattern)) Err("key", "流程代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭");
        else if (ReservedKeys.Contains(input.Key!)) Err("key", ApprovalMessages.FlowKeyReserved);
        if (input.Name is null || !input.Name.IsValid(out _)) Err("name", "名稱必須有繁體中文");
        if (input.ToOptionId == wf.InitialOptionId || wf.States.All(s => s.OptionId != input.ToOptionId))
            Err("toOptionId", "目標狀態必須是狀態機的狀態，而且不是起始狀態");
        if (input.Priority < 0) Err("priority", "優先序不能是負數");
        if (input.Definition is null) Err("definition", "缺少流程定義");
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        var previous = self?.CurrentVersionId is { } vid ? (await flows.GetVersionAsync(vid, ct))?.Definition : null;
        // 定義沒變、而且是從啟用改成停用：定義裡引用的群組或欄位可能已被刪除或停用，這時仍要讓設計者先把流程停下來，所以不重跑定義驗證。啟用時照常驗證。
        var deactivatingOnly = self is { Active: true } && !input.Active && previous is not null && ApprovalDefinitionJson.SameAs(previous, input.Definition!);
        if (!deactivatingOnly)
        {
            foreach (var (k, v) in await validator.ValidateAsync(module, wf, input.Definition!, previous, ct)) errors.TryAdd($"definition.{k}", v);
            if (errors.Count > 0) return ServiceResult.Invalid(errors);
        }

        var current = await flows.ListCurrentByModuleAsync(module.Id, ct);
        if (current.Any(f => f.Flow.Id != self?.Id && f.Flow.ToOptionId == input.ToOptionId && f.Flow.Priority == input.Priority))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["priority"] = ["同一目標狀態上的流程優先序不能重複"] });

        var proposed = new ApprovalFlow
        {
            Id = self?.Id ?? 0, Key = input.Key!, NameI18n = input.Name!, ModuleId = module.Id, ToOptionId = input.ToOptionId,
            Priority = input.Priority, Active = input.Active
        };
        var set = current.Where(f => f.Flow.Id != self?.Id).Append(new FlowState(proposed, input.Definition!)).ToList();
        return Constraints(module, set);
    }

    /// <summary>流程集合改變後（例如刪除）重算入口約束。</summary>
    public static ServiceResult? Constraints(Module module, IReadOnlyList<FlowState> set)
    {
        var errors = ApprovalConstraints.Validate(module, module.Workflow!, set);
        return errors.Count > 0 ? ServiceResult.Invalid(errors) : null;
    }

    public async Task<ApprovalFlowResponse> ResponseAsync(ApprovalFlow f, string moduleKey, CancellationToken ct)
    {
        var v = (await flows.GetVersionAsync(f.CurrentVersionId!.Value, ct))!;
        return new ApprovalFlowResponse(f.Key, f.NameI18n, moduleKey, f.ToOptionId, f.Priority, f.Active, v.Version,
            await flows.IsUsedAsync(f.Id, ct), await flows.CountPendingAsync(f.Id, ct), v.Definition,
            await flows.IsVersionUsedAsync(v.Id, ct));
    }
}

public class CreateApprovalFlowHandler(
    IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<CreateApprovalFlowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateApprovalFlowRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        if (ApprovalFlowEditor.CheckModule(module) is { } bad) return bad;
        var input = request.Input;
        if (await editor.ValidateAsync(module, input, null, ct) is { } invalid) return invalid;
        if (await flows.GetByKeyAsync(input.Key, ct) is not null) return ServiceResult.Conflict("流程代碼已存在");

        var now = time.Now;
        var flow = new ApprovalFlow
        {
            Key = input.Key, NameI18n = input.Name, ModuleId = module.Id, ToOptionId = input.ToOptionId, Priority = input.Priority,
            Active = input.Active
        };
        flow.StampCreated(user.UserId, now);
        flows.Add(flow);
        await uow.CommitAsync(ct);
        var version = new ApprovalFlowVersion { FlowId = flow.Id, Version = 1, Definition = input.Definition, CreatedBy = user.UserId, CreatedTime = now };
        flows.AddVersion(version);
        await uow.CommitAsync(ct);
        flow.CurrentVersionId = version.Id;
        await uow.CommitAsync(ct);
        return ServiceResult.Created(await editor.ResponseAsync(flow, module.Key, ct));
    }
}

public class UpdateApprovalFlowHandler(
    IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateApprovalFlowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateApprovalFlowRequest request, CancellationToken ct)
    {
        var flow = await flows.GetByKeyAsync(request.Key, ct);
        if (flow is null) return ServiceResult.NotFound("找不到流程");
        var input = request.Input;
        if (input.Key != flow.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["流程代碼不能更改"] });
        var module = (await modules.GetByIdAsync(flow.ModuleId, includeChildren: true, ct))!;
        if (ApprovalFlowEditor.CheckModule(module) is { } bad) return bad;
        if (input.ToOptionId != flow.ToOptionId && await flows.IsUsedAsync(flow.Id, ct))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["toOptionId"] = [ApprovalFlowEditor.UsedCannotRetarget] });
        if (await editor.ValidateAsync(module, input, flow, ct) is { } invalid) return invalid;

        var now = time.Now;
        var current = (await flows.GetVersionAsync(flow.CurrentVersionId!.Value, ct))!;
        // 目前版本已被用過、而且定義真的改了才開新版本；只改名稱、啟停、優先序不動版本。
        var versionUsed = await flows.IsVersionUsedAsync(current.Id, ct);
        if (versionUsed && !ApprovalDefinitionJson.SameAs(current.Definition, input.Definition))
        {
            var next = new ApprovalFlowVersion
            {
                FlowId = flow.Id, Version = await flows.MaxVersionAsync(flow.Id, ct) + 1, Definition = input.Definition,
                CreatedBy = user.UserId, CreatedTime = now
            };
            flows.AddVersion(next);
            await uow.CommitAsync(ct);
            flow.CurrentVersionId = next.Id;
        }
        else if (!versionUsed)
        {
            current.Definition = input.Definition;
            current.CreatedBy = user.UserId;
            current.CreatedTime = now;
        }
        flow.NameI18n = input.Name;
        flow.ToOptionId = input.ToOptionId;
        flow.Priority = input.Priority;
        flow.Active = input.Active;
        flow.StampModified(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await editor.ResponseAsync(flow, module.Key, ct));
    }
}

public class DeleteApprovalFlowHandler(IModuleRepository modules, IApprovalFlowRepository flows, IUnitOfWork uow)
    : IRequestHandler<DeleteApprovalFlowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteApprovalFlowRequest request, CancellationToken ct)
    {
        var flow = await flows.GetByKeyAsync(request.Key, ct);
        if (flow is null) return ServiceResult.NotFound("找不到流程");
        if (await flows.IsUsedAsync(flow.Id, ct)) return ServiceResult.Conflict(ApprovalFlowEditor.UsedCannotDelete);
        var module = (await modules.GetByIdAsync(flow.ModuleId, includeChildren: true, ct))!;
        if (module.Workflow is not null)
        {
            var remaining = (await flows.ListCurrentByModuleAsync(module.Id, ct)).Where(f => f.Flow.Id != flow.Id).ToList();
            if (ApprovalFlowEditor.Constraints(module, remaining) is { } invalid) return invalid;
        }
        await flows.RemoveAsync(flow, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class ReorderApprovalFlowsHandler(IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor, IUnitOfWork uow)
    : IRequestHandler<ReorderApprovalFlowsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ReorderApprovalFlowsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        var list = await flows.ListByTargetTrackedAsync(module.Id, request.ToOptionId, ct);
        var keys = request.Keys ?? [];
        if (keys.Count != list.Count || keys.Distinct().Count() != keys.Count || !keys.ToHashSet().SetEquals(list.Select(f => f.Key)))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["keys"] = ["必須剛好列出這個目標狀態上的全部流程"] });

        // 先改成負值再改成 1..n，避開 (ModuleId, ToOptionId, Priority) 唯一索引。
        foreach (var f in list) f.Priority = -f.Priority - 1;
        await uow.CommitAsync(ct);
        var byKey = list.ToDictionary(f => f.Key);
        for (var i = 0; i < keys.Count; i++) byKey[keys[i]].Priority = i + 1;
        await uow.CommitAsync(ct);

        var result = new List<ApprovalFlowResponse>();
        foreach (var k in keys) result.Add(await editor.ResponseAsync(byKey[k], module.Key, ct));
        return ServiceResult.Success(result);
    }
}

public class GetApprovalFlowsHandler(IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor)
    : IRequestHandler<GetApprovalFlowsRequest, ServiceResult>, IRequestHandler<GetApprovalFlowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetApprovalFlowsRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        var result = new List<ApprovalFlowResponse>();
        foreach (var f in await flows.ListCurrentByModuleAsync(module.Id, ct)) result.Add(await editor.ResponseAsync(f.Flow, module.Key, ct));
        return ServiceResult.Success((IReadOnlyList<ApprovalFlowResponse>)result);
    }

    public async Task<ServiceResult> Handle(GetApprovalFlowRequest request, CancellationToken ct)
    {
        var moduleKey = await flows.ModuleKeyOfAsync(request.Key, ct);
        var flow = moduleKey is null ? null : await flows.GetByKeyAsync(request.Key, ct);
        return flow is null ? ServiceResult.NotFound("找不到流程") : ServiceResult.Success(await editor.ResponseAsync(flow, moduleKey!, ct));
    }
}
