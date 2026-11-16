using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService;

/// <summary>列表與匯出共用：把 filter／sort／q 與資料範圍組成 RecordQuery；錯誤寫進 errors 並回 null。</summary>
public static class RecordQueryBuilder
{
    private const int MaxSorts = 3;
    private const int MaxKeywordLength = 100;
    private const string MalformedFilter = "篩選條件格式錯誤";

    public static async Task<RecordQuery?> BuildAsync(
        IModuleRepository modules, FieldTypeRegistry registry, Module module, EffectivePermissions perms,
        string? filterText, IReadOnlyList<string> sortBy, IReadOnlyList<string> sortOrder, string? q,
        DateOnly today, int page, int size, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        var filter = await ParseFilter(modules, registry, filterText, module, perms, errors, ct);
        var sorts = ParseSorts(sortBy, sortOrder, module, perms, errors);
        var keyword = string.IsNullOrEmpty(q) || !module.TitleVisibleTo(perms) ? null : q;
        if (keyword is { Length: > MaxKeywordLength }) errors["q"] = ["關鍵字最多 100 個字元"];
        if (errors.Count > 0) return null;
        return new RecordQuery(module.Id, filter, sorts, keyword, today, page, size, perms.OwnerUserIds);
    }

    private static async Task<ConditionNode?> ParseFilter(
        IModuleRepository modules, FieldTypeRegistry registry, string? filter, Module module, EffectivePermissions perms,
        Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(filter);
            json = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            errors["filter"] = [MalformedFilter];
            return null;
        }

        var result = ConditionParser.Parse(json, module, registry, ConditionUsage.ListFilter, await modules.ListUserIdsAsync(ct), perms.CanSee);
        if (result.Error is not null) errors["filter"] = [result.Error];
        return result.Node;
    }

    private static List<RecordSort> ParseSorts(
        IReadOnlyList<string> by, IReadOnlyList<string> order, Module module, EffectivePermissions perms, Dictionary<string, string[]> errors)
    {
        var sorts = new List<RecordSort>();
        if (by.Count > MaxSorts) errors["sortBy"] = ["排序欄位最多 3 個"];
        else if (by.Count != order.Count) errors["sortBy"] = ["sortBy 與 sortOrder 數量必須相同"];
        else if (order.Any(o => o is not ("asc" or "desc"))) errors["sortBy"] = ["排序方向只能是 asc 或 desc"];
        else
        {
            var fields = module.Blocks.SelectMany(b => b.Fields).ToList();
            for (var i = 0; i < by.Count; i++)
            {
                var field = fields.FirstOrDefault(f => f.Key == by[i] && f.Status == FieldStatus.Active && perms.CanSee(f) && f.Config.IsSortable);
                // 有標題樣板時，列表的標題欄就是組好的標題：依 Record.Title 排序，標題看不到的人不能拿它排序
                var byTitle = field is not null && module.TitleTemplate is not null && field.Id == module.TitleFieldId;
                if (field is null || (byTitle && !module.TitleVisibleTo(perms)))
                {
                    errors["sortBy"] = [$"欄位 {by[i]} 不存在或未開放排序"];
                    break;
                }
                sorts.Add(new RecordSort(field, order[i] == "desc", byTitle));
            }
        }
        return sorts;
    }
}
