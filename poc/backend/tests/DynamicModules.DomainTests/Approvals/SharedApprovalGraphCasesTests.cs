using System.Text.Json;
using DynamicModules.Domain.Approvals;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Approvals;

/// <summary>讀 poc/shared/approval-graph-cases.json：流程圖結構驗證的唯一真相，前端（fg3）讀同一份。斷言 (鍵, 規則, 代碼) 的集合，不看順序與語言。</summary>
public class SharedApprovalGraphCasesTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement Doc = Load();

    private static JsonElement Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "poc", "shared", "approval-graph-cases.json")))
            dir = dir.Parent;
        if (dir is null) throw new FileNotFoundException("找不到 poc/shared/approval-graph-cases.json");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "poc", "shared", "approval-graph-cases.json"))).RootElement.Clone();
    }

    public static TheoryData<int, string> Cases()
    {
        var data = new TheoryData<int, string>();
        var i = 0;
        foreach (var c in Doc.GetProperty("cases").EnumerateArray()) data.Add(i++, c.GetProperty("id").GetString()!);
        return data;
    }

    [Fact]
    public void CaseIds_AreUnique()
    {
        var ids = Doc.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        ids.Should().OnlyHaveUniqueItems().And.NotContainNulls();
    }

    private static GraphPolicyLimits LimitsOf(JsonElement e) =>
        new(e.GetProperty("maxApprovalNodes").GetInt32(), e.GetProperty("maxBranches").GetInt32());

    private static List<(string, string, string)> Issues(JsonElement arr) =>
        [.. arr.EnumerateArray().Select(t => (t[0].GetString()!, t[1].GetString()!, t[2].GetString()!))];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(int index, string id)
    {
        var c = Doc.GetProperty("cases")[index];
        c.GetProperty("id").GetString().Should().Be(id);
        var name = $"{id} {c.GetProperty("name").GetString()}";

        var d = c.GetProperty("definition").Deserialize<ApprovalDefinition>(Web)!;
        var prev = c.GetProperty("previous") is { ValueKind: JsonValueKind.Object } p ? p.Deserialize<ApprovalDefinition>(Web) : null;
        var limits = LimitsOf(c.GetProperty("limits") is { ValueKind: JsonValueKind.Object } l ? l : Doc.GetProperty("limits"));

        var result = ApprovalGraphValidator.Check(d, limits, prev);

        result.Errors.Select(i => (i.Key, i.Rule, i.Code)).Should().BeEquivalentTo(Issues(c.GetProperty("errors")), name);
        result.Warnings.Select(i => (i.Key, i.Rule, i.Code)).Should().BeEquivalentTo(Issues(c.GetProperty("warnings")), name);
    }
}
