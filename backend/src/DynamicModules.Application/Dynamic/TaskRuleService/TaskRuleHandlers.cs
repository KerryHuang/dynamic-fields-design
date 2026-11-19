using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.RulePause;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

/// <summary>規則增改共用：套用輸入、算回應。</summary>
public sealed class TaskRuleEditor(IAppRepository apps)
{
    /// <summary>驗證通過的 opt_ 值（opt_05、opt_+5）改寫成記錄裡的標準格式 opt_{id}，比對記錄值時才會相等。</summary>
    private static JsonElement? CanonicalValue(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s && s.StartsWith("opt_", StringComparison.Ordinal)
            && long.TryParse(s.AsSpan(4), out var optionId)
            ? JsonSerializer.SerializeToElement($"opt_{optionId}")
            : value;

    public static void Apply(TaskRule rule, TaskRuleInput input)
    {
        rule.NameI18n = input.Name;
        rule.Active = input.Active;
        rule.Repeat = input.Repeat;
        // 只有指定值觸發才用欄位與值，其他觸發存成 null，避免留下沒用的引用
        rule.Trigger = input.Trigger.On == "onFieldBecomes"
            ? new TaskRuleTrigger(input.Trigger.On, input.Trigger.FieldId, CanonicalValue(input.Trigger.Value))
            : new TaskRuleTrigger(input.Trigger.On, null, null);
        rule.WhenJson = TaskRuleMapping.WhenText(input.When);

        // 以任務代碼對應既有範本原地更新（避免刪了再加撞到 (RuleId, Code) 唯一索引），沒有的移除、新的加入
        var codes = input.Tasks.Select(t => t.Code).ToHashSet();
        foreach (var gone in rule.Tasks.Where(t => !codes.Contains(t.Code)).ToList()) rule.Tasks.Remove(gone);
        for (var i = 0; i < input.Tasks.Count; i++)
        {
            var t = input.Tasks[i];
            var task = rule.Tasks.FirstOrDefault(x => x.Code == t.Code);
            if (task is null) rule.Tasks.Add(task = new TaskRuleTask { Code = t.Code });
            task.SortOrder = i;
            task.TitleI18n = t.Title;
            task.DescriptionI18n = t.Description is { Values.Count: > 0 } d && d.Values.Values.Any(v => !string.IsNullOrWhiteSpace(v)) ? d : null;
            task.AssigneeFieldId = t.AssigneeFieldId;
            task.DueFieldId = t.DueFieldId;
            task.DueOffsetDays = t.DueOffsetDays;
            task.WhenJson = TaskRuleMapping.WhenText(t.When);
        }
    }

    public async Task<string?> AppKeyAsync(long? appId, CancellationToken ct) =>
        appId is null ? null : (await apps.ListAsync(ct)).FirstOrDefault(a => a.Id == appId)?.Key;

    public static IReadOnlySet<long> FieldIds(Module module) =>
        module.Blocks.SelectMany(b => b.Fields).Select(f => f.Id).ToHashSet();
}

public class GetTaskRulesHandler(IModuleRepository modules, ITaskRuleRepository rules, TaskRuleEditor editor, IRuleHealthRepository health)
    : IRequestHandler<GetTaskRulesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetTaskRulesRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(TaskRuleMessages.ModuleNotFound);
        var fieldIds = TaskRuleEditor.FieldIds(module);
        var items = new List<TaskRuleResponse>();
        var list = await rules.ListByModuleAsync(module.Id, ct);
        var rows = (await health.ListAsync(RuleFailureTag.TaskRule, list.Select(r => r.Id).ToList(), ct)).ToDictionary(h => h.RuleId);
        foreach (var r in list)
            items.Add(r.ToResponse(module.Key, await editor.AppKeyAsync(r.AppId, ct), fieldIds).WithHealth(rows.GetValueOrDefault(r.Id)));
        return ServiceResult.Success((IReadOnlyList<TaskRuleResponse>)items);
    }
}

public class CreateTaskRuleHandler(
    IModuleRepository modules, ITaskRuleRepository rules, TaskRuleValidator validator, TaskRuleEditor editor,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<CreateTaskRuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateTaskRuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(TaskRuleMessages.ModuleNotFound);
        if (TaskRuleValidator.CheckModule(module) is { } bad) return bad;
        var input = request.Input;
        if (await validator.ValidateAsync(module, input, null, ct) is { } invalid) return invalid;
        if (await rules.GetByKeyAsync(input.Key, ct) is not null) return ServiceResult.Conflict(TaskRuleMessages.KeyExists);

        var rule = new TaskRule { Key = input.Key, ModuleId = module.Id, AppId = request.AppId };
        TaskRuleEditor.Apply(rule, input);
        rule.StampCreated(user.UserId, time.Now);
        rules.Add(rule);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(rule.ToResponse(module.Key, await editor.AppKeyAsync(rule.AppId, ct), TaskRuleEditor.FieldIds(module)));
    }
}

public class UpdateTaskRuleHandler(
    IModuleRepository modules, ITaskRuleRepository rules, TaskRuleValidator validator, TaskRuleEditor editor,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time, IRuleHealthRepository health)
    : IRequestHandler<UpdateTaskRuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateTaskRuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(TaskRuleMessages.ModuleNotFound);
        var rule = await rules.GetByKeyAsync(request.Key, ct);
        if (rule is null || rule.ModuleId != module.Id) return ServiceResult.NotFound(TaskRuleMessages.RuleNotFound);
        var input = request.Input;
        if (input.Key != rule.Key) return ServiceResult.Invalid(new Dictionary<string, string[]> { ["key"] = [TaskRuleMessages.KeyImmutable] });
        if (TaskRuleValidator.CheckModule(module) is { } bad) return bad;
        if (await validator.ValidateAsync(module, input, rule, ct) is { } invalid) return invalid;

        TaskRuleEditor.Apply(rule, input);
        rule.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        // 改規則不會解除暫停（要按恢復，§17.2.7）；回應照清單帶暫停狀態
        return ServiceResult.Success(rule.ToResponse(module.Key, await editor.AppKeyAsync(rule.AppId, ct), TaskRuleEditor.FieldIds(module))
            .WithHealth(await health.GetAsync(RuleFailureTag.TaskRule, rule.Id, ct)));
    }
}

public class DeleteTaskRuleHandler(IModuleRepository modules, ITaskRuleRepository rules, IUnitOfWork uow, RulePauseCleanup rulePause)
    : IRequestHandler<DeleteTaskRuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteTaskRuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound(TaskRuleMessages.ModuleNotFound);
        var rule = await rules.GetByKeyAsync(request.Key, ct);
        if (rule is null || rule.ModuleId != module.Id) return ServiceResult.NotFound(TaskRuleMessages.RuleNotFound);
        // 健康列沒有外鍵：同交易刪掉，暫停事件以 ruleDeleted 結案（維運設計 §17.2.7）
        await rulePause.RulesDeletedAsync([rule.Id], ct);
        rules.Remove(rule);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

/// <summary>
/// 恢復自動暫停的規則（維運設計 §17.2.7）：條件式更新（<c>WHERE PausedUtc IS NOT NULL</c>）清暫停、記恢復時間與人、計數歸零、
/// <c>StreakResetUtc</c> 推到現在、刪掉失敗列；暫停事件以 <c>ruleResumed</c> 結案；稽核摘要帶略過次數與暫停起點。
/// 兩人同時按，第二個回 409 <c>ruleNotPaused</c>。
/// </summary>
public class ResumeTaskRuleHandler(
    IModuleRepository modules, ITaskRuleRepository rules, TaskRuleEditor editor, IRuleHealthRepository health, RulePauseCleanup incidents,
    AuditContext audit, ICurrentUser user, TimeProvider clock)
    : IRequestHandler<ResumeTaskRuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ResumeTaskRuleRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(TaskRuleMessages.ModuleNotFound);
        var rule = await rules.GetByKeyAsync(request.Key, ct);
        if (rule is null || rule.ModuleId != module.Id) return ServiceResult.NotFound(TaskRuleMessages.RuleNotFound);
        var before = await health.GetAsync(RuleFailureTag.TaskRule, rule.Id, ct);
        var now = StorageTime.ToMicroseconds(clock.GetUtcNow().UtcDateTime);
        if (before?.PausedUtc is null || !await health.TryResumeAsync(RuleFailureTag.TaskRule, rule.Id, now, user.UserId, ct))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.TaskRuleNotPaused), "ruleNotPaused");
        await incidents.ResolvePausedAsync([rule.Id], IncidentResolvedReason.RuleResumed, user.UserId, ct);
        audit.Detail("pausedUtc", before.PausedUtc);
        audit.Detail("skippedWhilePaused", before.SkippedWhilePaused);
        audit.Detail("pauseCount", before.PauseCount);
        return ServiceResult.Success(rule.ToResponse(module.Key, await editor.AppKeyAsync(rule.AppId, ct), TaskRuleEditor.FieldIds(module))
            .WithHealth(await health.GetAsync(RuleFailureTag.TaskRule, rule.Id, ct)));
    }
}
