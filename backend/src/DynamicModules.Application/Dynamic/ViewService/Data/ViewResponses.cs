using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ViewService.Data;

public record ViewResponse(
    long Id, I18nText Name, bool Shared, bool IsOwner, List<long> Columns, JsonElement? Filter, List<ViewSort> Sort,
    bool IsDefault, List<long> StaleFieldIds);

public static class ViewMapper
{
    /// <summary>
    /// 回應用：把已停用、我看不到、不可篩選、不可排序的欄位從 Columns、Filter、Sort 剔除；資料庫內容不改。
    /// StaleFieldIds 只列已停用、不再開放篩選或排序的欄位；我看不到的欄位（如沒有金額軸時的金額）靜靜剔除，不列入。
    /// 共用檢視沒有擁有者，以建立者視為 IsOwner。
    /// </summary>
    public static ViewResponse ToResponse(this ListView v, Module module, string userId, EffectivePermissions p)
    {
        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var stale = new List<long>();

        bool Active(long id) => fields.TryGetValue(id, out var f) && f.Status == FieldStatus.Active && p.CanSee(f);
        bool Hidden(long id) => fields.TryGetValue(id, out var f) && f.Status == FieldStatus.Active && !p.CanSee(f);
        void MarkStale(long id)
        {
            if (!Hidden(id)) stale.Add(id);
        }
        bool Filterable(long id) =>
            Active(id) && fields[id].Config.IsFilterable && FieldTypeCapabilities.SupportsFilter(fields[id].Type);
        bool Sortable(long id) =>
            Active(id) && fields[id].Config.IsSortable && FieldTypeCapabilities.SupportsSort(fields[id].Type);

        List<T> Keep<T>(List<T> items, Func<T, long> id, Func<long, bool> ok)
        {
            var kept = new List<T>();
            foreach (var item in items)
                if (ok(id(item))) kept.Add(item);
                else MarkStale(id(item));
            return kept;
        }

        var columns = Keep(v.Columns, id => id, Active);
        var sort = Keep(v.Sort, s => s.Field, Sortable);
        JsonElement? filter = null;
        if (v.Filter is { } raw)
        {
            var node = StripFilter(JsonNode.Parse(raw.GetRawText()), Filterable, MarkStale);
            filter = node is null ? null : JsonSerializer.SerializeToElement(node);
        }

        var isOwner = v.OwnerUserId is null ? v.CreatedUserId == userId : v.OwnerUserId == userId;
        return new(v.Id, v.NameI18n, v.OwnerUserId is null, isOwner, columns, filter, sort, v.IsDefault,
            stale.Distinct().ToList());
    }

    /// <summary>失效的條件整個移除，群組變空也移除；格式不認得的節點原樣保留（儲存時已驗過）。</summary>
    private static JsonNode? StripFilter(JsonNode? node, Func<long, bool> filterable, Action<long> markStale)
    {
        if (node is not JsonObject obj) return node;
        if (obj.Count == 1 && obj.First() is { Key: "all" or "any", Value: JsonArray children } group)
        {
            var kept = new JsonArray();
            foreach (var child in children.ToList())
                if (StripFilter(child, filterable, markStale) is { } k) kept.Add(k.DeepClone());
            return kept.Count == 0 ? null : new JsonObject { [group.Key] = kept };
        }

        if (obj["field"] is JsonValue f && f.TryGetValue<long>(out var id) && !filterable(id))
        {
            markStale(id);
            return null;
        }
        return obj;
    }
}
