using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using DynamicModules.Application.Common;
using DynamicModules.Domain.FieldTypes;

namespace DynamicModules.ApplicationTests.Common;

/// <summary>MessageText 表的不變式：任何一條壞掉，執行時不是整站 500（重複鍵）就是 FormatException。</summary>
public class MessageTextTests
{
    private static readonly string[] Locales = ["zh-TW", "en-US", "th", "vi"];

    private static List<(string Key, string[] Texts)> Entries()
    {
        var table = (System.Collections.IDictionary)typeof(MessageText)
            .GetField("Table", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var list = new List<(string, string[])>();
        foreach (System.Collections.DictionaryEntry kv in table)
        {
            var v = kv.Value!;
            string P(string n) => (string)v.GetType().GetField(n)!.GetValue(v)!;
            list.Add(((string)kv.Key, [P("Item1"), P("Item2"), P("Item3"), P("Item4")]));
        }
        return list;
    }

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();

    [Fact]
    public void EveryKey_HasNonBlankTextInAllFourLocales()
    {
        var entries = Entries();
        entries.Should().NotBeEmpty();
        foreach (var (key, texts) in entries)
            for (var i = 0; i < Locales.Length; i++)
                string.IsNullOrWhiteSpace(texts[i]).Should().BeFalse($"{key} 的 {Locales[i]} 不可空白");
    }

    [Fact]
    public void ZhTexts_AreUnique()
    {
        var dup = Entries().GroupBy(e => e.Texts[0]).Where(g => g.Count() > 1)
            .Select(g => $"{string.Join("/", g.Select(e => e.Key))}: {g.Key}").ToList();
        dup.Should().BeEmpty("zh-TW 原文重複會讓字面值自動翻譯建表時丟例外");
    }

    [Fact]
    public void Placeholders_MatchZhInEveryLocale_AndFormatDoesNotThrow()
    {
        foreach (var (key, texts) in Entries())
        {
            var expected = Placeholders(texts[0]);
            var args = expected.Select(_ => (object)1).ToArray();
            for (var i = 0; i < Locales.Length; i++)
            {
                Placeholders(texts[i]).Should().Equal(expected, $"{key} 的 {Locales[i]} 佔位符要和 zh-TW 一致");
                var act = () => MessageText.Get(Locales[i], key, args);
                act.Should().NotThrow($"{key} 的 {Locales[i]} 帶入 {args.Length} 個參數");
            }
        }
    }

    [Fact]
    public void EveryDomainFieldError_HasAMessage()
    {
        var keys = Entries().Select(e => e.Key).ToHashSet();
        var missing = typeof(FieldErrors).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!).Where(k => !keys.Contains(k)).ToList();
        missing.Should().BeEmpty("Domain 只帶鍵，沒有文字的鍵在執行時會 KeyNotFoundException");
    }
}
