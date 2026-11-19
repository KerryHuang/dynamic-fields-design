using System.Diagnostics;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.RulePause;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 規則失敗的計數與客戶設定事件（維運設計 §17.2.7）：排在 Validation 之後、ApprovalAlert 之前（兩者都在交易之外），只處理最外層請求
/// （外層已開交易表示巢狀，比照 <see cref="ApprovalAlertBehavior{TReq,TRes}"/>）。
/// <list type="bullet">
/// <item>例外帶任務規則標記、而且 <see cref="RuleFailureClassifier"/> 判為歸屬規則本身：另開 DI scope 送 <see cref="RecordRuleFailureRequest"/>
/// （業務交易已回滾），然後原樣丟出原例外；補記失敗只記 Error log，不改變回給使用者的錯誤。不論歸屬與否都移除在途登記。</item>
/// <item>結果是失敗、而 <see cref="RuleFailureNote"/> 有已知錯誤碼：同樣另開 scope 開或更新客戶設定事件，不計數、不暫停。</item>
/// </list>
/// 這兩種寫入的行為者都是「系統」（使用者只是剛好觸發了規則；稽核 <c>taskRule.autoPause</c> 行為者種類記系統）。
/// </summary>
public class RuleFailureBehavior<TReq, TRes>(
    ITransactionManager transactions, RuleFailureNote notes, RuleFailureClassifier classifier, RuleFailureInFlight inFlight, IServiceScopeFactory scopes,
    ILogger<RuleFailureBehavior<TReq, TRes>> logger) : IPipelineBehavior<TReq, TRes> where TReq : notnull
{
    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (transactions.IsActive || request is RecordRuleFailureRequest or RecordRuleConfigIssuesRequest) return await next();
        TRes response;
        try
        {
            response = await next();
        }
        catch (Exception e)
        {
            await RecordFailureAsync(e, ct);
            throw;
        }
        if (response is ServiceResult { IsSuccess: false } && notes.Entries.Count > 0)
        {
            var entries = notes.Entries.ToList();
            notes.Clear();
            await SendAsync(new RecordRuleConfigIssuesRequest(entries, RequestId()), "規則已知錯誤碼的客戶設定事件", ct);
        }
        return response;
    }

    private async Task RecordFailureAsync(Exception e, CancellationToken ct)
    {
        var (kind, id) = RuleFailureTag.Read(e);
        if (kind != RuleFailureTag.TaskRule || id is not { } ruleId || !FirstTime(e)) return;
        var evaluated = RuleFailureTag.ReadEvaluatedUtc(e);
        try
        {
            if (evaluated is { } at && classifier.IsAttributable(e))
                await SendAsync(new RecordRuleFailureRequest(kind, ruleId, IncidentTitles.ShortName(e.GetType().Name), at, RequestId()),
                    $"任務規則 {ruleId} 的失敗計數", ct);
        }
        finally
        {
            if (evaluated is { } at) inFlight.Remove(ruleId, at);
        }
    }

    /// <summary>另開 scope（失敗請求的 DbContext 可能還留著沒存的修改或壞掉的連線狀態）；寫不進去只記 Error log。</summary>
    private async Task SendAsync(IRequest<ServiceResult> request, string what, CancellationToken ct)
    {
        try
        {
            using var system = ActingScope.Begin("");
            await using var scope = scopes.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, CancellationToken.None) is { IsSuccess: false } failed)
                logger.LogError("{What}寫入失敗：{Result}", what, failed.Message ?? failed.Code);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{What}寫入失敗", what);
        }
    }

    private const string RecordedKey = "formwright.ruleFailureRecorded";

    /// <summary>
    /// 同一個例外只補記一次：外層不是交易型請求時，內層交易型請求也算「最外層」，例外會穿過兩層這個 behavior（E12）。
    /// </summary>
    private static bool FirstTime(Exception e)
    {
        try
        {
            if (e.Data.Contains(RecordedKey)) return false;
            e.Data[RecordedKey] = true;
        }
        catch (Exception)
        {
            // Data 唯讀：放棄去重
        }
        return true;
    }

    private static string? RequestId() => Activity.Current?.TraceId.ToHexString();
}
