using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Formulas;

/// <summary>公式求值：一律用 decimal；任一運算元沒有值、除以零、溢位都回 null。</summary>
public static class FormulaEvaluator
{
    private const int DefaultScale = 2;
    private const decimal MaxDays = 3_000_000m;

    /// <param name="scale">數字結果的小數位數；呼叫端傳欄位的 Config.Scale，null 用預設 2。日期結果不進位。</param>
    public static JsonElement? Evaluate(
        FormulaNode node, Module module, IReadOnlyDictionary<string, JsonElement?> values, DateOnly today,
        int? scale = null)
    {
        var fields = new Dictionary<long, Field>();
        foreach (var f in module.Blocks.SelectMany(b => b.Fields).Where(f => f.Status == FieldStatus.Active))
            fields.TryAdd(f.Id, f);
        object? result;
        try { result = Eval(node, fields, values, today); }
        catch (OverflowException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }

        return result switch
        {
            decimal d => ToJson(Math.Round(d, Math.Clamp(scale ?? DefaultScale, 0, 28), MidpointRounding.AwayFromZero)),
            DateOnly date => JsonSerializer.SerializeToElement(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            _ => null,
        };
    }

    private static JsonElement ToJson(decimal d) =>
        JsonDocument.Parse(d.ToString("0.############################", CultureInfo.InvariantCulture)).RootElement.Clone();

    private static object? Eval(
        FormulaNode node, Dictionary<long, Field> fields, IReadOnlyDictionary<string, JsonElement?> values, DateOnly today)
    {
        switch (node)
        {
            case NumberNode n: return n.Value;
            case TodayNode: return today;
            case FieldRefNode r: return fields.TryGetValue(r.FieldId, out var rf) ? ReadField(rf, values) : null;
            case NegateNode neg: return Eval(neg.Operand, fields, values, today) is decimal v ? -v : null;
            case BinaryNode b:
                if (Eval(b.Left, fields, values, today) is not decimal l
                    || Eval(b.Right, fields, values, today) is not decimal r2) return null;
                return b.Op switch
                {
                    '+' => l + r2,
                    '-' => l - r2,
                    '*' => l * r2,
                    _ => r2 == 0 ? null : (object)(l / r2),
                };
            case RoundNode rn:
                return Eval(rn.Value, fields, values, today) is decimal rv
                    ? Math.Round(rv, rn.Digits, MidpointRounding.AwayFromZero) : null;
            case IfNode i:
                return Eval(i.Condition, fields, values, today) is bool cond
                    ? Eval(cond ? i.Then : i.Else, fields, values, today) : null;
            case CompareNode c:
                var cl = Eval(c.Left, fields, values, today);
                var cr = Eval(c.Right, fields, values, today);
                if (cl is null || cr is null) return null;
                var cmp = cl is decimal dl ? dl.CompareTo((decimal)cr) : ((DateOnly)cl).CompareTo((DateOnly)cr);
                return c.Op switch
                {
                    "=" => cmp == 0, "<>" => cmp != 0, "<" => cmp < 0,
                    "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0,
                };
            case DateDiffNode dd:
                return Eval(dd.Left, fields, values, today) is DateOnly a && Eval(dd.Right, fields, values, today) is DateOnly b2
                    ? (decimal)(a.DayNumber - b2.DayNumber) : null;
            case DateAddNode da:
                if (Eval(da.Date, fields, values, today) is not DateOnly date
                    || Eval(da.Days, fields, values, today) is not decimal days
                    || Math.Abs(days) > MaxDays) return null;
                return date.AddDays((int)decimal.Truncate(days));
            default: return null;
        }
    }

    private static object? ReadField(Field f, IReadOnlyDictionary<string, JsonElement?> values)
    {
        if (!values.TryGetValue(f.Key, out var raw) || raw is not { } v
            || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        var isDate = f.Type == FieldType.Date || (f.Type is FieldType.Formula or FieldType.Rollup && f.Config.ResultType == "date");
        if (isDate)
            return v.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var num)) return null;
        return f.Type == FieldType.Percent ? num / 100m : num;
    }
}
