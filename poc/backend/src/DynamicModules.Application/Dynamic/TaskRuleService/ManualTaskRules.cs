using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.TaskRuleService;

/// <summary>手動套用的規則：啟用中、觸發方式是 manual、沒有應用程式或應用程式開啟中。不保留跨呼叫的狀態。</summary>
public sealed class ManualTaskRules(ITaskRuleRepository rules, ITaskRepository tasks, IAppRepository apps, IModuleRepository modules,
    FieldTypeRegistry registry, IBusinessTime time)
{
    public async Task<List<TaskRule>> ListUsableAsync(long moduleId, CancellationToken ct)
    {
        var list = (await rules.ListActiveByModuleAsync(moduleId, ct)).Where(r => r.Trigger.On == "manual").ToList();
        if (!list.Any(r => r.AppId is not null)) return list;
        var open = (await apps.ListAsync(ct)).Where(a => a.Status == AppStatus.Open).Select(a => a.Id).ToHashSet();
        return list.Where(r => r.AppId is not { } appId || open.Contains(appId)).ToList();
    }

    public bool Applicable(TaskRule rule, Module module, IReadOnlyDictionary<string, JsonElement?> values, IReadOnlySet<string> userIds) =>
        TaskConditions.Holds(rule.WhenJson, module, values, registry, time.Today, userIds);

    /// <summary>Applied：once 已套用過；PreviousOpen：everyTime 還有未完成的任務；Applicable：規則條件成立。</summary>
    public async Task<IReadOnlyList<ManualRuleResponse>> ListAsync(Module module, Record record, CancellationToken ct)
    {
        var usable = await ListUsableAsync(module.Id, ct);
        if (usable.Count == 0) return [];
        var values = RecordValueProcessor.Parse(record.ValuesJson);
        var userIds = await modules.ListUserIdsAsync(ct);
        var result = new List<ManualRuleResponse>(usable.Count);
        foreach (var rule in usable)
        {
            var latest = await rules.GetLatestOccurrenceAsync(rule.Id, record.Id, ct);
            var previousOpen = rule.Repeat == TaskRuleRepeat.EveryTime && await tasks.AnyOpenByRuleAndRecordAsync(rule.Id, record.Id, ct);
            result.Add(new ManualRuleResponse(rule.Key, rule.NameI18n, rule.Repeat,
                rule.Repeat == TaskRuleRepeat.Once && latest is not null, Applicable(rule, module, values, userIds), previousOpen));
        }
        return result;
    }
}

/// <summary>
/// 看不到記錄 404；模組不可寫 409；規則不存在、停用、不是 manual、應用程式已關閉 404；條件不符 409；
/// once 已套用、everyTime 上一輪未完成 409；其餘產生下一批（第一次是 1）。同時送出時由唯一索引擋下第二次。
/// </summary>
public class ApplyTaskRuleHandler(
    TaskAccess access, ManualTaskRules manual, ITaskRuleRepository rules, ITaskRepository tasks, IModuleRepository modules,
    IPermissionRepository permissions, TaskRuleApplier applier, TaskWriter writer, TaskSupport support)
    : IRequestHandler<ApplyTaskRuleRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyTaskRuleRequest request, CancellationToken ct)
    {
        var target = await access.ForRecordAsync(request.RecordId, ct);
        if (target is null) return ServiceResult.NotFound(RecordMapper.RecordNotFound);
        if (!target.CanAdd) return ServiceResult.Conflict(Common.Security.ModuleGate.ClosedMessage);
        var rule = (await manual.ListUsableAsync(target.Module.Id, ct)).FirstOrDefault(r => r.Key == request.RuleKey);
        if (rule is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.TaskRuleNotFound));

        var values = RecordValueProcessor.Parse(target.Record.ValuesJson);
        var userIds = await modules.ListUserIdsAsync(ct);
        if (!manual.Applicable(rule, target.Module, values, userIds)) return ServiceResult.Conflict(MessageRef.Of(MessageText.TaskRuleNotApplicable));
        var latest = await rules.GetLatestOccurrenceAsync(rule.Id, target.Record.Id, ct);
        if (rule.Repeat == TaskRuleRepeat.Once && latest is not null) return ServiceResult.Conflict(MessageRef.Of(MessageText.TaskRuleApplied));
        if (rule.Repeat == TaskRuleRepeat.EveryTime && await tasks.AnyOpenByRuleAndRecordAsync(rule.Id, target.Record.Id, ct))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.TaskRulePreviousOpen));

        var activeUsers = (await permissions.ListUsersAsync(ct)).Where(u => u.IsActive).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        await applier.GenerateAsync(rule, target.Module, target.Record.Id, (latest ?? 0) + 1, target.Record.Title, values, activeUsers, userIds, ct);
        try
        {
            await writer.FlushAsync(ct);
        }
        catch (DbUpdateException e) when (TaskRuleConflicts.IsUnique(e))
        {
            return ServiceResult.Conflict(MessageRef.Of(MessageText.TaskRuleAlreadyRun));
        }
        return ServiceResult.Success(await support.RecordTasksAsync(target, ct));
    }
}
