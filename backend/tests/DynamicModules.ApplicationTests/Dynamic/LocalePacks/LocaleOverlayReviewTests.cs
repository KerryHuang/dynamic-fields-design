using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>覆寫層檢視（i4d2 計畫判斷 3）：原文已更新、參數不符（含執行期退回內建翻譯）、已失效，從目前版的原始列算。</summary>
public class LocaleOverlayReviewTests
{
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static string Zh(string key) => BuiltinServerText.TryGet("zh-TW", key, out var zh) ? zh : "";

    private static IReadOnlySet<int> Args(string template) =>
        Regex.Matches(template, @"(?<!\{)\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();

    /// <summary>現在 zh 範本的參數編號剛好是 <paramref name="args"/> 的後端基準鍵。</summary>
    private static string ServerKey(params int[] args) =>
        BuiltinServerText.BaseKeys.First(k => Args(Zh(k)).SetEquals(args) && !Zh(k).Contains("{{"));

    private static LocalePack Row(IReadOnlyDictionary<string, string>? ui = null, IReadOnlyDictionary<string, string>? server = null,
        IReadOnlyDictionary<string, string>? uiHashes = null, IReadOnlyDictionary<string, string>? serverHashes = null) => new()
    {
        Code = "th", IsCurrent = true,
        UiJson = JsonSerializer.Serialize(ui ?? new Dictionary<string, string>()),
        ServerJson = JsonSerializer.Serialize(server ?? new Dictionary<string, string>()),
        BaseHashesJson = JsonSerializer.Serialize(new { ui = uiHashes ?? new Dictionary<string, string>(), server = serverHashes ?? new Dictionary<string, string>() }),
    };

    private static Dictionary<string, string> D(params (string K, string V)[] items) => items.ToDictionary(i => i.K, i => i.V, StringComparer.Ordinal);

    private static void AllZero(OverlayReviewSection s) =>
        (s.BaseChanged, s.ParamsChanged, s.RuntimeFallback, s.Orphaned).Should().Be((0, 0, 0, 0));

    [Fact]
    public void NoVersion_AllZero()
    {
        var r = LocaleOverlayReview.Of(null);
        AllZero(r.Ui);
        AllZero(r.Server);
    }

    [Fact]
    public void BaseChanged_ByHash()
    {
        var zhSave = BuiltinLocaleResources.Ui["zh-TW"]["dynamic.save"];
        var ui = D(("dynamic.save", "บันทึก!"), ("menu.home", "หน้าแรก!"), ("dynamic.cancel", "ยกเลิก!"));
        var r = LocaleOverlayReview.Of(Row(ui, uiHashes: D(("dynamic.save", "0000000000000000"),
            ("menu.home", Hash(BuiltinLocaleResources.Ui["zh-TW"]["menu.home"])))));
        r.Ui.BaseChanged.Should().Be(1, "menu.home 的雜湊和現在相同；dynamic.cancel 沒記雜湊，不猜");
        r.Ui.BaseChangedKeys.Should().Equal("dynamic.save");
        Hash(zhSave).Should().NotBe("0000000000000000");
        AllZero(r.Server);
    }

    [Fact]
    public void Server_ParamsChanged_WithoutHash()
    {
        var key = ServerKey(0);
        var r = LocaleOverlayReview.Of(Row(server: D((key, "ไม่มีพารามิเตอร์"))));
        r.Server.ParamsChanged.Should().Be(1, "參數不符不依賴有沒有記雜湊");
        r.Server.ParamsChangedKeys.Should().Equal(key);
        r.Server.BaseChanged.Should().Be(0);
    }

    [Fact]
    public void Server_MoreArgsThanZh_RuntimeFallback()
    {
        var key = ServerKey(0);
        var r = LocaleOverlayReview.Of(Row(server: D((key, "{0} และ {1}"))));
        (r.Server.ParamsChanged, r.Server.RuntimeFallback).Should().Be((1, 1));
        r.Server.RuntimeFallbackKeys.Should().Equal(key);
    }

    [Fact]
    public void Server_FewerArgsThanZh_NotRuntimeFallback()
    {
        var key = ServerKey(0, 1);
        var r = LocaleOverlayReview.Of(Row(server: D((key, "เฉพาะ {0}"))));
        (r.Server.ParamsChanged, r.Server.RuntimeFallback).Should().Be((1, 0));
        r.Server.RuntimeFallbackKeys.Should().BeEmpty();
    }

    [Fact]
    public void Server_ZeroArgZh_WithArg_NotRuntimeFallback()
    {
        var key = ServerKey();
        var r = LocaleOverlayReview.Of(Row(server: D((key, "ข้อความ {0}"))));
        (r.Server.ParamsChanged, r.Server.RuntimeFallback).Should().Be((1, 0));
    }

    [Fact]
    public void Ui_UnknownPlaceholder_ParamsChanged_NoRuntimeFallback()
    {
        var r = LocaleOverlayReview.Of(Row(ui: D(("approval.stagesMore", "{first} {gone}"))));
        (r.Ui.ParamsChanged, r.Ui.RuntimeFallback).Should().Be((1, 0));
        r.Ui.ParamsChangedKeys.Should().Equal("approval.stagesMore");
        r.Ui.RuntimeFallbackKeys.Should().BeEmpty();
    }

    [Fact]
    public void Orphaned_NonBaseKey()
    {
        var r = LocaleOverlayReview.Of(Row(ui: D(("approval.removedKey", "เก่า"), ("menu.home", "หน้าแรก!"))));
        r.Ui.Orphaned.Should().Be(1);
        r.Ui.OrphanedKeys.Should().Equal("approval.removedKey");
        LocaleOverlayReview.OrphanedTexts(Row(ui: D(("approval.removedKey", "เก่า"))), "ui").Should().Equal(D(("approval.removedKey", "เก่า")));
    }

    [Fact]
    public void Orphaned_60_Listed50_Ordinal()
    {
        var ui = Enumerable.Range(0, 60).ToDictionary(i => $"zz.gone{i:D2}", i => "t" + i);
        var row = Row(ui: ui);
        var r = LocaleOverlayReview.Of(row);
        r.Ui.Orphaned.Should().Be(60);
        r.Ui.OrphanedKeys.Should().HaveCount(LocalePackLimits.MaxListedItems).And.Equal(ui.Keys.Order(StringComparer.Ordinal).Take(50));
        LocaleOverlayReview.OrphanedTexts(row, "ui").Should().HaveCount(60);
    }

    [Fact]
    public void InvalidJson_AllZero_NoThrow()
    {
        var row = Row();
        row.UiJson = "{not json";
        var r = LocaleOverlayReview.Of(row);
        AllZero(r.Ui);
        AllZero(r.Server);
        LocaleOverlayReview.OrphanedTexts(row, "ui").Should().BeEmpty();
    }
}
