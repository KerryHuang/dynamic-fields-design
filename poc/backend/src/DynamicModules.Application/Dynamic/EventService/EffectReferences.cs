using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>事件、檢查、分錄定義引用了哪些模組與欄位；安全刪除與模組移除共用，確保盤點和清除是同一份判斷。</summary>
public static partial class EffectReferences
{
    public sealed record Refs(HashSet<string> ModuleKeys, HashSet<long> FieldIds);

    [GeneratedRegex(@"""field(?:Ref)?""\s*:\s*(\d+)")]
    private static partial Regex ConditionField();

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex FormulaField();

    private static void AddConditionFields(JsonElement? json, HashSet<long> ids)
    {
        if (json is null or { ValueKind: JsonValueKind.Null }) return;
        foreach (Match m in ConditionField().Matches(json.Value.GetRawText())) ids.Add(long.Parse(m.Groups[1].Value));
    }

    public static Refs Of(ModuleEvent e, string sourceModuleKey)
    {
        var keys = new HashSet<string> { sourceModuleKey };
        var fields = new HashSet<long>();
        var d = e.Definition;
        foreach (var w in d.Trigger.Watch ?? []) fields.Add(w);
        AddConditionFields(d.When, fields);
        foreach (var fx in d.Effects)
        {
            if (fx.TargetModuleKey is not null) keys.Add(fx.TargetModuleKey);
            if (!string.IsNullOrEmpty(fx.Each)) keys.Add(fx.Each);
            if (fx.Via is { } via) fields.Add(via);
            foreach (var s in fx.Set ?? [])
            {
                fields.Add(s.To);
                if (s.From is { } f) fields.Add(f.FieldId);
                if (s.Formula is { } fo)
                    foreach (Match m in FormulaField().Matches(fo.Expr ?? "")) fields.Add(long.Parse(m.Groups[1].Value));
            }
        }
        return new Refs(keys, fields);
    }

    public static HashSet<long> Of(ModuleCheck c)
    {
        var d = c.Definition;
        var fields = new HashSet<long>(d.GroupBy ?? []);
        if (d.FieldId is { } f) fields.Add(f);
        if (d.Assert?.Limit is { } l)
        {
            if (l.Field is { } lf) fields.Add(lf);
            if (l.Via is { } lv) fields.Add(lv);
        }
        AddConditionFields(d.Where, fields);
        return fields;
    }

    public static HashSet<long> Of(ModuleLedger l)
    {
        var d = l.Definition;
        var fields = new HashSet<long> { d.ReversalFieldId };
        foreach (var n in d.Negate) fields.Add(n);
        foreach (var p in d.Swap) foreach (var s in p) fields.Add(s);
        return fields;
    }
}
