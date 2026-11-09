using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建的共用主檔往來對象（partner）與聯絡人（contact）的行為（共用主檔設計 §5.1、§5.2、§15.2「往來對象與聯絡人行為」）。
/// 用只在測試裡的使用者套件 trade-lite：銷售單的客戶只收客戶或兩者，採購單的供應商只收供應商或兩者。
/// </summary>
public class BuiltInMasterTests : IDisposable
{
    public const string Partner = "master-partner", Contact = "master-contact", Employment = "master-employment";

    private const string Trade = """
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "trade-lite", "version": "1.0.0", "name": { "zh-TW": "買賣（測試）" },
            "requires": { "masters": [ { "id": "partner", "modules": ["partner"] }, { "id": "contact", "modules": ["contact"] } ] } },
          "groups": [],
          "modules": [
            { "code": "sale", "key": "trade-sale", "name": { "zh-TW": "銷售單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "customer", "type": "reference", "label": { "zh-TW": "客戶" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "partner" },
                    "refGuard": { "field": "partner/partner.role", "in": ["customer", "both"] } } }
              ] } ], "permissions": [] },
            { "code": "buy", "key": "trade-buy", "name": { "zh-TW": "採購單" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "supplier", "type": "reference", "label": { "zh-TW": "供應商" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "partner" },
                    "refGuard": { "field": "partner/partner.role", "in": ["supplier", "both"] } } }
              ] } ], "permissions": [] }
          ]
        }
        """;

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    public static object Body(object? groupMap = null) => new
    {
        groupMap = groupMap ?? new Dictionary<string, object>(),
        masters = new Dictionary<string, object>
        {
            ["partner"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } },
            ["contact"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } }
        }
    };

    private async Task OpenTradeAsync()
    {
        await _rig.ImportAsync(Trade);
        await _rig.OpenOkAsync("trade-lite", Body());
    }

    private long OptionId(string appKey, string name) => _rig.Db(db =>
        (from a in db.Set<App>() where a.Key == appKey
         join o in db.Set<AppOption>() on a.Id equals o.AppId
         where o.Name == name select o.OptionId).Single());

    private async Task<long> PartnerAsync(string name, string? role)
    {
        var values = new Dictionary<string, object> { [_rig.FieldKey("partner", "partner.name")] = name };
        if (role is not null) values[_rig.FieldKey("partner", "partner.role")] = $"opt_{OptionId("partner", $"partner.{role}")}";
        return (await _rig.CreateRecordAsync(_rig.Admin, Partner, values)).GetProperty("id").GetInt64();
    }

    private Task<HttpResponseMessage> DocAsync(string module, string field, long partner) =>
        _rig.Admin.PostAsJsonAsync($"{Base}/modules/trade-{module}/records", new
        {
            values = new Dictionary<string, object> { [_rig.FieldKey("trade-lite", $"{module}.no")] = "D-1", [_rig.FieldKey("trade-lite", $"{module}.{field}")] = partner }
        });

    [Fact]
    public async Task OpeningAUserOfContact_OpensTheBuiltInPartnerAndContact_WithTheEmploymentDetail()
    {
        await OpenTradeAsync();

        _rig.StatusOf("partner").Should().Be(AppStatus.Open);
        _rig.StatusOf("contact").Should().Be(AppStatus.Open);
        _rig.Db(db => db.Set<App>().Where(a => a.Kind == AppKind.Master).Select(a => a.Key).ToList()).Should().BeEquivalentTo(["partner", "contact"]);
        _rig.Db(db => db.Set<Module>().Where(m => m.Key.StartsWith("master-")).Select(m => m.Key).ToList())
            .Should().BeEquivalentTo([Partner, Contact, Employment]);
        var packages = await JsonOf(await _rig.Admin.GetAsync($"{SharedMasterRig.Apps}/packages"));
        packages.EnumerateArray().Select(p => p.GetProperty("id").GetString()).Should().NotContain(["partner", "contact"], "主檔不能單獨開啟");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task RefGuard_SalesTakeCustomersOrBoth_PurchasesTakeSuppliersOrBoth()
    {
        await OpenTradeAsync();
        var customer = await PartnerAsync("晨曦機械", "customer");
        var supplier = await PartnerAsync("遠見電子", "supplier");
        var both = await PartnerAsync("恆展工業", "both");
        var none = await PartnerAsync("新航物流", null);

        (await DocAsync("sale", "customer", customer)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await DocAsync("sale", "customer", both)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await DocAsync("sale", "customer", supplier)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DocAsync("sale", "customer", none)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "沒有角色的往來對象兩邊都選不到");
        (await DocAsync("buy", "supplier", supplier)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await DocAsync("buy", "supplier", both)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await DocAsync("buy", "supplier", customer)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AContactNeedsACompany_KeepsItsEmploymentHistory_AndTheCompanyPageListsCurrentContacts()
    {
        await OpenTradeAsync();
        var before = await PartnerAsync("晨曦機械", "customer");
        var now = await PartnerAsync("遠見電子", "customer");
        string name = _rig.FieldKey("contact", "contact.name"), current = _rig.FieldKey("contact", "contact.partner");

        var orphan = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Contact}/records",
            new { values = new Dictionary<string, object> { [name] = "沒有公司" } });
        orphan.StatusCode.Should().Be(HttpStatusCode.BadRequest, "聯絡人必填目前公司");

        string company = _rig.FieldKey("contact", "employment.partner"), title = _rig.FieldKey("contact", "employment.title"),
            start = _rig.FieldKey("contact", "employment.startDate"), end = _rig.FieldKey("contact", "employment.endDate");
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Contact}/records", new
        {
            values = new Dictionary<string, object> { [name] = "林怡君", [current] = now },
            details = new Dictionary<string, object[]>
            {
                [Employment] =
                [
                    new { values = new Dictionary<string, object> { [company] = before, [title] = "採購專員", [start] = "2021-03-01", [end] = "2024-06-30" } },
                    new { values = new Dictionary<string, object> { [company] = now, [title] = "採購經理", [start] = "2024-07-01" } }
                ]
            }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync() + _rig.LastError());
        var contact = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{(await JsonOf(res)).GetProperty("id").GetInt64()}"));
        contact.GetProperty("details").GetProperty(Employment).GetArrayLength().Should().Be(2, "聯絡人頁看得到他待過哪幾家");

        async Task<Dictionary<long, int>> Tabs(long partner)
        {
            var tabs = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{partner}/related"));
            var counts = new Dictionary<long, int>();
            foreach (var tab in tabs.EnumerateArray())
            {
                var field = tab.GetProperty("fieldId").GetInt64();
                var paged = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{partner}/related/{field}/paged?page=1&size=20"));
                counts[field] = paged.GetProperty("totalCount").GetInt32();
            }
            return counts;
        }
        // 「待過的人」（任職經歷明細的公司欄位）不在公司頁：核心的反向關聯不列明細模組，見結果檔待決事項
        var currentId = long.Parse(current[2..]);
        (await Tabs(now)).GetValueOrDefault(currentId).Should().Be(1, "公司頁列出目前的聯絡人");
        (await Tabs(before)).GetValueOrDefault(currentId).Should().Be(0, "離職的人不是前公司目前的聯絡人");
    }
}
