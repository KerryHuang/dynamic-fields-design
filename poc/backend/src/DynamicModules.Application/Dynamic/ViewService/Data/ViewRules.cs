using System.Text.Json;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentValidation;

namespace DynamicModules.Application.Dynamic.ViewService.Data;

public static class ViewRules
{
    public const string ViewNotFound = "檢視不存在";
    public const int MaxColumns = 50;
    public const int MaxSorts = 3;

    /// <summary>名稱、欄位與排序的數量和方向；跨模組 metadata 的檢查在 <see cref="Check"/>。</summary>
    public static void ValidateShape<T>(
        this AbstractValidator<T> v, System.Linq.Expressions.Expression<Func<T, I18nText>> name,
        System.Linq.Expressions.Expression<Func<T, List<long>>> columns,
        System.Linq.Expressions.Expression<Func<T, List<ViewSort>>> sort)
    {
        v.RuleFor(name).ValidLabel();
        v.RuleFor(columns).Must(c => c is { Count: >= 1 and <= MaxColumns }).WithMessage("欄位數量必須是 1 到 50 個");
        v.RuleFor(sort).Must(s => s is { Count: <= MaxSorts }).WithMessage("排序欄位最多 3 個")
            .Must(s => s is null || s.All(x => x is { Order: "asc" or "desc" })).WithMessage("排序方向只能是 asc 或 desc");
    }

    /// <summary>依模組 metadata 檢查欄位、篩選、排序；回傳錯誤，沒有錯誤時為空。看不到的欄位（<paramref name="isVisible"/>）視同不存在。</summary>
    public static Dictionary<string, string[]> Check(
        Module module, FieldTypeRegistry registry, IReadOnlySet<string> userIds,
        List<long> columns, JsonElement? filter, List<ViewSort> sort, Func<Field, bool>? isVisible = null)
    {
        var errors = new Dictionary<string, string[]>();
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => isVisible?.Invoke(f) ?? true).ToList();

        var badColumn = columns.Cast<long?>()
            .FirstOrDefault(id => !fields.Any(f => f.Id == id && f.Status == FieldStatus.Active));
        if (badColumn is { } c) errors["Columns"] = [$"欄位 {c} 不存在或未啟用"];

        if (filter is { ValueKind: not JsonValueKind.Null } json)
        {
            var parsed = ConditionParser.Parse(json, module, registry, ConditionUsage.ListFilter, userIds, isVisible);
            if (parsed.Error is not null) errors["Filter"] = [parsed.Error];
        }

        var badSort = sort.FirstOrDefault(s => !fields.Any(f =>
            f.Id == s.Field && f.Status == FieldStatus.Active && f.Config.IsSortable
            && FieldTypeCapabilities.SupportsSort(f.Type)));
        if (badSort is not null) errors["Sort"] = [$"欄位 {badSort.Field} 不存在、未啟用或未開放排序"];
        return errors;
    }

    /// <summary>檢視（欄位、篩選、排序）是否引用了呼叫端看不到的欄位；直接掃儲存的內容，不依賴解析。</summary>
    public static bool ReferencesHiddenField(ListView v, Module module, Func<Field, bool> isVisible)
    {
        var hidden = module.Blocks.SelectMany(b => b.Fields).Where(f => !isVisible(f)).Select(f => f.Id).ToHashSet();
        if (hidden.Count == 0) return false;
        return v.Columns.Any(hidden.Contains) || v.Sort.Any(s => hidden.Contains(s.Field))
               || (v.Filter is { } f && FilterReferences(f, hidden));
    }

    private static bool FilterReferences(JsonElement el, HashSet<long> ids)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array: return el.EnumerateArray().Any(x => FilterReferences(x, ids));
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Name == "field" && prop.Value.TryGetInt64(out var id) && ids.Contains(id)) return true;
                    if (prop.Name is "all" or "any" && FilterReferences(prop.Value, ids)) return true;
                }
                return false;
            default: return false;
        }
    }

    /// <summary>JSON null 與沒有篩選一律存成 null。</summary>
    public static JsonElement? NormalizeFilter(JsonElement? filter) =>
        filter is { ValueKind: JsonValueKind.Null } ? null : filter;

    /// <summary>同一個模組、同一個範圍的其他預設檢視改成非預設（在呼叫端的交易內）。</summary>
    public static async Task ClearDefaultsAsync(
        this IListViewRepository views, long moduleId, string? owner, long? except, string userId, DateTime now,
        CancellationToken ct)
    {
        foreach (var other in await views.ListByScopeAsync(moduleId, owner, ct))
            if (other.IsDefault && other.Id != except)
            {
                other.IsDefault = false;
                other.StampModified(userId, now);
            }
    }
}
