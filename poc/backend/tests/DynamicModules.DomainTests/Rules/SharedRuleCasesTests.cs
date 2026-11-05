using System.Text.Json;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Rules;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.Rules;

/// <summary>讀 poc/shared/rule-cases.json：規則語意的唯一真相，前端 vitest 讀同一份。</summary>
public class SharedRuleCasesTests
{
    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();
    private static readonly JsonElement Doc = Load();

    private static JsonElement Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "poc", "shared", "rule-cases.json")))
            dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("找不到 poc/shared/rule-cases.json");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "poc", "shared", "rule-cases.json"))).RootElement.Clone();
    }

    private static DateOnly Today => DateOnly.Parse(Doc.GetProperty("today").GetString()!);

    private static IReadOnlySet<string> Users =>
        Doc.GetProperty("users").EnumerateArray().Select(u => u.GetString()!).ToHashSet();

    public static TheoryData<int, string> ConditionCases() => Names("conditions");
    public static TheoryData<int, string> OutcomeCases() => Names("outcomes");

    private static TheoryData<int, string> Names(string section)
    {
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var c in Doc.GetProperty(section).EnumerateArray()) data.Add(i++, c.GetProperty("name").GetString()!);
        return data;
    }

    private static Module BuildModule(JsonElement fields, JsonElement blocks)
    {
        var byId = fields.EnumerateArray().Select(f => new Field
        {
            Id = f.GetProperty("id").GetInt64(),
            Key = f.GetProperty("key").GetString()!,
            Type = Enum.Parse<FieldType>(f.GetProperty("type").GetString()!, ignoreCase: true),
            Status = f.TryGetProperty("active", out var a) && !a.GetBoolean() ? FieldStatus.Inactive : FieldStatus.Active,
            Config = f.TryGetProperty("config", out var c) ? c.Deserialize<FieldConfig>(new JsonSerializerOptions(JsonSerializerDefaults.Web))! : new FieldConfig(),
            Options = f.TryGetProperty("options", out var o)
                ? o.EnumerateArray().Select((x, i) => new FieldOption { Id = x.GetInt64(), Active = true, Sequence = i }).ToList()
                : []
        }).ToDictionary(f => f.Id);
        return new Module
        {
            Blocks = blocks.EnumerateArray().Select(b => new Block
            {
                Id = b.GetProperty("id").GetInt64(),
                Fields = b.GetProperty("fieldIds").EnumerateArray().Select(i => byId[i.GetInt64()]).ToList()
            }).ToList()
        };
    }

    private static Dictionary<string, JsonElement?> ValuesOf(JsonElement obj) =>
        obj.EnumerateObject().ToDictionary(p => p.Name, p => (JsonElement?)p.Value.Clone());

    [Theory]
    [MemberData(nameof(ConditionCases))]
    public void Condition(int index, string name)
    {
        var c = Doc.GetProperty("conditions")[index];
        var module = BuildModule(Doc.GetProperty("fields"), Doc.GetProperty("blocks"));

        var parsed = ConditionParser.Parse(c.GetProperty("condition"), module, Registry, ConditionUsage.Rule, Users);
        parsed.Error.Should().BeNull(name);

        ConditionEvaluator.Evaluate(parsed.Node, ValuesOf(c.GetProperty("values")), Registry, Today)
            .Should().Be(c.GetProperty("expected").GetBoolean(), name);
    }

    [Theory]
    [MemberData(nameof(OutcomeCases))]
    public void Outcome(int index, string name)
    {
        var o = Doc.GetProperty("outcomes")[index];
        var module = BuildModule(
            o.TryGetProperty("fields", out var f) ? f : Doc.GetProperty("fields"),
            o.TryGetProperty("blocks", out var b) ? b : Doc.GetProperty("blocks"));
        var seq = 0;
        module.Rules = o.GetProperty("rules").EnumerateArray().Select(r => new Rule
        {
            Kind = Enum.Parse<RuleKind>(r.GetProperty("kind").GetString()!, ignoreCase: true),
            Condition = r.GetProperty("condition").Clone(),
            Targets = r.GetProperty("targets").EnumerateArray().Select(t => t.GetString()!).ToList(),
            OptionIds = r.TryGetProperty("optionIds", out var ids) ? ids.EnumerateArray().Select(i => i.GetInt64()).ToList() : [],
            MessageI18n = r.TryGetProperty("message", out var m) ? m.Deserialize<I18nText>() : null,
            Active = !r.TryGetProperty("active", out var a) || a.GetBoolean(),
            Sequence = seq++
        }).ToList();
        var stored = o.GetProperty("stored");

        var outcome = RuleEngine.Evaluate(
            module, ValuesOf(o.GetProperty("values")),
            stored.ValueKind == JsonValueKind.Null ? null : ValuesOf(stored),
            Registry, Users, Today);

        var e = o.GetProperty("expected");
        Ids(e, "hidden").Should().BeEquivalentTo(outcome.HiddenFieldIds, name);
        Ids(e, "required").Should().BeEquivalentTo(outcome.RequiredFieldIds, name);
        Ids(e, "readOnly").Should().BeEquivalentTo(outcome.ReadOnlyFieldIds, name);
        outcome.ReadOnlyAll.Should().Be(e.GetProperty("readOnlyAll").GetBoolean(), name);

        var allowed = e.GetProperty("allowedOptions").EnumerateObject()
            .ToDictionary(p => long.Parse(p.Name), p => p.Value.EnumerateArray().Select(i => i.GetInt64()).ToHashSet());
        outcome.AllowedOptions.Keys.Should().BeEquivalentTo(allowed.Keys, name);
        foreach (var (id, options) in allowed) outcome.AllowedOptions[id].Should().BeEquivalentTo(options, name);

        var failed = e.GetProperty("failedValidations").EnumerateArray()
            .Select(t => t.EnumerateArray().Select(i => i.GetInt64()).ToList()).ToList();
        outcome.FailedValidations.Select(v => v.TargetFieldIds.ToList()).Should().Equal(failed, (x, y) => x.SequenceEqual(y), name);
    }

    private static HashSet<long> Ids(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(i => i.GetInt64()).ToHashSet();

    [Fact]
    public void Cases_CoverEveryOperatorOfEveryFieldType()
    {
        // 防止案例檔被刪減：每個欄位型別（公式分數字與日期結果）的每個運算子至少各有一個成立與一個不成立的案例；
        // textarea 只有空值運算子。
        var module = BuildModule(Doc.GetProperty("fields"), Doc.GetProperty("blocks"));
        var fields = module.Blocks.SelectMany(x => x.Fields).Where(x => x.Status == FieldStatus.Active).ToList();
        static string TypeKey(Field f) => f.Type == FieldType.Formula ? $"formula:{f.Config.ResultType}" : f.Type.ToString();
        var typeOfField = fields.ToDictionary(f => f.Id, TypeKey);

        var seen = new HashSet<(string Type, string Op, bool Expected)>();
        foreach (var c in Doc.GetProperty("conditions").EnumerateArray())
        {
            var cond = c.GetProperty("condition");
            if (cond.TryGetProperty("field", out var fid) && typeOfField.TryGetValue(fid.GetInt64(), out var type))
                seen.Add((type, cond.GetProperty("op").GetString()!, c.GetProperty("expected").GetBoolean()));
        }

        var missing = new List<string>();
        foreach (var field in fields.DistinctBy(TypeKey))
        {
            var ops = field.Type == FieldType.Textarea
                ? [FilterOperator.IsEmpty, FilterOperator.IsNotEmpty]
                : Registry.Get(field.Type).OperatorsFor(field);
            foreach (var op in ops)
            {
                var name = char.ToLowerInvariant(op.ToString()[0]) + op.ToString()[1..];
                if (!seen.Contains((TypeKey(field), name, true))) missing.Add($"{TypeKey(field)} {name} 缺成立案例");
                if (!seen.Contains((TypeKey(field), name, false))) missing.Add($"{TypeKey(field)} {name} 缺不成立案例");
            }
        }
        string.Join("; ", missing).Should().BeEmpty();
    }
}
