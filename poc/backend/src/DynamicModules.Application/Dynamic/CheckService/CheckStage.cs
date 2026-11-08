using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.CheckService;

/// <summary>
/// 寫入後的跨記錄驗證（設計 §2 步驟 10、§5.2）：對本次請求寫過的每一筆記錄，取「寫入前」與「寫入後」兩個狀態，
/// 符合 where 的狀態各自決定一個組，去重後逐組聚合比對。互斥由 SQLite 的 BEGIN IMMEDIATE 提供（設計 §7），
/// 這裡只負責「在同一個交易、寫完之後」求值。
/// </summary>
public class CheckStage(
    IWriteJournal journal, IModuleRepository modules, ICheckRepository checks, IUnitOfWork uow, CheckEvaluator evaluator) : IWriteStage
{
    public const int MaxGroups = 500;
    public const string TooBig = "數值太大，無法精確加總（此欄位被跨記錄驗證引用）";

    public int Order => 10;

    public async Task<ServiceResult?> RunAsync(CancellationToken ct)
    {
        var merged = JournalView.Merge(journal.Entries);
        if (merged.Count == 0) return null;

        var violations = new List<string>();
        var flushed = false;
        var groupCount = 0;
        foreach (var byModule in merged.GroupBy(e => e.ModuleId))
        {
            var active = await checks.ListActiveByModuleAsync(byModule.Key, ct);
            if (active.Count == 0) continue;
            if (!flushed)
            {
                await uow.CommitAsync(ct);   // 先把本次請求的變更刷進資料庫，聚合才看得到
                flushed = true;
            }
            var module = (await modules.GetByIdAsync(byModule.Key, includeChildren: true, ct))!;
            foreach (var check in active)
            {
                var (prepared, failure) = await evaluator.PrepareAsync(module, check, ct);
                if (failure is not null) return failure;

                if (TooLarge(prepared!, byModule) is { } big) return ServiceResult.Invalid(
                    new Dictionary<string, string[]> { [RecordValueProcessor.RecordErrorKey] = [big] });

                // 寫入後的狀態先收：上限若讀自本筆記錄（field／via），以「現在」的值為準，同組的寫入前狀態不再求值
                // （否則把上限改小時，會拿舊上限比較而放行）。同組多筆記錄的上限不同時各自求值。
                var groups = new Dictionary<string, CheckGroup>();
                var afterIds = new HashSet<string>();
                foreach (var entry in byModule)
                    if (!entry.AfterDeleted && evaluator.GroupOf(prepared!, RecordValueProcessor.Parse(entry.AfterValues)) is { } after)
                    {
                        afterIds.Add(after.Id);
                        groups.TryAdd(after.Id + "\u0000" + LimitKey(prepared!.Check.Definition.Assert.Limit, after), after);
                    }
                foreach (var entry in byModule)
                    if (entry.BeforeValues is not null && !entry.BeforeDeleted && entry.Kind == JournalKind.Modified
                        && evaluator.GroupOf(prepared!, RecordValueProcessor.Parse(entry.BeforeValues)) is { } before
                        && !afterIds.Contains(before.Id))
                        groups.TryAdd(before.Id + "\u0000" + LimitKey(prepared!.Check.Definition.Assert.Limit, before), before);
                if ((groupCount += groups.Count) > MaxGroups)
                    return ServiceResult.Conflict($"這次請求牽涉的檢查組數超過 {MaxGroups}，請分批處理");

                foreach (var group in groups.Values)
                {
                    var outcome = await evaluator.EvaluateAsync(prepared!, group, ct);
                    if (outcome.Failure is not null) return outcome.Failure;
                    if (outcome.Violation is not null && !violations.Contains(outcome.Violation)) violations.Add(outcome.Violation);
                }
            }
        }
        return violations.Count == 0
            ? null
            : ServiceResult.Invalid(new Dictionary<string, string[]> { [RecordValueProcessor.RecordErrorKey] = [.. violations] });
    }

    private static string LimitKey(CheckLimit limit, CheckGroup group)
    {
        var field = limit.Via ?? limit.Field;
        if (limit.Const is not null || field is not { } id) return "";
        return group.Source.TryGetValue($"f_{id}", out var v) && v is { } e ? e.GetRawText() : "";
    }

    /// <summary>被加總的欄位放大後超出 64 位元整數：寫入時就擋（fail-closed），不悄悄少算。</summary>
    private static string? TooLarge(CheckEvaluator.Prepared p, IEnumerable<JournalEntry> entries)
    {
        if (p.Check.Definition is not { Function: "sum", FieldId: { } fid } || !p.Fields.TryGetValue(fid, out var field)) return null;
        foreach (var entry in entries.Where(e => !e.AfterDeleted))
        {
            var values = RecordValueProcessor.Parse(entry.AfterValues);
            if (values.TryGetValue(field.Key, out var v) && v is { ValueKind: System.Text.Json.JsonValueKind.Number }
                && ScaledNumber.Of(field, v.Value) is null)
                return $"{field.LabelI18n.Resolve("zh-TW")}：{TooBig}";
        }
        return null;
    }
}
