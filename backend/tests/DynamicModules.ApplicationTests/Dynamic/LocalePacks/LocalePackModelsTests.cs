using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>i4d1 形狀的預覽暫存（部署前建立、還沒過期）在 i4d2 照樣能讀（i4d2 計畫判斷 7 的相容規則、第二輪必修 8）。</summary>
public class LocalePackModelsTests
{
    private static readonly LocalePackInfo Info = new(null, null, null, null);

    private static LocalePackPlan NewPlan(bool withSentence) => new("ja", false, null,
        withSentence ? new Dictionary<string, string> { ["menu.home"] = "ホーム" } : new Dictionary<string, string>(), [],
        new Dictionary<string, string>(), [], [], new Dictionary<string, BooleanCandidate>(), Info)
    {
        UiConfirm = ["dynamic.save"], ServerConfirm = ["Forbidden"],
    };

    /// <summary>序列化後刪掉 i4d2 新加的欄位，模擬 i4d1 產出的 JSON。</summary>
    private static string Legacy(object value, params string[] paths)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(value, LocalePackStorage.Json))!;
        foreach (var path in paths)
        {
            var parts = path.Split('.');
            var parent = parts[..^1].Aggregate(node, (n, p) => n[p]!);
            parent.AsObject().Remove(parts[^1]).Should().BeTrue(path);
        }
        return node.ToJsonString();
    }

    [Fact]
    public void Plan_OldJsonWithoutConfirm_DeserializesEmpty()
    {
        foreach (var withSentence in new[] { true, false })
        {
            var json = Legacy(NewPlan(withSentence), "uiConfirm", "serverConfirm");
            json.Should().NotContain("Confirm\"");
            var back = JsonSerializer.Deserialize<LocalePackPlan>(json, LocalePackStorage.Json)!;
            back.UiConfirm.Should().BeEmpty();
            back.ServerConfirm.Should().BeEmpty();
            back.OverlayChanged.Should().Be(withSentence);
            back.NoChanges.Should().Be(!withSentence);
        }

        var nulls = JsonSerializer.Deserialize<LocalePackPlan>(
            Legacy(NewPlan(false), "uiConfirm", "serverConfirm").Replace("\"code\":\"ja\"", "\"code\":\"ja\",\"uiConfirm\":null,\"serverConfirm\":null"),
            LocalePackStorage.Json)!;
        nulls.UiConfirm.Should().BeEmpty();
        nulls.ServerConfirm.Should().BeEmpty();

        var current = JsonSerializer.Deserialize<LocalePackPlan>(JsonSerializer.Serialize(NewPlan(false), LocalePackStorage.Json), LocalePackStorage.Json)!;
        current.UiConfirm.Should().Equal("dynamic.save");
        current.OverlayChanged.Should().BeTrue("只有確認也要寫新版本");
    }

    [Fact]
    public void Report_OldJson_DefaultsNormalized()
    {
        var section = new SectionReport(1, 0, 2, 0, 0, 0, [], 0, []) { Confirmed = 2 };
        var report = new LocalePackReport(null, null, true, "f", "h", new LocaleReport("ja", false, false, "日本語", null, null, [], []), Info,
            section, section, new CompletenessReport(new CompletenessCount(1, 2), new CompletenessCount(3, 4)) { TopMissing = [new MissingCategory("ui", "approval", 1)] },
            new DesignDataReport(0), [])
        {
            OrphanedDropped = new OrphanedDroppedReport(1, 2),
        };
        var json = Legacy(report, "ui.confirmed", "server.confirmed", "orphanedDropped", "completeness.topMissing");
        var back = JsonSerializer.Deserialize<LocalePackReport>(json, LocalePackStorage.Json)!;
        back.Ui.Confirmed.Should().Be(0);
        back.Server.Confirmed.Should().Be(0);
        back.OrphanedDropped.Should().Be(new OrphanedDroppedReport(0, 0));
        back.Completeness.TopMissing.Should().BeEmpty();
        back.Completeness.Ui.Should().Be(new CompletenessCount(1, 2));

        var full = JsonSerializer.Deserialize<LocalePackReport>(JsonSerializer.Serialize(report, LocalePackStorage.Json), LocalePackStorage.Json)!;
        (full.Ui.Confirmed, full.OrphanedDropped, full.Completeness.TopMissing.Single().Category).Should().Be((2, new OrphanedDroppedReport(1, 2), "approval"));
    }
}
