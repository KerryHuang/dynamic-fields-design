using System.Net;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建 CRM 改用共用主檔往來對象與聯絡人（共用主檔設計 §11.1、§15.2「開啟」「匯出匯入」「往來對象與聯絡人行為」）：
/// 匯出內容、開啟順序不影響主檔的欄位集合與區塊序列、只開 MES 時不出現聯絡人。
/// </summary>
public class CrmMasterTests
{
    /// <summary>只在測試裡的貢獻者：在往來對象加一個「倉儲」區塊。應用程式 id 排在 crm-sales 之後。</summary>
    private const string Ware = """
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "ware-lite", "version": "1.0.0", "name": { "zh-TW": "倉儲（測試）" },
            "requires": { "masters": [ { "id": "partner", "modules": ["partner"] } ] } },
          "groups": [],
          "modules": [
            { "code": "receipt", "key": "ware-receipt", "name": { "zh-TW": "收貨單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "supplier", "type": "reference", "label": { "zh-TW": "供應商" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "partner" } } }
              ] } ], "permissions": [] }
          ],
          "contributions": [ { "master": "partner", "module": "partner", "block": { "code": "ware", "name": { "zh-TW": "倉儲" } },
            "fields": [ { "code": "leadDays", "type": "integer", "label": { "zh-TW": "交貨天數" }, "config": {} } ] } ]
        }
        """;

    public static readonly object CrmBody = new
    {
        groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["manager"] = new { mode = "create" } },
        masters = new Dictionary<string, object>
        {
            ["partner"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } },
            ["contact"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } }
        }
    };

    private static async Task<List<(string Block, List<string> Fields)>> PartnerAfterOpeningAsync(params string[] order)
    {
        using var rig = new SharedMasterRig();
        if (order.Contains("ware-lite")) await rig.ImportAsync(Ware);
        foreach (var id in order) await rig.OpenOkAsync(id, id == "crm-sales" ? CrmBody : new { groupMap = new Dictionary<string, object>() });
        await rig.AssertIntegrityAsync();
        return rig.Blocks(BuiltInMasterTests.Partner);
    }

    /// <summary>主檔的刪除與核准留給 steward（§4.3、§5.1、§5.2）：CRM 對往來對象、聯絡人授予的權限沒有這兩個動作。</summary>
    [Fact]
    public async Task CrmGrantsOnTheMasters_NeverIncludeDeleteOrApprove()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmBody);
        var actions = rig.Db(db =>
        {
            var crm = db.Set<App>().Single(a => a.Key == "crm-sales").Id;
            return db.Set<AppGrant>().Where(g => g.SourceKind == GrantSource.App && g.AppId == crm).Select(g => g.Action).Distinct().ToList();
        });
        actions.Should().NotBeEmpty().And.NotContain([PermissionAction.Delete, PermissionAction.Approve]);
    }

    [Fact]
    public async Task OpeningOrder_DoesNotChangeThePartnerFieldsOrBlockSequence()
    {
        var alone = await PartnerAfterOpeningAsync("crm-sales");
        var crmFirst = await PartnerAfterOpeningAsync("crm-sales", "ware-lite");
        var wareFirst = await PartnerAfterOpeningAsync("ware-lite", "crm-sales");

        alone.Select(b => b.Block).Should().Equal("基本資料", "CRM");
        alone.Last().Fields.Should().Equal("產業:Enum:Active", "來源潛在客戶:Reference:Active");
        crmFirst.Select(b => b.Block).Should().Equal("基本資料", "CRM", "倉儲");
        crmFirst.Take(2).Should().BeEquivalentTo(alone, o => o.WithStrictOrdering());
        wareFirst.Should().BeEquivalentTo(crmFirst, o => o.WithStrictOrdering(), "貢獻區塊依應用程式 id 排序，與開啟順序無關");
    }

    [Fact]
    public async Task ExportingTheBuiltInCrm_PutsTheMastersInRequiresMasters_AndKeepsTheFixedValueConversion()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmBody);

        var (package, _) = await rig.ExportAsync("crm-sales");

        var requires = package.GetProperty("manifest").GetProperty("requires");
        (requires.TryGetProperty("apps", out var apps) ? apps.EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList() : [])
            .Should().NotContain(["partner", "contact"]);
        requires.GetProperty("masters").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().BeEquivalentTo(["partner", "contact"]);
        var groups = package.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("code").GetString()).ToList();
        groups.Should().OnlyHaveUniqueItems("同一個群組只有一個代號").And.HaveCount(2, "匯出用群組的 Key 當代號，主檔的 steward 群組不在 CRM 名下");
        package.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("code").GetString()).Should().NotContain(["customer", "contact"]);

        var contributions = package.GetProperty("contributions").EnumerateArray().ToList();
        contributions.Select(c => $"{c.GetProperty("master").GetString()}/{c.GetProperty("module").GetString()}")
            .Should().BeEquivalentTo(["partner/partner", "contact/contact"]);
        var partner = contributions.Single(c => c.GetProperty("master").GetString() == "partner");
        partner.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("code").GetString()).Should().Equal("industry", "fromLead");
        foreach (var c in contributions)
            c.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("group").GetString()).Should().BeEquivalentTo(groups);

        var conversion = package.GetProperty("conversions").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "crm-lead-to-customer");
        conversion.GetProperty("target").GetString().Should().Be("partner/partner");
        var set = conversion.GetProperty("header").GetProperty("set").EnumerateArray().Single();
        (set.GetProperty("to").GetString(), set.GetProperty("option").GetString()).Should().Be(("partner/partner.role", "customer"));
    }

    [Fact]
    public async Task OpeningOnlyMes_DoesNotOpenTheContactMaster()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("mes-mold", new { groupMap = new Dictionary<string, object>() });

        rig.StatusOf("contact").Should().BeNull("MES 沒有用到聯絡人");
        rig.Db(db => db.Set<App>().Count(a => a.Kind == AppKind.Master)).Should().Be(0);
        (await rig.Admin.GetAsync($"{ApiTestKit.Base}/modules/{BuiltInMasterTests.Contact}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
