using System.Text.Json;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.DomainTests.Localization;

/// <summary>讀 shared/i18n-message-cases.json 的 ui、server 段（語系包設計 §13.2、T15）；前端 message-check.vitest.spec.ts 跑同一份 ui 段。</summary>
public class LocaleMessageCasesTests
{
    private static readonly JsonElement Doc = SharedFile.Load("i18n-message-cases.json");

    public static TheoryData<string> UiCases() => Names("ui");
    public static TheoryData<string> ServerCases() => Names("server");

    private static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Doc.GetProperty(section).EnumerateArray()) data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    private static JsonElement Case(string section, string name)
        => Doc.GetProperty(section).EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);

    private static string Text(JsonElement c)
    {
        var text = c.GetProperty("text").GetString()!;
        return c.TryGetProperty("repeat", out var r) ? string.Concat(Enumerable.Repeat(text, r.GetInt32())) : text;
    }

    private static string? Expected(JsonElement c)
        => c.GetProperty("expected").ValueKind == JsonValueKind.Null ? null : c.GetProperty("expected").GetString();

    [Theory]
    [MemberData(nameof(UiCases))]
    public void Ui(string name)
    {
        var c = Case("ui", name);
        var allowed = c.GetProperty("allowed").EnumerateArray().Select(a => a.GetString()!).ToHashSet(StringComparer.Ordinal);
        var text = Text(c);

        var actual = LocaleTextSafety.Check(text, LocalePackLimits.MaxTextLength) ?? UiMessageSyntax.Check(text, allowed);

        actual.Should().Be(Expected(c), name);
    }

    [Theory]
    [MemberData(nameof(ServerCases))]
    public void Server(string name)
    {
        var c = Case("server", name);
        var text = Text(c);

        var actual = LocaleTextSafety.Check(text, LocalePackLimits.MaxTextLength) ?? ServerTemplateSyntax.Check(text, c.GetProperty("base").GetString()!);

        actual.Should().Be(Expected(c), name);
    }

    [Fact]
    public void CaseFile_HasAllSections()
    {
        Doc.GetProperty("ui").GetArrayLength().Should().BeGreaterThan(10);
        Doc.GetProperty("server").GetArrayLength().Should().BeGreaterThan(5);
        Doc.GetProperty("boolean").GetArrayLength().Should().BeGreaterThan(3);
    }
}
