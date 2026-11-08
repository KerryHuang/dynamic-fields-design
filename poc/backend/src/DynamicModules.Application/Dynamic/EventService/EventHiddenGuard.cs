using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Formulas;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>
/// 儲存欄位權限時的守衛（設計 §4.2「事後隱藏」）：要設為隱藏的欄位若被某個事件的 from／formula 引用，
/// 而該對應沒有 allowHiddenSource，拒絕儲存（fail-closed）。
/// </summary>
public partial class EventHiddenGuard(IEventRepository events, IModuleRepository modules)
{
    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex FieldRefPattern();

    /// <summary>回傳第一個違規的說明；沒有回 null。</summary>
    public async Task<string?> FindViolationAsync(Module module, IReadOnlySet<long> hiding, CancellationToken ct)
    {
        if (hiding.Count == 0) return null;
        var all = await events.ListAsync(ct);
        foreach (var e in all)
        {
            Module? source = null;
            foreach (var fx in e.Definition.Effects)
            {
                source ??= await modules.GetByIdAsync(e.SourceModuleId, includeChildren: true, ct);
                if (source is null) break;
                foreach (var s in fx.Set ?? [])
                {
                    if (s.AllowHiddenSource) continue;
                    var used = await UsedAsync(s, fx, source, ct);
                    foreach (var (usedModule, fieldId) in used)
                        if (usedModule.Id == module.Id && hiding.Contains(fieldId))
                        {
                            var name = module.Blocks.SelectMany(b => b.Fields).First(f => f.Id == fieldId).LabelI18n.Resolve("zh-TW");
                            return $"事件「{e.Key}」的效果「{fx.Code}」複製了欄位「{name}」；請先到該事件的對應項明確允許複製被隱藏的值（allowHiddenSource），或移除這個引用";
                        }
                }
            }
        }
        return null;
    }

    /// <summary>一個 set 對應引用的 (模組, 欄位 Id)；與 EventValidator 同一套解析（公式用 FormulaParser）。</summary>
    public async Task<List<(Module Module, long FieldId)>> UsedAsync(EventSet s, EventEffect fx, Module source, CancellationToken ct)
    {
        var result = new List<(Module, long)>();
        var side = s.From?.Side ?? s.Formula?.Side;
        if (side is null) return result;
        var module = side == "line" && !string.IsNullOrEmpty(fx.Each) ? await modules.GetByKeyAsync(fx.Each, includeChildren: true, ct) : source;
        if (module is null) return result;
        if (s.From is { } from) result.Add((module, from.FieldId));
        else if (s.Formula is { } formula)
        {
            // 公式因欄位被停用等原因解析失敗時 FieldRefs 是空的；守衛 fail-closed，一律另從原文抓 {欄位 Id}，
            // 否則「先停用、再隱藏、再啟用」可以繞過（重新啟用後隱藏值就流進目標）。
            var ids = FormulaParser.Parse(formula.Expr, module).FieldRefs.Concat(
                FieldRefPattern().Matches(formula.Expr).Select(m => long.Parse(m.Groups[1].Value))).Distinct();
            foreach (var id in ids) result.Add((module, id));
        }
        return result;
    }
}
