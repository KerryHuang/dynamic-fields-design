using System.Text;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>設計器請求本文的格式檢查（J65、J66）：只收流程圖格式、版面格式合法、草稿不超過 256 KB。草稿存檔、乾跑驗證、發布共用。</summary>
public static class ApprovalDraftRules
{
    public static ServiceResult? CheckFormat(ApprovalFlowDraftInput input)
    {
        if (input.Definition is null) return Invalid("definition", ApprovalMessages.DefinitionMissing);
        if (input.Definition.Shape != ApprovalShapes.Graph) return Invalid("definition.shape", ApprovalMessages.DraftShapeInvalid);
        if (ApprovalFlowLayout.Validate(input.Layout).Count > 0) return Invalid("layout", ApprovalMessages.LayoutInvalid);
        var size = Encoding.UTF8.GetByteCount(new ApprovalFlowDraft(ApprovalFlowDraft.NewRev(), input.Definition, LayoutOf(input)).Serialize());
        return size > ApprovalGraphLimits.MaxDraftBytes ? Invalid("definition", ApprovalMessages.DraftTooLarge) : null;
    }

    /// <summary>版本的 LayoutJson 只留下新定義裡還存在的節點（§9 末段）；沒有版面回 null。</summary>
    public static string? PruneLayoutJson(string? layoutJson, ApprovalDefinition definition) =>
        layoutJson is null ? null : ApprovalFlowLayout.Prune(ApprovalFlowLayout.Parse(layoutJson), definition).ToJson();

    /// <summary>請求本文的版面轉成存放用的 JSON；沒有版面（null）回 null。</summary>
    public static ApprovalFlowLayout? LayoutOf(ApprovalFlowDraftInput input) =>
        input.Layout is { ValueKind: System.Text.Json.JsonValueKind.Object } ? ApprovalFlowLayout.Parse(input.Layout) : null;

    /// <summary>
    /// 存草稿、乾跑驗證、發布共用的流程列：啟停沿用目前狀態（沒有流程列時停用）；優先序只在換目標狀態時採用請求值，
    /// 原目標狀態上的位置不動（J65）。三個 handler 必須取同一份，乾跑驗證才與發布一致（J64）。
    /// </summary>
    public static ApprovalFlowInput RowFor(ApprovalFlow? flow, ApprovalFlowDraftInput input)
    {
        var row = input.ToFlowInput(flow?.Active ?? false);
        return flow is not null && input.ToOptionId == flow.ToOptionId ? row with { Priority = flow.Priority } : row;
    }

    private static ServiceResult Invalid(string key, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [key] = [message] });
}

/// <summary>乾跑驗證（J64）：跑與發布相同的檢查（流程列、定義、優先序唯一、入口約束），只差樂觀鎖；不寫入。</summary>
public class ValidateApprovalFlowHandler(IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor)
    : IRequestHandler<ValidateApprovalFlowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ValidateApprovalFlowRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        if (ApprovalFlowEditor.CheckModule(module) is { } bad) return bad;
        var input = request.Input;
        if (ApprovalDraftRules.CheckFormat(input) is { } format) return format;

        ApprovalFlow? self = null;
        if (request.Key is { } key)
        {
            self = await flows.GetByKeyAsync(key, ct);
            if (self is null || self.ModuleId != module.Id) return KeyError(ApprovalMessages.FlowNotInModule);
            if (input.Key != self.Key) return KeyError("流程代碼不能更改");
        }

        var failure = self is null ? null : await editor.CheckRetargetAsync(self, input.ToOptionId, ct);
        IReadOnlyList<ApprovalFlowWarning> warnings = [];
        if (failure is null)
        {
            // 流程列照發布的取法（RowFor）：啟停沿用目前狀態，優先序只在換目標狀態時採用請求值
            var check = await editor.CheckAsync(module, ApprovalDraftRules.RowFor(self, input), self, ct);
            (failure, warnings) = (check.Failure, check.Warnings);
        }
        var errors = failure is null ? [] : failure.Errors.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (self is null && !errors.ContainsKey("key") && await flows.GetByKeyAsync(input.Key, ct) is not null)
            errors["key"] = [ApprovalMessages.FlowKeyTaken];

        var (newVersion, next, pending) = await VersionImpactAsync(self, input.Definition, ct);
        return ServiceResult.Success(new ApprovalFlowValidation(errors, errors.Count > 0 ? [] : warnings, newVersion, next, pending));
    }

    /// <summary>目前版本被用過、而且這份定義與它不同才會開新版本（沿用 UpdateApprovalFlowHandler 的判斷）。</summary>
    private async Task<(bool NewVersion, int? NextVersion, int Pending)> VersionImpactAsync(ApprovalFlow? flow, ApprovalDefinition definition, CancellationToken ct)
    {
        if (flow is null) return (false, null, 0);
        var pending = await flows.CountPendingAsync(flow.Id, ct);
        if (flow.CurrentVersionId is not { } vid || await flows.GetVersionAsync(vid, ct) is not { } current) return (false, null, pending);
        var opens = await flows.IsVersionUsedAsync(vid, ct) && !ApprovalDefinitionJson.SameAs(current.Definition, definition);
        return (opens, opens ? await flows.MaxVersionAsync(flow.Id, ct) + 1 : null, pending);
    }

    private static ServiceResult KeyError(string message) => ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = [message] });
}

/// <summary>草稿與發布共用的寫入輔助：樂觀鎖比對、衝突回應。權杖一律用 <see cref="ApprovalFlowDraft.TokenOf"/>（發布、捨棄後只剩權杖）。</summary>
public sealed class ApprovalDraftGate(IApprovalFlowRepository flows, ApprovalFlowEditor editor)
{
    /// <summary>
    /// 邏輯比對（J65）：請求的 BaseRev 對讀到的權杖（沒有或讀不懂視為 null）、BaseVersionId 對 CurrentVersionId；
    /// 不符且不是 Force 回 409。條件更新另外擋「讀取之後才變」的情況。
    /// </summary>
    public async Task<ServiceResult?> CheckBaseAsync(ApprovalFlow flow, ApprovalFlowDraftInput input, CancellationToken ct) =>
        input.Force || (input.BaseRev == ApprovalFlowDraft.TokenOf(flow.DraftJson) && input.BaseVersionId == flow.CurrentVersionId)
            ? null
            : await ConflictAsync(flow, ct);

    public async Task<ServiceResult> ConflictAsync(ApprovalFlow flow, CancellationToken ct)
    {
        // 草稿清掉（別人剛發布或捨棄）時沒有草稿修改者，改報流程最後的修改者與時間，前端才說得出是誰動的
        var (by, time) = flow.DraftUpdatedBy is null ? (flow.ModifiedUserId, flow.ModifiedTime) : (flow.DraftUpdatedBy, flow.DraftUpdatedTime);
        var names = by is null ? null : await editor.NamesAsync(ct);
        return ServiceResult.Conflict(ApprovalMessages.DraftConflict, "DraftConflict",
            new DraftConflictInfo(by, by is null ? null : names!.GetValueOrDefault(by, by), time, ApprovalFlowDraft.TokenOf(flow.DraftJson), flow.CurrentVersionId));
    }

    /// <summary>條件更新失敗：重讀資料列再回 409，修改者與時間才是別人剛存的那一份。</summary>
    public async Task<ServiceResult> ConflictAfterLostRaceAsync(ApprovalFlow flow, CancellationToken ct)
    {
        await flows.ReloadAsync(flow, ct);
        return await ConflictAsync(flow, ct);
    }
}

/// <summary>建立只有草稿的新流程（§10.8、J64）：不跑 §9，只驗格式、大小與流程列欄位；沒有版本、強制停用。</summary>
public class CreateApprovalFlowDraftHandler(
    IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<CreateApprovalFlowDraftRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateApprovalFlowDraftRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound("找不到模組");
        if (ApprovalFlowEditor.CheckModule(module) is { } bad) return bad;
        var input = request.Input;
        if (ApprovalDraftRules.CheckFormat(input) is { } format) return format;
        if (await editor.ValidateRowAsync(module, input.ToFlowInput(false), null, ct) is { } invalid) return invalid;
        if (await flows.GetByKeyAsync(input.Key, ct) is not null) return ServiceResult.Conflict(ApprovalMessages.FlowKeyTaken, "FlowKeyTaken");

        var now = time.Now;
        var flow = new ApprovalFlow
        {
            Key = input.Key, NameI18n = input.Name, ModuleId = module.Id, ToOptionId = input.ToOptionId, Priority = input.Priority, Active = false,
            DraftJson = new ApprovalFlowDraft(ApprovalFlowDraft.NewRev(), input.Definition, ApprovalDraftRules.LayoutOf(input)).Serialize(),
            DraftUpdatedBy = user.UserId, DraftUpdatedTime = now
        };
        flow.StampCreated(user.UserId, now);
        flows.Add(flow);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(await editor.ResponseAsync(flow, module.Key, ct));
    }
}

/// <summary>
/// 儲存草稿（§10.8、J65）：草稿只驗格式與大小；同時套用名稱與目標狀態（U53，立即生效）。順序：驗格式 → 流程列欄位 →
/// 已發布流程換目標狀態時用已發布版本的定義重驗入口約束 → 邏輯比對 → 條件更新草稿 → 名稱與目標狀態（同一交易）。
/// </summary>
public class SaveApprovalFlowDraftHandler(
    IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor, ApprovalDraftGate gate,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<SaveApprovalFlowDraftRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SaveApprovalFlowDraftRequest request, CancellationToken ct)
    {
        var flow = await flows.GetByKeyAsync(request.Key, ct);
        if (flow is null) return ServiceResult.NotFound("找不到流程");
        var input = request.Input;
        if (input.Key != flow.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["流程代碼不能更改"] });
        var module = (await modules.GetByIdAsync(flow.ModuleId, includeChildren: true, ct))!;
        if (ApprovalFlowEditor.CheckModule(module) is { } bad) return bad;
        if (ApprovalDraftRules.CheckFormat(input) is { } format) return format;

        var retargeted = input.ToOptionId != flow.ToOptionId;
        var row = ApprovalDraftRules.RowFor(flow, input);
        if (retargeted && await editor.CheckRetargetAsync(flow, input.ToOptionId, ct) is { } used) return used;
        if (await editor.ValidateRowAsync(module, row, flow, ct) is { } invalid) return invalid;
        // 已發布的流程換目標狀態：已發布版本的定義放到新目標狀態上要合法（含 G9–G13 與入口約束），否則整筆不寫（審查 🔴2）
        if (retargeted && flow.CurrentVersionId is { } vid)
        {
            var published = (await flows.GetVersionAsync(vid, ct))!.Definition;
            if (await editor.ValidateAsync(module, row with { Definition = published }, flow, ct) is { } rejected) return rejected;
        }
        if (await gate.CheckBaseAsync(flow, input, ct) is { } conflict) return conflict;

        var now = time.Now;
        var draft = new ApprovalFlowDraft(ApprovalFlowDraft.NewRev(), input.Definition, ApprovalDraftRules.LayoutOf(input));
        if (!await flows.TryReplaceDraftAsync(flow.Id, flow.DraftJson, flow.CurrentVersionId, draft.Serialize(), user.UserId, now, ct))
            return await gate.ConflictAfterLostRaceAsync(flow, ct);

        if (!SameName(flow, input) || retargeted)
        {
            flow.NameI18n = input.Name;
            flow.ToOptionId = input.ToOptionId;
            flow.Priority = row.Priority;
            flow.StampModified(user.UserId, now);
        }
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await editor.ResponseAsync(flow, module.Key, ct));
    }

    private static bool SameName(ApprovalFlow flow, ApprovalFlowDraftInput input) =>
        System.Text.Json.JsonSerializer.Serialize(flow.NameI18n) == System.Text.Json.JsonSerializer.Serialize(input.Name);
}

/// <summary>捨棄草稿，回到已發布的版本（J64）；DraftJson 改成只有新權杖（J65）；從未發布的流程沒有版本可回，要改用刪除流程（U64）。</summary>
public class DiscardApprovalFlowDraftHandler(
    IApprovalFlowRepository flows, IModuleRepository modules, ApprovalFlowEditor editor, ApprovalDraftGate gate)
    : IRequestHandler<DiscardApprovalFlowDraftRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DiscardApprovalFlowDraftRequest request, CancellationToken ct)
    {
        var flow = await flows.GetByKeyAsync(request.Key, ct);
        if (flow is null) return ServiceResult.NotFound("找不到流程");
        if (flow.CurrentVersionId is null) return ServiceResult.Conflict(ApprovalMessages.DraftOnlyUseDelete, "DraftOnlyUseDelete");
        if (!request.Force && request.BaseRev != ApprovalFlowDraft.TokenOf(flow.DraftJson)) return await gate.ConflictAsync(flow, ct);
        if (!await flows.TryReplaceDraftAsync(flow.Id, flow.DraftJson, flow.CurrentVersionId, ApprovalFlowDraft.TokenOnly(ApprovalFlowDraft.NewRev()), null, null, ct))
            return await gate.ConflictAfterLostRaceAsync(flow, ct);
        var moduleKey = (await modules.GetByIdAsync(flow.ModuleId, includeChildren: false, ct))!.Key;
        return ServiceResult.Success(await editor.ResponseAsync(flow, moduleKey, ct));
    }
}

/// <summary>
/// 發布（§10.8、J67）：以請求內容跑 §9 全部規則，通過才寫入版本並清掉草稿內容（DraftJson 只留新權杖）。交易內順序：讀 → 樂觀鎖邏輯比對 → 驗證
/// （不過 400、什麼都不寫、草稿保留）→ 條件更新清草稿內容並換新權杖（0 列回 409）→ 寫版本與 CurrentVersionId → 名稱與目標狀態 → 提交。
/// 版本規則：沒有版本（只有草稿）→ 建第 1 版、仍停用；定義有改且目前版本被用過 → 開新版；定義有改沒被用過 → 原地覆寫；
/// 定義沒改、只有版面 → 原地改寫目前版本的版面，被用過也不開新版（設計 J4）。
/// </summary>
public class PublishApprovalFlowHandler(
    IModuleRepository modules, IApprovalFlowRepository flows, ApprovalFlowEditor editor, ApprovalDraftGate gate,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<PublishApprovalFlowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PublishApprovalFlowRequest request, CancellationToken ct)
    {
        var flow = await flows.GetByKeyAsync(request.Key, ct);
        if (flow is null) return ServiceResult.NotFound("找不到流程");
        var input = request.Input;
        if (input.Key != flow.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = ["流程代碼不能更改"] });
        var module = (await modules.GetByIdAsync(flow.ModuleId, includeChildren: true, ct))!;
        if (ApprovalFlowEditor.CheckModule(module) is { } bad) return bad;
        if (ApprovalDraftRules.CheckFormat(input) is { } format) return format;

        var retargeted = input.ToOptionId != flow.ToOptionId;
        if (retargeted && await editor.CheckRetargetAsync(flow, input.ToOptionId, ct) is { } used) return used;
        if (await gate.CheckBaseAsync(flow, input, ct) is { } conflict) return conflict;

        var row = ApprovalDraftRules.RowFor(flow, input);
        var check = await editor.CheckAsync(module, row, flow, ct);
        if (check.Failure is { } invalid) return invalid;

        var layout = ApprovalDraftRules.LayoutOf(input);
        var current = flow.CurrentVersionId is { } vid ? await flows.GetVersionAsync(vid, ct) : null;
        // 請求沒帶版面就沿用目前版本的版面（與 PUT 一致，J73），只留下新定義裡還存在的節點
        var layoutJson = layout is null ? ApprovalDraftRules.PruneLayoutJson(current?.LayoutJson, input.Definition) : ApprovalFlowLayout.Prune(layout, input.Definition).ToJson();
        var usedVersion = current is not null && await flows.IsVersionUsedAsync(current.Id, ct);
        var definitionChanged = current is null || !ApprovalDefinitionJson.SameAs(current.Definition, input.Definition);

        // 清掉草稿內容但換一個新權杖：原地發布 CurrentVersionId 不變，沒有新權杖就會回到發布前的 (rev, versionId)（ABA，審查 🔴1）
        if (!await flows.TryReplaceDraftAsync(flow.Id, flow.DraftJson, flow.CurrentVersionId, ApprovalFlowDraft.TokenOnly(ApprovalFlowDraft.NewRev()), null, null, ct))
            return await gate.ConflictAfterLostRaceAsync(flow, ct);

        var now = time.Now;
        if (current is null || (definitionChanged && usedVersion))
        {
            var next = new ApprovalFlowVersion
            {
                FlowId = flow.Id, Version = await flows.MaxVersionAsync(flow.Id, ct) + 1, Definition = input.Definition, LayoutJson = layoutJson,
                CreatedBy = user.UserId, CreatedTime = now
            };
            flows.AddVersion(next);
            await uow.CommitAsync(ct);
            flow.CurrentVersionId = next.Id;
        }
        else
        {
            if (definitionChanged)
            {
                current.Definition = input.Definition;
                current.CreatedBy = user.UserId;
                current.CreatedTime = now;
            }
            current.LayoutJson = layoutJson;
        }
        flow.NameI18n = input.Name;
        flow.ToOptionId = input.ToOptionId;
        flow.Priority = row.Priority;
        flow.StampModified(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new ApprovalFlowPublishResponse(await editor.ResponseAsync(flow, module.Key, ct), check.Warnings));
    }
}
