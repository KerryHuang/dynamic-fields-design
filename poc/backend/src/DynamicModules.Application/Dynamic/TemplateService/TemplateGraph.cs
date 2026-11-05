using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.TemplateService;

/// <summary>
/// 範本內欄位的依賴、鎖定與計算順序；欄位一律用限定名 &lt;模組代號&gt;.&lt;欄位代號&gt;。
/// 只收解析得到的引用；引用不存在由 TemplateValidator 回報。
/// </summary>
public sealed partial class TemplateGraph
{
    private readonly Dictionary<string, List<string>> _dependsOn;
    private readonly HashSet<string> _locked;
    private readonly List<string> _computedOrder;

    private TemplateGraph(Dictionary<string, List<string>> dependsOn, HashSet<string> locked, List<string> computedOrder, bool hasCycle)
    {
        _dependsOn = dependsOn;
        _locked = locked;
        _computedOrder = computedOrder;
        HasCycle = hasCycle;
    }

    /// <summary>計算欄位（formula、rollup）之間有循環依賴；循環上的欄位不會出現在 ComputedOrder。</summary>
    public bool HasCycle { get; }

    /// <summary>公式引用的欄位；彙總的來源參照、彙總對象與條件欄位；parentDefaultField 的表頭欄位。依首次出現排序。</summary>
    public IReadOnlyList<string> DependsOn(string qualifiedField) =>
        _dependsOn.TryGetValue(qualifiedField, out var deps) ? deps : [];

    /// <summary>不能在「從範本建立」時取消勾選：各模組的標題欄位、relation = detail 的參照欄位。</summary>
    public bool IsLocked(string qualifiedField) => _locked.Contains(qualifiedField);

    /// <summary>計算欄位依依賴拓撲排序（被依賴者在前），同層照宣告順序。</summary>
    public IReadOnlyList<string> ComputedOrder() => _computedOrder;

    public static TemplateGraph Build(TemplatePackage package)
    {
        var fields = AllFields(package);
        var dependsOn = new Dictionary<string, List<string>>();
        var locked = new HashSet<string>();

        foreach (var module in package.Modules)
        {
            if (module.TitleField is not null) locked.Add(Qualify(module.Code, module.TitleField));
            foreach (var field in module.Blocks.SelectMany(b => b.Fields))
            {
                var name = Qualify(module.Code, field.Code);
                if (IsDetailReference(field)) locked.Add(name);
                var deps = new List<string>();
                void Add(string q) { if (fields.ContainsKey(q) && !deps.Contains(q)) deps.Add(q); }

                if (FormulaOf(field) is { } formula)
                    foreach (var code in FormulaCodes(formula)) Add(Qualify(module.Code, code));

                if (RollupOf(field) is { } rollup)
                {
                    var source = ConfigString(rollup, "sourceField");
                    if (source is not null) Add(source);
                    if (ConfigString(rollup, "aggregateField") is { } aggregate) Add(aggregate);
                    if (SplitQualified(source) is { } src && ConfigObject(rollup, "condition") is { } condition)
                        foreach (var leaf in ConditionLeaves(condition))
                        {
                            if (LeafField(leaf) is { } f) Add(Qualify(src.Module, f));
                            if (LeafFieldRef(leaf) is { } r) Add(Qualify(src.Module, r));
                        }
                }

                if (ConfigString(field.Config, "parentDefaultField") is { } parent) Add(parent);
                if (!dependsOn.ContainsKey(name)) dependsOn[name] = deps;
            }
        }

        var (order, hasCycle) = TopologicalOrder(fields, dependsOn);
        return new TemplateGraph(dependsOn, locked, order, hasCycle);
    }

    private static (List<string>, bool) TopologicalOrder(
        Dictionary<string, TemplateField> fields, Dictionary<string, List<string>> dependsOn)
    {
        var computed = fields.Where(kv => kv.Value.Type is FieldType.Formula or FieldType.Rollup).Select(kv => kv.Key).ToList();
        var computedSet = computed.ToHashSet();
        var pending = computed.ToDictionary(c => c, c => DependsOnOf(c).Count(computedSet.Contains));
        var order = new List<string>();
        var progress = true;
        while (progress)
        {
            progress = false;
            foreach (var name in computed)
            {
                if (pending[name] != 0) continue;
                order.Add(name);
                pending[name] = -1;
                foreach (var other in computed)
                    if (pending[other] > 0) pending[other] -= DependsOnOf(other).Count(d => d == name);
                progress = true;
                break; // 每次從頭找，讓同層維持宣告順序
            }
        }
        return (order, order.Count != computed.Count);

        List<string> DependsOnOf(string name) => dependsOn.GetValueOrDefault(name) ?? [];
    }

    internal static string Qualify(string? module, string? field) => $"{module}.{field}";

    internal static (string Module, string Field)? SplitQualified(string? qualified)
    {
        var dot = qualified?.IndexOf('.') ?? -1;
        return dot <= 0 || dot == qualified!.Length - 1 ? null : (qualified[..dot], qualified[(dot + 1)..]);
    }

    /// <summary>限定名對欄位；代號重複時以第一個為準（重複由 TemplateValidator 回報）。</summary>
    internal static Dictionary<string, TemplateField> AllFields(TemplatePackage package)
    {
        var fields = new Dictionary<string, TemplateField>();
        foreach (var module in package.Modules)
            foreach (var field in module.Blocks.SelectMany(b => b.Fields))
                fields.TryAdd(Qualify(module.Code, field.Code), field);
        return fields;
    }

    internal static bool IsDetailReference(TemplateField field) =>
        field.Type == FieldType.Reference && ConfigString(field.Config, "relation") == "detail";

    // 範本裡「引用」的唯一定義：TemplateValidator、TemplateInstantiator 都用這裡的，不各自判斷。

    /// <summary>公式裡的 {代號}。</summary>
    [GeneratedRegex(@"\{([^{}]*)\}")]
    internal static partial Regex FormulaReference();

    /// <summary>下拉欄位值裡的 opt_&lt;選項代號&gt;（只對 enum、multienum 的值才算選項引用）。</summary>
    [GeneratedRegex(@"^opt_([a-z][a-z0-9_-]*)$")]
    internal static partial Regex OptionReference();

    internal static IEnumerable<string> FormulaCodes(string formula) =>
        FormulaReference().Matches(formula).Select(m => m.Groups[1].Value);

    /// <summary>只有公式欄位的 formula 是引用；其他型別帶 formula 由 TemplateValidator 擋下。</summary>
    internal static string? FormulaOf(TemplateField field) =>
        field.Type == FieldType.Formula ? ConfigString(field.Config, "formula") : null;

    /// <summary>只有彙總欄位的 rollup 是引用；其他型別帶 rollup 由 TemplateValidator 擋下。</summary>
    internal static JsonElement? RollupOf(TemplateField field) =>
        field.Type == FieldType.Rollup ? ConfigObject(field.Config, "rollup") : null;

    internal static bool IsOptionField(TemplateField field) => field.Type is FieldType.Enum or FieldType.MultiEnum;

    internal static string? ConfigString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static JsonElement? ConfigObject(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    /// <summary>遞迴走 all／any 群組，回傳所有葉節點（非群組的物件）。</summary>
    internal static IEnumerable<JsonElement> ConditionLeaves(JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.Object) yield break;
        var props = condition.EnumerateObject().ToList();
        if (props.Count == 1 && props[0].Name is "all" or "any")
        {
            if (props[0].Value.ValueKind != JsonValueKind.Array) yield break;
            foreach (var child in props[0].Value.EnumerateArray())
                foreach (var leaf in ConditionLeaves(child)) yield return leaf;
            yield break;
        }
        yield return condition;
    }

    internal static string? LeafField(JsonElement leaf) => ConfigString(leaf, "field");

    internal static string? LeafFieldRef(JsonElement leaf) =>
        ConfigObject(leaf, "value") is { } value ? ConfigString(value, "fieldRef") : null;
}
