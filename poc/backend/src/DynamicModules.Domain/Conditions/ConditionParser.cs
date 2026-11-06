using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.FieldTypes.Handlers;

namespace DynamicModules.Domain.Conditions;

/// <summary>條件的用途；Rule 不看 IsFilterable，另支援 fieldRef 與 textarea 的空值運算子。</summary>
public enum ConditionUsage { ListFilter, Rule }

public sealed record ConditionParseResult(ConditionNode? Node, string? Error);

/// <summary>把 §8 條件 JSON 解析成 AST 並依模組 metadata 驗證；純函式，不碰資料庫。</summary>
public static class ConditionParser
{
    private const int MaxConditions = 20;
    private const int MaxDepth = 3;
    private const int MaxListItems = 50;
    private const int MaxDays = 3650;

    private static readonly Dictionary<string, FilterOperator> OperatorsByName =
        Enum.GetValues<FilterOperator>().ToDictionary(o => char.ToLowerInvariant(o.ToString()[0]) + o.ToString()[1..]);

    private sealed class ParseException(string message) : Exception(message);

    /// <param name="isVisible">呼叫端看不到的欄位視同不存在（在型別、運算子、可篩選檢查之前），不洩漏欄位存在；null 表示全部可見。</param>
    /// <param name="userIds">user 欄位的有效使用者 id；Domain 沒有 repository，由呼叫端（handler）提供。</param>
    public static ConditionParseResult Parse(
        JsonElement json, Module module, FieldTypeRegistry registry, ConditionUsage usage,
        IReadOnlySet<string> userIds, Func<Field, bool>? isVisible = null)
    {
        var ctx = new Context(module, registry, usage, userIds, isVisible);
        try
        {
            var node = ParseNode(json, ctx, depth: 0);
            return new ConditionParseResult(node, null);
        }
        catch (ParseException e)
        {
            return new ConditionParseResult(null, e.Message);
        }
    }

    private sealed class Context(Module module, FieldTypeRegistry registry, ConditionUsage usage, IReadOnlySet<string> userIds, Func<Field, bool>? isVisible)
    {
        public ConditionUsage Usage { get; } = usage;
        public Module Module { get; } = module;
        public Func<Field, bool>? IsVisible { get; } = isVisible;
        public FieldTypeRegistry Registry { get; } = registry;
        public IReadOnlySet<string> UserIds { get; } = userIds;
        public int Count { get; set; }
    }

    /// <summary>回 null 代表空群組（沒有條件）。</summary>
    private static ConditionNode? ParseNode(JsonElement el, Context ctx, int depth)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new ParseException("篩選條件格式錯誤");
        var props = el.EnumerateObject().ToList();

        if (props.Count == 1 && props[0].Name is "all" or "any")
        {
            var group = props[0];
            if (group.Value.ValueKind != JsonValueKind.Array) throw new ParseException("篩選條件格式錯誤");
            if (depth + 1 > MaxDepth) throw new ParseException("篩選條件最多 3 層");
            var children = new List<ConditionNode>();
            foreach (var child in group.Value.EnumerateArray())
                if (ParseNode(child, ctx, depth + 1) is { } node) children.Add(node);
            return children.Count == 0 ? null : new ConditionGroup(group.Name == "all", children);
        }

        return ParseCondition(el, props, ctx);
    }

    private static FieldCondition ParseCondition(JsonElement el, List<JsonProperty> props, Context ctx)
    {
        if (props.Any(p => p.Name is not ("field" or "op" or "value" or "fieldRef")))
            throw new ParseException("篩選條件格式錯誤");
        if (++ctx.Count > MaxConditions) throw new ParseException("篩選條件最多 20 個");

        if (!el.TryGetProperty("field", out var fieldEl) || fieldEl.ValueKind != JsonValueKind.Number
            || !fieldEl.TryGetInt64(out var id)
            || !el.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
            throw new ParseException("篩選條件格式錯誤");

        var hasValue = el.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Null;
        var isRule = ctx.Usage == ConditionUsage.Rule;
        var hasRef = hasValue && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("fieldRef", out _);
        if (el.TryGetProperty("fieldRef", out _) || (hasRef && !isRule))
            throw new ParseException(isRule ? "篩選條件格式錯誤" : "列表篩選不支援 fieldRef");

        var field = FindField(ctx, id) ?? throw new ParseException($"欄位 {id} 不存在或未啟用");
        if (!isRule && (!field.Config.IsFilterable || !FieldTypeCapabilities.SupportsFilter(field.Type)))
            throw new ParseException($"欄位 {id} 未開放篩選");

        var opName = opEl.GetString()!;
        var handler = ctx.Registry.Get(field.Type);
        var allowed = isRule && field.Type == FieldType.Textarea
            ? [FilterOperator.IsEmpty, FilterOperator.IsNotEmpty]
            : handler.OperatorsFor(field);
        if (!OperatorsByName.TryGetValue(opName, out var op) || !allowed.Contains(op))
            throw new ParseException($"欄位 {id} 不支援運算子 {opName}");

        var bad = new ParseException($"欄位 {id} 的條件值格式錯誤");
        if (hasRef) return ParseFieldRef(field, op, opName, value, ctx, bad);

        var (values, days) = ParseValue(field, handler, op, hasValue ? value : null, ctx, bad);
        return new FieldCondition(field, op, values, days);
    }

    private static Field? FindField(Context ctx, long id) =>
        ctx.Module.Blocks.SelectMany(b => b.Fields)
            .FirstOrDefault(f => f.Id == id && f.Status == FieldStatus.Active && (ctx.IsVisible?.Invoke(f) ?? true));

    private static readonly HashSet<FilterOperator> RefOperators =
        [FilterOperator.Eq, FilterOperator.Ne, FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Lt, FilterOperator.Lte];

    private static FieldCondition ParseFieldRef(
        Field field, FilterOperator op, string opName, JsonElement value, Context ctx, ParseException bad)
    {
        if (!RefOperators.Contains(op)) throw new ParseException($"欄位 {field.Id} 不支援運算子 {opName}");
        if (value.EnumerateObject().Count() != 1 || !value.TryGetProperty("fieldRef", out var r)
            || r.ValueKind != JsonValueKind.Number || !r.TryGetInt64(out var refId))
            throw bad;
        var refField = FindField(ctx, refId) ?? throw new ParseException($"欄位 {refId} 不存在或未啟用");
        var kind = IndexKindOf(field);
        if (kind is null || kind != IndexKindOf(refField))
            throw new ParseException($"欄位 {field.Id} 的 fieldRef 型別不符");
        return new FieldCondition(field, op, [], null, refField);
    }

    private enum IndexKind { Number, Date, Text }

    /// <summary>依型別的 ToIndex 會填 NumberValue、DateValue 或 TextValue 分類；選項、使用者等不可比較者回 null。</summary>
    private static IndexKind? IndexKindOf(Field f) => f.Type switch
    {
        FieldType.Integer or FieldType.Decimal or FieldType.Currency or FieldType.Percent or FieldType.Boolean => IndexKind.Number,
        FieldType.Formula or FieldType.Rollup => f.Config.ResultType == "date" ? IndexKind.Date : IndexKind.Number,
        FieldType.Date or FieldType.DateTime => IndexKind.Date,
        FieldType.Text or FieldType.Email or FieldType.Phone or FieldType.Url or FieldType.Time or FieldType.AutoNumber => IndexKind.Text,
        _ => null
    };

    private static (IReadOnlyList<IndexValue>, int?) ParseValue(
        Field field, IFieldTypeHandler handler, FilterOperator op, JsonElement? value, Context ctx, ParseException bad)
    {
        switch (op)
        {
            case FilterOperator.IsEmpty or FilterOperator.IsNotEmpty:
                return value is null ? ([], null) : throw bad;

            case FilterOperator.WithinNextDays or FilterOperator.WithinLastDays:
                if (value is not { ValueKind: JsonValueKind.Number } n || !n.TryGetInt32(out var days)
                    || days is < 0 or > MaxDays)
                    throw bad;
                return ([], days);

            case FilterOperator.Contains or FilterOperator.StartsWith:
                if (value is not { ValueKind: JsonValueKind.String } s) throw bad;
                var text = s.GetString()!;
                if (text.Length is < 1 or > HandlerHelpers.MaxIndexTextLength) throw bad;
                return ([new IndexValue(0, text, null, null, null)], null);

            case FilterOperator.In or FilterOperator.NotIn:
                if (value is not { ValueKind: JsonValueKind.Array } list) throw bad;
                var items = list.EnumerateArray().ToList();
                if (items.Count is < 1 or > MaxListItems) throw bad;
                return (items.Select(e => Index(field, ElementHandler(field, handler, ctx), e, ctx, bad)).ToList(), null);

            case FilterOperator.Between:
                if (value is not { ValueKind: JsonValueKind.Array } pair || pair.GetArrayLength() != 2) throw bad;
                return (pair.EnumerateArray().Select(e => Index(field, handler, e, ctx, bad)).ToList(), null);

            default:
                return value is { } v ? ([Index(field, handler, v, ctx, bad)], null) : throw bad;
        }
    }

    /// <summary>multienum 的 in／notIn：清單的每個元素是單一選項，用 enum 語意正規化。</summary>
    private static IFieldTypeHandler ElementHandler(Field field, IFieldTypeHandler handler, Context ctx) =>
        field.Type == FieldType.MultiEnum ? ctx.Registry.Get(FieldType.Enum) : handler;

    /// <summary>系統計算欄位的 Normalize 一律回 null，篩選值改用其值型別的 handler 驗證格式。</summary>
    private static IFieldTypeHandler NormalizerFor(Field field, IFieldTypeHandler handler, Context ctx) => handler.Type switch
    {
        FieldType.Formula or FieldType.Rollup => ctx.Registry.Get(field.Config.ResultType == "date" ? FieldType.Date : FieldType.Decimal),
        FieldType.AutoNumber => ctx.Registry.Get(FieldType.Text),
        _ => handler
    };

    /// <summary>選項用「全部選項」（active 與 all 相同），讓停用的選項也能篩選。</summary>
    private static IndexValue Index(Field field, IFieldTypeHandler handler, JsonElement value, Context ctx, ParseException bad)
    {
        var ids = field.Options.Select(o => o.Id).ToHashSet();
        var seq = field.Options.ToDictionary(o => o.Id, o => o.Sequence);
        var result = NormalizerFor(field, handler, ctx).Normalize(value, field, new FieldTypeContext(ids, ids, seq, null, ctx.UserIds, EnforceWriteLimits: false));
        if (!result.IsValid || result.Value is null) throw bad;
        return handler.ToIndex(result.Value.Value, field).FirstOrDefault() ?? throw bad;
    }
}
