using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 內建應用程式「進銷存」（apps/inventory.app.json）的載入驗證：AppCatalog 啟動載入通過（含 V3、V4 引用層、
/// 套件內事件圖無環、allowHiddenSource、列印範本欄位引用），以及權限與四語系這些設計上的約定。
/// </summary>
public class InventoryPackageTests
{
    private static readonly Lazy<AppPackageData> Package = new(() =>
        AppCatalog.Load(AppCatalog.DefaultDirectory).BuiltIns.Single(p => p.Manifest.Id == "inventory"));

    [Fact]
    public void Catalog_LoadsInventory_AndTheValidatorReportsNoErrors()
    {
        var p = Package.Value;
        p.FormatVersion.Should().Be(4);
        p.Manifest.Requires.Apps.Should().BeEmpty("進銷存不依賴其他應用程式");
        p.Manifest.Requires.Entities.Should().BeEmpty();
        AppPackageValidator.Validate(p).Errors.Should().BeEmpty();
        p.Modules.Should().HaveCount(13);
        p.Modules.Should().OnlyContain(m => m.Key.StartsWith("inv-"));
    }

    [Fact]
    public void EventsChecksLedgerConversionReportsAndPrint_AreAllThere_WithAppPrefixedCodes()
    {
        var p = Package.Value;
        p.Events.Select(e => e.Code).Should().BeEquivalentTo(
            "inv-receipt-post", "inv-receipt-cancel", "inv-shipment-post", "inv-shipment-cancel", "inv-adjust-post", "inv-adjust-cancel");
        p.Checks.Should().ContainSingle().Which.Code.Should().Be("inv-no-negative-stock");
        p.Ledgers.Should().ContainSingle().Which.Module.Should().Be("move");
        p.Conversions.Should().ContainSingle().Which.Code.Should().Be("inv-po-to-receipt");
        p.Reports.Should().HaveCount(5);
        p.PrintTemplates.Should().ContainSingle().Which.Code.Should().Be("inv-ship-sheet");
        // 效果只寫入庫存異動，而庫存異動沒有任何事件：事件圖無環（validator 也會擋，這裡把設計前提釘住）
        p.Events.SelectMany(e => e.Effects).Should().OnlyContain(x => x.Target == "move");
        p.Events.Should().NotContain(e => e.Source == "move");
    }

    [Fact]
    public void NoGroupCanAddMoves_AndEveryGroupHasPricing()
    {
        var p = Package.Value;
        var move = p.Modules.Single(m => m.Code == "move");
        move.Permissions.Select(x => x.Group).Should().BeEquivalentTo("buyer", "keeper", "manager");
        move.Permissions.Should().OnlyContain(x => !x.Privileges.Add && !x.Privileges.Edit && !x.Privileges.Delete && x.Privileges.Read);
        p.Modules.SelectMany(m => m.Permissions).Should().OnlyContain(x => x.Privileges.Pricing, "三個群組都有金額權限，帶值才帶得到單價");
        p.FieldAccess.Should().BeEmpty("事件來源欄位沒有被隱藏，不需要 allowHiddenSource");
    }

    [Fact]
    public void EveryLabel_HasAllFourLocales()
    {
        var p = Package.Value;
        var texts = new List<(string Where, I18nText Text)> { ("manifest.name", p.Manifest.Name), ("manifest.description", p.Manifest.Description!) };
        texts.AddRange(p.Groups.Select(g => ($"group {g.Code}", g.Name)));
        foreach (var m in p.Modules)
        {
            texts.Add(($"{m.Code}", m.Name));
            foreach (var b in m.Blocks)
            {
                texts.Add(($"{m.Code}.{b.Code}", b.Label));
                foreach (var f in b.Fields)
                {
                    texts.Add(($"{m.Code}.{f.Code}", f.Label));
                    texts.AddRange(f.Options.Select(o => ($"{m.Code}.{f.Code}.{o.Code}", o.Label)));
                }
            }
            texts.AddRange(m.Rules.Where(r => r.Message is not null).Select(r => ($"{m.Code} rule", r.Message!)));
        }
        texts.AddRange(p.Events.Select(e => ($"event {e.Code}", e.Name)));
        texts.AddRange(p.Checks.SelectMany(c => new[] { ($"check {c.Code}", c.Name), ($"check {c.Code} message", c.Message) }));
        texts.AddRange(p.Conversions.Select(c => ($"conversion {c.Code}", c.Name)));
        texts.AddRange(p.PrintTemplates.Select(t => ($"print {t.Code}", t.Name)));

        foreach (var (where, text) in texts)
            text.Values.Keys.Should().BeEquivalentTo(I18nText.SupportedLocales, where);
    }

    [Fact]
    public void FieldsTheCheckUses_AreFilterableInThePackage()
    {
        var move = Package.Value.Modules.Single(m => m.Code == "move");
        foreach (var code in new[] { "item", "warehouse", "qty" })
        {
            var field = move.Blocks.SelectMany(b => b.Fields).Single(f => f.Code == code);
            field.Config.GetProperty("isFilterable").GetBoolean().Should().BeTrue(code);
        }
    }
}
