using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Common;

/// <summary><see cref="ServiceResult"/> 與 <see cref="MessageRef"/> 經 catalog 解析（語系包 i4c 計畫判斷 5）：裝了 ja（上層 en-US）時走退回鏈，內建四語與舊多載相同。</summary>
public class ServiceResultCatalogTests
{
    private static readonly Dictionary<string, Dictionary<string, string>> Base = BuiltinServerTextTests.Baseline("message-report.json");

    private static Task<MessageCatalogTests.Env> JapaneseAsync() => MessageCatalogTests.CreateAsync("zh-TW", r => r.Add("ja", "日本語", fallback: "en-US"));

    private static I18nText Name => new([KeyValuePair.Create("zh-TW", "主管關"), KeyValuePair.Create("en-US", "Manager stage")]);

    [Fact]
    public async Task KeyedMessage_Ja_English()
    {
        var env = await JapaneseAsync();
        ServiceResult.Forbidden(MessageRef.Of(MessageText.Forbidden)).MessageIn(env.Catalog, env.Lc("ja"))
            .Should().Be(Base[MessageText.Forbidden]["en-US"]);
    }

    [Fact]
    public async Task Literal_MatchingZh_TranslatedThroughChain()
    {
        var env = await JapaneseAsync();
        ServiceResult.NotFound(Base[MessageText.Forbidden]["zh-TW"]).MessageIn(env.Catalog, env.Lc("ja"))
            .Should().Be(Base[MessageText.Forbidden]["en-US"]);
        ServiceResult.NotFound("不在表裡的一句").MessageIn(env.Catalog, env.Lc("ja")).Should().Be("不在表裡的一句");
    }

    [Fact]
    public async Task FieldErrors_LiteralNeverTranslated()
    {
        var env = await JapaneseAsync();
        var zh = Base[MessageText.Forbidden]["zh-TW"];
        ServiceResult.Invalid("name", MessageRef.Literal(zh)).ErrorsIn(env.Catalog, env.Lc("ja"))["name"].Should().Equal(zh);
        ServiceResult.Invalid("name", MessageRef.Of(MessageText.Forbidden)).ErrorsIn(env.Catalog, env.Lc("ja"))["name"]
            .Should().Equal(Base[MessageText.Forbidden]["en-US"]);
    }

    [Fact]
    public async Task I18nTextArg_UsesSameChain()
    {
        var env = await JapaneseAsync();
        MessageRef.Of(MessageText.ApprovalNoApprover, Name, "x").Resolve(env.Catalog, env.Lc("ja"))
            .Should().Be(string.Format(CultureInfo.InvariantCulture, Base[MessageText.ApprovalNoApprover]["en-US"], "Manager stage", "x"));
    }

    [Fact]
    public async Task OldOverload_Unchanged()
    {
        var env = await MessageCatalogTests.CreateAsync();
        var results = new[]
        {
            ServiceResult.Forbidden(), ServiceResult.Stale(), ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound)),
            ServiceResult.NotFound(Base[MessageText.RecordNotFound]["zh-TW"]), ServiceResult.NotFound("不在表裡的一句"),
            ServiceResult.Conflict(MessageRef.Of(MessageText.ApprovalNoApprover, Name, 3)),
        };
        var errors = ServiceResult.Invalid(new Dictionary<string, MessageRef[]>
        {
            ["a"] = [MessageRef.Of(MessageText.NewPasswordTooShort, 8)], ["b"] = [MessageRef.Literal(Base[MessageText.Forbidden]["zh-TW"])],
        });
        foreach (var l in BuiltinServerTextTests.Locales)
        {
            var lc = env.Lc(l);
            foreach (var r in results) r.MessageIn(env.Catalog, lc).Should().Be(r.MessageIn(l), l);
            errors.ErrorsIn(env.Catalog, lc).Should().BeEquivalentTo(errors.ErrorsIn(l), l);
        }
    }
}
