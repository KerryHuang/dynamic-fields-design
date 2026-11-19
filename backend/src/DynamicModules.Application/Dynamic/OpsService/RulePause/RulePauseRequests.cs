using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.RulePause;

/// <summary>
/// 補記一次歸屬規則本身的失敗（維運設計 §17.2.7 <c>RecordRuleFailureRequest</c>）：由 <c>RuleFailureBehavior</c> 在業務交易回滾之後、
/// 另開 scope 送出。<paramref name="FailedUtc"/> 是規則的評估時間（不是補記時間）。Data 是 bool：這次是否觸發了暫停。
/// </summary>
public sealed record RecordRuleFailureRequest(string RuleKind, long RuleId, string ErrorCode, DateTime FailedUtc, string? RequestId)
    : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>
/// 一個交易、兩種資料庫同一份（§17.2.7 第 1–5 步、§17.7）：第一句是健康列的條件式更新（取得列鎖，同一規則的並行失敗與 reset 在 PostgreSQL 上排隊），
/// 沒有列才新增、撞主鍵重做；失敗不晚於 <c>StreakResetUtc</c> 的不進窗口；清掉窗口外與最近一次成功之前的失敗列；窗口內列數達門檻時
/// 以 <c>WHERE PausedUtc IS NULL</c> 暫停，影響 1 列的交易在同一交易寫暫停事件、站內通知、寄送佇列與稽核 <c>taskRule.autoPause</c>。
/// 規則已被刪除時什麼都不寫。
/// </summary>
public class RecordRuleFailureHandler(
    IRuleHealthRepository health, ITaskRuleRepository rules, IModuleRepository modules, IPolicyReader policies, RuleIncidents incidents,
    IAuditLog audit, IUnitOfWork uow, IDatabaseErrors errors, TimeProvider clock) : IRequestHandler<RecordRuleFailureRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RecordRuleFailureRequest r, CancellationToken ct)
    {
        if (r.RuleKind != RuleFailureTag.TaskRule || await rules.GetByIdAsync(r.RuleId, ct) is not { } rule) return ServiceResult.Success(false);
        var failed = StorageTime.ToMicroseconds(r.FailedUtc);
        var code = Cut(r.ErrorCode, 100)!;
        var requestId = Cut(r.RequestId, 32);

        // 1. 先更新（取得列鎖）；沒有列才新增，撞主鍵（別人剛新增）重做
        for (var attempt = 1; ; attempt++)
        {
            if (await health.BumpFailureAsync(r.RuleKind, r.RuleId, failed, code, requestId, ct) > 0) break;
            var row = new RuleHealth
            {
                RuleKind = r.RuleKind, RuleId = r.RuleId, ConsecutiveFailures = 1, LastFailureUtc = failed, LastErrorCode = code, LastRequestId = requestId,
            };
            health.Add(row);
            try
            {
                await uow.CommitAsync(ct);
                break;
            }
            catch (Exception e) when (errors.IsUniqueViolation(e) && attempt < 3)
            {
                health.Detach(row);
            }
        }

        // 2. 不晚於最近一次已記下的成功：只留在第 1 步的欄位，不進窗口
        var current = (await health.GetAsync(r.RuleKind, r.RuleId, ct))!;
        var reset = current.StreakResetUtc;
        if (reset is { } at && failed <= at) return ServiceResult.Success(false);
        health.AddFailure(new RuleFailure { RuleKind = r.RuleKind, RuleId = r.RuleId, FailedUtc = failed, RequestId = requestId });
        await uow.CommitAsync(ct);

        // 3、4. 清掉用不到的失敗列，數窗口內的（滑動窗口，O2-U12）。窗口的終點是目前已記下的最晚失敗，不是這次：
        // 補記可能晚於評估順序（較晚的失敗先補記完），以這次為終點會漏數已記下、比這次晚的那筆，兩邊都數到門檻減一而漏掉暫停（E05）
        var window = await policies.IntAsync(PolicyCatalog.OpsRulePauseWindowMinutes, null, ct);
        var threshold = await policies.IntAsync(PolicyCatalog.OpsRulePauseConsecutiveFailures, null, ct);
        var latest = current.LastFailureUtc is { } last && last > failed ? last : failed;
        var from = latest.AddMinutes(-window);
        await health.PruneFailuresAsync(r.RuleKind, r.RuleId, from, reset, ct);
        var count = await health.CountFailuresAsync(r.RuleKind, r.RuleId, from, latest, reset, ct);
        if (count < threshold) return ServiceResult.Success(false);

        // 5. 暫停：暫停時間用補記當下（暫停真正生效的時間）
        var now = StorageTime.ToMicroseconds(clock.GetUtcNow().UtcDateTime);
        if (!await health.TryPauseAsync(r.RuleKind, r.RuleId, now, ct)) return ServiceResult.Success(false);
        var moduleKey = (await modules.GetByIdAsync(rule.ModuleId, includeChildren: false, ct))?.Key ?? "";
        await incidents.PausedAsync(rule, moduleKey, current.LastErrorCode, threshold, count, now, requestId, ct);
        audit.Add(new AuditEntry("taskRule.autoPause", AuditResult.Success, AuditTargets.TaskRule, rule.Key,
            Summary: new AuditSummary().Add("failures", count).Add("threshold", threshold).Add("windowMinutes", window)
                .Add("lastErrorCode", current.LastErrorCode).ToJson()));
        await uow.CommitAsync(ct);
        return ServiceResult.Success(true);
    }

    private static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
}

/// <summary>事件與檢查的已知錯誤碼開或更新客戶設定事件（§17.2.2；不計數、不暫停）。由 <c>RuleFailureBehavior</c> 在失敗請求回滾之後送出。</summary>
public sealed record RecordRuleConfigIssuesRequest(IReadOnlyList<RuleFailureNoteEntry> Notes, string? RequestId) : IRequest<ServiceResult>, ITransactionalRequest;

public class RecordRuleConfigIssuesHandler(RuleIncidents incidents) : IRequestHandler<RecordRuleConfigIssuesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RecordRuleConfigIssuesRequest request, CancellationToken ct)
    {
        foreach (var note in request.Notes) await incidents.KnownErrorAsync(note, request.RequestId, ct);
        return ServiceResult.Success();
    }
}
