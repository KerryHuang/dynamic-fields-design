using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService.Data;

/// <summary>
/// 依觀看者遮蔽儲存的定義：條件值（filters 與量值 condition）只要欄位、via 或參照目標觀看者看不到，就拿掉 value 並標 hidden，
/// 否則回傳定義會讓人由條件值推測隱藏欄位的內容（spec §7）。擁有者也套同一邏輯。
/// </summary>
public static class ReportMasking
{
    /// <param name="set">用 lenient 模式載入（沒權限的模組不在其中）。</param>
    public static JsonElement Mask(string definitionJson, ReportDefinition d, ReportModuleSet set)
    {
        var root = JsonNode.Parse(definitionJson)!;
        if (root is JsonObject obj)
        {
            if (obj["filters"] is JsonArray filters)
                for (var i = 0; i < filters.Count && i < d.Filters.Count; i++)
                    if (filters[i] is JsonObject f && !Visible(d.Filters[i].Field, set)) Hide(f);
            if (obj["measures"] is JsonArray measures)
                for (var i = 0; i < measures.Count && i < d.Measures.Count; i++)
                    if (d.Measures[i].Condition is { } c && measures[i]?["condition"] is JsonObject co && !Visible(c.Field, set)) Hide(co);
        }
        return JsonSerializer.Deserialize<JsonElement>(root.ToJsonString());
    }

    private static void Hide(JsonObject o)
    {
        o.Remove("value");
        o["hidden"] = true;
    }

    private static bool Visible(ReportFieldRef r, ReportModuleSet set)
    {
        var side = r.Source == "main" ? set.Main : r.Source == "child" ? set.Child : null;
        if (side is null) return false;
        Field? Find(Module m, long id) => m.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == id && f.Status == FieldStatus.Active);

        if (r.ViaFieldId is null) return FieldVisible(Find(side.Module, r.FieldId), side, set);
        var via = Find(side.Module, r.ViaFieldId.Value);
        if (via is not { Config.RefTarget: { } target } || !side.Perms.CanSee(via)) return via is null;
        if (target.Kind == ReferenceTarget.EntityKind) return true;
        return set.Targets.TryGetValue(target.Key, out var t) && FieldVisible(Find(t.Module, r.FieldId), t, set);
    }

    /// <summary>找不到的欄位沒有內容可洩漏（視為可見）；參照欄位還要目標可讀且標題可見，否則條件結果會洩漏目標內容。</summary>
    private static bool FieldVisible(Field? f, ReportSide side, ReportModuleSet set)
    {
        if (f is null) return true;
        if (!side.Perms.CanSee(f)) return false;
        if (f.Type != FieldType.Reference || f.Config.RefTarget is not { Kind: ReferenceTarget.ModuleKind } target) return true;
        return set.Targets.TryGetValue(target.Key, out var t) && t.Module.TitleVisibleTo(t.Perms);
    }
}
