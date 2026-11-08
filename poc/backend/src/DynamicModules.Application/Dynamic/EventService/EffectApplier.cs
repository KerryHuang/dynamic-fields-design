using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>某個事件被某筆來源記錄觸發後，要執行效果所需的一切。Source 是寫入後的來源值。</summary>
public sealed record EffectRun(
    ModuleEvent Event, Module SourceModule, long SourceRecordId, string OwnerUserId,
    IReadOnlyDictionary<string, JsonElement?> Source, int Depth);

/// <summary>執行事件的效果（設計 §4.1、§4.4、§6.5）：create、update、reverse、custom。失敗回傳非 null 的 ServiceResult，整筆請求回滾。</summary>
public class EffectApplier(
    IModuleRepository modules, IRecordRepository records, IEventFactRepository facts, ILedgerRepository ledgers,
    LedgerReverser reverser, IEffectHandlerRegistry handlers, EffectWriter writer, IOperationContext operation,
    Common.Time.IBusinessTime time, IUnitOfWork uow)
{
    public const int MaxEachRows = 200;

    public async Task<ServiceResult?> ApplyAsync(EffectRun run, CancellationToken ct)
    {
        foreach (var fx in run.Event.Definition.Effects)
        {
            var failure = fx.Kind switch
            {
                "create" => await CreateAsync(run, fx, ct),
                "update" => await UpdateAsync(run, fx, ct),
                "reverse" => await ReverseAsync(run, fx, ct),
                "custom" => await CustomAsync(run, fx, ct),
                _ => ServiceResult.Conflict($"未知的效果種類 {fx.Kind}")
            };
            if (failure is not null) return Wrap(run, fx, failure);
        }
        return null;
    }

    private static ServiceResult Wrap(EffectRun run, EventEffect fx, ServiceResult inner)
    {
        var reason = inner.Message ?? string.Join("；", inner.Errors.SelectMany(e => e.Value));
        var message = $"連動事件「{run.Event.Key}」的效果「{fx.Code}」失敗：{reason}";
        return inner.IsInvalid
            ? ServiceResult.Invalid(new Dictionary<string, string[]> { [RecordValueProcessor.RecordErrorKey] = [message] })
            : ServiceResult.Conflict(message);
    }

    // ---- create ----

    private async Task<ServiceResult?> CreateAsync(EffectRun run, EventEffect fx, CancellationToken ct)
    {
        var target = await modules.GetByKeyAsync(fx.TargetModuleKey!, includeChildren: true, ct);
        if (target is null || !target.IsWritable()) return ServiceResult.Conflict($"目標模組「{fx.TargetModuleKey}」未啟用或已關閉");

        var lines = new List<(long ItemKey, IReadOnlyDictionary<string, JsonElement?>? Values, Module? Module)> { (0, null, null) };
        if (!string.IsNullOrEmpty(fx.Each))
        {
            var (child, parentField) = (await modules.ListDetailModulesAsync(run.SourceModule.Key, ct)).FirstOrDefault(d => d.Child.Key == fx.Each);
            if (child is null) return ServiceResult.Conflict($"來源沒有明細模組「{fx.Each}」");
            var childModule = (await modules.GetByIdAsync(child.Id, includeChildren: true, ct))!;
            var rows = (await records.ListByRefAsync(parentField.Id, run.SourceRecordId.ToString(CultureInfo.InvariantCulture), ct))
                .Where(r => !r.DeleteFlag).OrderBy(r => r.Id).ToList();
            if (rows.Count > MaxEachRows) return ServiceResult.Conflict($"明細列超過 {MaxEachRows} 列，無法產生連動記錄");
            lines = rows.Select(r => (r.Id, (IReadOnlyDictionary<string, JsonElement?>?)RecordValueProcessor.Parse(r.ValuesJson), (Module?)childModule)).ToList();
        }

        foreach (var (itemKey, lineValues, lineModule) in lines)
        {
            if (await facts.FindAsync(run.Event.Key, fx.Code, run.SourceRecordId, itemKey, ct) is not null) continue;   // 已經記過，不重複
            var (values, error) = BuildValues(run, fx, target, lineModule, lineValues);
            if (error is not null) return ServiceResult.Conflict(error);

            var created = await writer.CreateAsync(run.OwnerUserId, target.Key, values, null, ct);
            if (!created.IsSuccess) return created;
            AddFact(run, fx, itemKey, ((RecordService.Data.DynamicRecordResponse)created.Data!).Id);
            await CommitFactsAsync(ct);
        }
        return null;
    }

    private (Dictionary<string, JsonElement?> Values, string? Error) BuildValues(
        EffectRun run, EventEffect fx, Module target, Module? lineModule, IReadOnlyDictionary<string, JsonElement?>? lineValues)
    {
        var fields = target.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var values = new Dictionary<string, JsonElement?>();
        foreach (var set in fx.Set ?? [])
        {
            if (!fields.TryGetValue(set.To, out var to)) return (values, $"目標欄位 {set.To} 已不存在");
            JsonElement? value;
            if (set.From is { } from)
                value = (from.Side == "line" ? lineValues : run.Source)?.GetValueOrDefault($"f_{from.FieldId}");
            else if (set.Formula is { } formula)
            {
                var side = formula.Side == "line" ? lineModule : run.SourceModule;
                var parsed = FormulaParser.Parse(formula.Expr, side ?? run.SourceModule);
                if (parsed.Node is null) return (values, $"公式不正確：{parsed.Error}");
                value = FormulaEvaluator.Evaluate(parsed.Node, side ?? run.SourceModule,
                    (formula.Side == "line" ? lineValues : run.Source) ?? run.Source, time.Today, to.Config.Scale);
            }
            else
                value = set.Const is { ValueKind: JsonValueKind.String } c && c.GetString() == "today" && to.Type == FieldType.Date
                    ? JsonSerializer.SerializeToElement(time.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    : set.Const;
            if (value is { ValueKind: JsonValueKind.Null }) value = null;
            values[to.Key] = value;
        }
        return (values, null);
    }

    // ---- update ----

    private async Task<ServiceResult?> UpdateAsync(EffectRun run, EventEffect fx, CancellationToken ct)
    {
        if (fx.Via is not { } via || !run.Source.TryGetValue($"f_{via}", out var reference) || reference is not { ValueKind: not JsonValueKind.Null })
            return null;   // 參照為空：略過，不算錯誤
        var idText = reference.Value.ValueKind == JsonValueKind.Number ? reference.Value.GetRawText() : reference.Value.GetString();
        if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId)) return null;

        var target = await modules.GetByKeyAsync(fx.TargetModuleKey!, includeChildren: true, ct);
        if (target is null || !target.IsWritable()) return ServiceResult.Conflict($"目標模組「{fx.TargetModuleKey}」未啟用或已關閉");
        var (values, error) = BuildValues(run, fx, target, null, null);
        if (error is not null) return ServiceResult.Conflict(error);
        return await writer.UpdateAsync(targetId, values, ct) is { IsSuccess: false } failed ? failed : null;
    }

    // ---- reverse ----

    private async Task<ServiceResult?> ReverseAsync(EffectRun run, EventEffect fx, CancellationToken ct)
    {
        var target = await modules.GetByKeyAsync(fx.TargetModuleKey!, includeChildren: true, ct);
        if (target is null || !target.IsWritable()) return ServiceResult.Conflict($"目標模組「{fx.TargetModuleKey}」未啟用或已關閉");
        var ledger = await ledgers.GetByModuleAsync(target.Id, ct);
        if (ledger is null) return ServiceResult.Conflict($"模組「{target.Key}」不是分錄，無法沖銷");

        var created = await facts.ListByEffectAsync(fx.OfEvent!, fx.OfEffect!, run.SourceRecordId, ct);
        foreach (var fact in created)
        {
            if (await facts.FindAsync(run.Event.Key, fx.Code, run.SourceRecordId, fact.TargetRecordId, ct) is not null) continue;
            var original = await records.GetAsync(fact.TargetRecordId, ct);
            if (original is null || await ledgers.FindReversalOfAsync(original.Id, ct) is not null) continue;   // 已沖銷過就略過

            var plan = await reverser.PlanAsync(target, ledger, original, null, null, ct);
            if (plan.Failed) return plan.Conflict is not null ? ServiceResult.Conflict(plan.Conflict) : ServiceResult.Invalid(plan.Errors);
            var reversed = await writer.CreateAsync(run.OwnerUserId, target.Key, plan.Values, plan.Details.Count > 0 ? plan.Details : null, ct);
            if (!reversed.IsSuccess) return reversed;
            var reversalId = ((RecordService.Data.DynamicRecordResponse)reversed.Data!).Id;
            ledgers.AddReversal(new LedgerReversal { OriginalRecordId = original.Id, ReversalRecordId = reversalId, CreatedTime = time.Now });
            AddFact(run, fx, original.Id, reversalId);
            await CommitFactsAsync(ct);
        }
        return null;
    }

    // ---- custom ----

    private async Task<ServiceResult?> CustomAsync(EffectRun run, EventEffect fx, CancellationToken ct)
    {
        var handler = handlers.Find(fx.Handler);
        if (handler is null) return ServiceResult.Conflict($"找不到外掛處理器「{fx.Handler}」");
        var context = new EffectContext(run.SourceModule.Key, run.SourceRecordId, run.Source, fx.Args, operation.OperationId, run.Depth,
            writer.Restrict(handler.DeclareWrites(fx.Args), run.OwnerUserId));
        var result = await handler.ApplyAsync(context, ct);
        return result.Error is null ? null : ServiceResult.Conflict(result.Error);
    }

    // ---- facts ----

    private void AddFact(EffectRun run, EventEffect fx, long itemKey, long targetRecordId) =>
        facts.Add(new EventFact
        {
            EventKey = run.Event.Key, EffectCode = fx.Code, SourceRecordId = run.SourceRecordId, ItemKey = itemKey,
            TargetRecordId = targetRecordId, OperationId = operation.OperationId, CreatedTime = time.Now
        });

    private async Task CommitFactsAsync(CancellationToken ct)
    {
        try
        {
            await uow.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("連動事件的事實重複（同一個事件對同一筆來源已經產生過連動記錄）", e);
        }
    }
}
