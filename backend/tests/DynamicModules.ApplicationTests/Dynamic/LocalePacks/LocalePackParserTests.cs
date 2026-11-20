using System.Text;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>語系包解析（語系包 i4d1 計畫 Task 5、判斷 1、2、5）：整包層級的錯誤與逐鍵的 duplicate／notString。</summary>
public class LocalePackParserTests
{
    private const string Head = "\"format\":\"formwright-langpack\",\"formatVersion\":1";

    private static string Pack(string locale = "{\"code\":\"ja\",\"nativeName\":\"日本語\"}", string? rest = null) =>
        "{" + Head + ",\"locale\":" + locale + (rest is null ? "" : "," + rest) + "}";

    private static (ParsedLocalePack? Pack, MessageRef? Error) Parse(string json) => LocalePackParser.Parse(Encoding.UTF8.GetBytes(json).AsMemory());

    private static (ParsedLocalePack? Pack, MessageRef? Error) Parse(byte[] bytes) => LocalePackParser.Parse(bytes.AsMemory());

    private static ParsedLocalePack Ok(string json)
    {
        var (pack, error) = Parse(json);
        error.Should().BeNull(error?.Resolve("zh-TW"));
        return pack!;
    }

    private static string Error(string json)
    {
        var (pack, error) = Parse(json);
        pack.Should().BeNull();
        error.Should().NotBeNull();
        return error!.Resolve("zh-TW");
    }

    // ── 格式 ──

    [Fact]
    public void NotJson_Text() => Error("hello").Should().Be("檔案不是語系包（.langpack.json）");

    [Fact]
    public void Broken_Json() => Error("{\"format\":").Should().Be("檔案不是語系包（.langpack.json）");

    [Fact]
    public void Xlsx_Signature() =>
        Parse([0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]).Error!.Resolve("zh-TW").Should().Be("本版只收 .langpack.json，Excel 翻譯樣板之後支援");

    [Fact]
    public void Bom_SameAsWithout()
    {
        var json = Pack(rest: "\"ui\":{\"menu.home\":\"ホーム\"}");
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        var a = Parse(withBom).Pack!;
        var b = Ok(json);
        a.Code.Should().Be(b.Code);
        a.Ui.Should().BeEquivalentTo(b.Ui);
    }

    [Fact]
    public void Format_Wrong() =>
        Error("{\"format\":\"other\",\"formatVersion\":1,\"locale\":{\"code\":\"ja\"}}").Should().Be("不是 Formwright 語系包");

    [Fact]
    public void FormatVersion_TooNew() =>
        Error("{\"format\":\"formwright-langpack\",\"formatVersion\":2,\"locale\":{\"code\":\"ja\"}}")
            .Should().Be("這份語系包的格式版本 2 比系統支援的 1 新");

    [Fact]
    public void FormatVersion_String() =>
        Error("{\"format\":\"formwright-langpack\",\"formatVersion\":\"1\",\"locale\":{\"code\":\"ja\"}}").Should().Be("不是 Formwright 語系包");

    [Fact]
    public void Depth17_TooDeep()
    {
        // 根物件算第 1 層，ui 的值是第 2 層：ui 底下共 16 層物件 → 17
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 16)) + "\"x\"" + new string('}', 16);
        Error(Pack(rest: "\"ui\":" + nested)).Should().Be("語系包的 JSON 層數太多（最多 16 層）");
        var ok = string.Concat(Enumerable.Repeat("{\"a\":", 15)) + "\"x\"" + new string('}', 15);
        Ok(Pack(rest: "\"ui\":" + ok)).Ui.Should().ContainKey(string.Join('.', Enumerable.Repeat("a", 15)));
    }

    [Fact]
    public void Rtl_Rejected() =>
        Error(Pack("{\"code\":\"ar\",\"nativeName\":\"العربية\",\"direction\":\"rtl\"}")).Should().Be("本版不支援由右至左的語言");

    [Fact]
    public void Code_Missing() => Error(Pack("{\"nativeName\":\"日本語\"}")).Should().Be("語系代碼「」格式不對（例：ja、zh-Hant-HK）");

    [Fact]
    public void Code_Normalized() => Ok(Pack("{\"code\":\"JA\"}")).Code.Should().Be("ja");

    [Fact]
    public void Code_Underscore_Rejected() => Error(Pack("{\"code\":\"ja_JP\"}")).Should().Be("語系代碼「ja_JP」格式不對（例：ja、zh-Hant-HK）");

    [Fact]
    public void NativeName_51_Rejected() =>
        Error(Pack("{\"code\":\"ja\",\"nativeName\":\"" + new string('日', 51) + "\"}")).Should().Be("locale.nativeName 不能超過 50 字，也不能含控制字元");

    [Fact]
    public void NativeName_Bidi_Rejected() =>
        Error(Pack("{\"code\":\"ja\",\"nativeName\":\"日本\\u202E語\"}")).Should().Be("locale.nativeName 不能超過 50 字，也不能含控制字元");

    [Fact]
    public void QuasarLang_Unknown() =>
        Error(Pack("{\"code\":\"ja\",\"quasarLang\":\"xx-YY\"}")).Should().Be("找不到日期與表格元件的語言包「xx-YY」");

    [Fact]
    public void QuasarLang_Known_Fallback_Normalized()
    {
        var lang = BuiltinLocaleResources.QuasarLangs.First();
        var p = Ok(Pack("{\"code\":\"ja\",\"quasarLang\":\"" + lang + "\",\"fallback\":\"EN-us\"}"));
        p.QuasarLang.Should().Be(lang);
        p.Fallback.Should().Be("en-US");
    }

    [Theory]
    [InlineData("version", 51, "pack.version 不能超過 50 字，也不能含控制字元")]
    [InlineData("note", 501, "pack.note 不能超過 500 字，也不能含控制字元")]
    [InlineData("builtFor", 21, "pack.builtFor 不能超過 20 字，也不能含控制字元")]
    public void PackFields_TooLong(string field, int length, string expected) =>
        Error(Pack(rest: "\"pack\":{\"" + field + "\":\"" + new string('a', length) + "\"}")).Should().Be(expected);

    [Fact]
    public void PackFields_Read()
    {
        var p = Ok(Pack(rest: "\"pack\":{\"version\":\"1.2.0\",\"builtFor\":\"2026.11\",\"baseLocale\":\"zh-TW\",\"note\":\"初版\"}"));
        (p.PackVersion, p.BuiltFor, p.BaseLocale, p.Note).Should().Be(("1.2.0", "2026.11", "zh-TW", "初版"));
    }

    // ── 段落 ──

    [Fact]
    public void Ui_Nested_Flattened()
    {
        var p = Ok(Pack(rest: "\"ui\":{\"menu\":{\"home\":\"ホーム\",\"sub\":{\"x\":\"X\"}},\"common.save\":\"保存\"}"));
        p.Ui.Should().BeEquivalentTo(new Dictionary<string, string> { ["menu.home"] = "ホーム", ["menu.sub.x"] = "X", ["common.save"] = "保存" });
        p.UiProblems.Should().BeEmpty();
    }

    [Fact]
    public void Ui_DuplicateProperty_BothDropped()
    {
        var p = Ok(Pack(rest: "\"ui\":{\"menu.home\":\"a\",\"menu.home\":\"b\",\"common.save\":\"保存\"}"));
        p.Ui.Should().NotContainKey("menu.home").And.ContainKey("common.save");
        p.UiProblems.Should().ContainSingle().Which.Should().BeEquivalentTo(new RejectedItem("menu.home", LocalePackReasons.Duplicate, []));
    }

    [Fact]
    public void Ui_NestedAndFlat_Collide()
    {
        var p = Ok(Pack(rest: "\"ui\":{\"menu\":{\"home\":\"a\"},\"menu.home\":\"b\"}"));
        p.Ui.Should().BeEmpty();
        p.UiProblems.Should().ContainSingle().Which.Reason.Should().Be(LocalePackReasons.Duplicate);
    }

    [Fact]
    public void Ui_DuplicateInsideNested()
    {
        var p = Ok(Pack(rest: "\"ui\":{\"menu\":{\"home\":\"a\",\"home\":\"b\"}}"));
        p.Ui.Should().BeEmpty();
        p.UiProblems.Should().ContainSingle().Which.Should().BeEquivalentTo(new RejectedItem("menu.home", LocalePackReasons.Duplicate, []));
    }

    [Fact]
    public void Ui_NonString_PerKey()
    {
        var p = Ok(Pack(rest: "\"ui\":{\"a.n\":1,\"a.b\":true,\"a.z\":null,\"a.arr\":[\"x\"],\"common.save\":\"保存\"}"));
        p.Ui.Keys.Should().BeEquivalentTo(["common.save"]);
        p.UiProblems.Select(x => (x.Key, x.Reason)).Should().BeEquivalentTo(new[]
        {
            ("a.arr", LocalePackReasons.NotString), ("a.b", LocalePackReasons.NotString),
            ("a.n", LocalePackReasons.NotString), ("a.z", LocalePackReasons.NotString),
        });
    }

    [Fact]
    public void Ui_EmptyString_Kept() => Ok(Pack(rest: "\"ui\":{\"common.save\":\"\"}")).Ui["common.save"].Should().Be("");

    [Fact]
    public void Ui_Array_SectionNotObject() => Error(Pack(rest: "\"ui\":[\"a\"]")).Should().Be("ui 段必須是 JSON 物件");

    [Fact]
    public void Server_ObjectValue_NotString()
    {
        var p = Ok(Pack(rest: "\"server\":{\"Forbidden\":{\"x\":\"y\"},\"report.Yes\":\"はい\"}"));
        p.Server.Should().BeEquivalentTo(new Dictionary<string, string> { ["report.Yes"] = "はい" });
        p.ServerProblems.Should().ContainSingle().Which.Should().BeEquivalentTo(new RejectedItem("Forbidden", LocalePackReasons.NotString, []));
    }

    [Fact]
    public void Ui_TooManyKeys()
    {
        var max = BuiltinLocaleResources.BaseKeys.Count * LocalePackLimits.MaxKeysFactor;
        var keys = string.Join(',', Enumerable.Range(0, max + 1).Select(i => $"\"k{i}\":\"v\""));
        Error(Pack(rest: "\"ui\":{" + keys + "}")).Should().Be($"ui 段有 {max + 1} 句，超過上限 {max} 句");
    }

    [Fact]
    public void AllSectionsMissing_Valid()
    {
        var p = Ok(Pack());
        p.Ui.Should().BeEmpty();
        p.Server.Should().BeEmpty();
        p.DesignDataCount.Should().Be(0);
        p.NativeName.Should().Be("日本語");
    }

    [Fact]
    public void UnknownTopLevel_Ignored_DesignDataCounted()
    {
        var p = Ok(Pack(rest: "\"future\":{\"x\":1},\"designData\":{\"module:a/name\":{\"text\":\"A\"},\"module:b/name\":{\"text\":\"B\"}}"));
        p.DesignDataCount.Should().Be(2);
    }
}
