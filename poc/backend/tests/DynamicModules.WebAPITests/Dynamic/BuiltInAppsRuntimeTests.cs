using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建 CRM、MES、進銷存三個套件同時開啟時共用往來對象的執行期行為（s2d-1f 審查建議）：貢獻欄位只給貢獻者的群組看、
/// 關閉應用程式後權限收回與欄位凍結、單據的角色守衛、進銷存倉管與查詢群組對往來對象的權限。
/// 群組一律對應到 DemoPeople 的示範群組：採購 buyer01、倉管 keeper01、進銷存主管 ops01、查詢 planner01，MES 業務 sales01；CRM 的群組新建、沒有成員。
/// </summary>
public class BuiltInAppsRuntimeTests : IDisposable
{
    private const string Partner = BuiltInMasterTests.Partner;
    private const string Buyer = "buyer01", Keeper = "keeper01", Viewer = "planner01", MesSales = "sales01";

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private object Map(params (string Code, string GroupKey)[] map) => new
    {
        groupMap = map.ToDictionary(m => m.Code, m => (object)new { mode = "map", groupId = _rig.GroupId(m.GroupKey) })
    };

    private static readonly object CrmNewGroups = new
    {
        groupMap = new Dictionary<string, object>
        {
            ["sales"] = new { mode = "create" }, ["manager"] = new { mode = "create" }, ["designer"] = new { mode = "create" }
        }
    };

    private Task OpenCrmAsync() => _rig.OpenOkAsync("crm-sales", CrmNewGroups);

    private Task OpenMesAsync() => _rig.OpenOkAsync("mes-mold", Map(("sales", "sales")));

    private async Task<InventoryRig> OpenInventoryAsync() => await InventoryRig.OpenAsync(_rig.Admin, assignMembers: false, groupMap: Map(
        ("buyer", "purchasing"), ("keeper", "warehouse"), ("manager", "ops-manager"), ("viewer", "inv-viewer")));

    private async Task<(Dictionary<string, string> Keys, JsonElement[] Fields)> PartnerSchemaAsync()
    {
        var module = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{Partner}"));
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToArray();
        return (fields.ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("key").GetString()!), fields);
    }

    private static string OptionOf(JsonElement[] fields, string field, string label) =>
        fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == field).GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("label").GetProperty("zh-TW").GetString() == label).GetProperty("value").GetString()!;

    private static async Task<HashSet<string>> ValuesSeenAsync(HttpClient c, long id)
    {
        var res = await c.GetAsync($"{Base}/records/{id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("values").EnumerateObject().Select(v => v.Name).ToHashSet();
    }

    private async Task<HttpResponseMessage> PutPartnerAsync(HttpClient c, long id, Dictionary<string, object> values)
    {
        var fresh = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{id}"));
        return await c.PutAsJsonAsync($"{Base}/records/{id}", new { values, rowVersion = fresh.GetProperty("rowVersion").GetString() });
    }

    /// <summary>一家客戶，CRM 的產業、MES 的信用等級與進銷存的出貨地址都有值。</summary>
    private async Task<long> FullPartnerAsync()
    {
        var (k, fields) = await PartnerSchemaAsync();
        var values = new Dictionary<string, object>
        {
            [k["名稱"]] = "晨曦機械", [k["角色"]] = OptionOf(fields, "角色", "客戶"), [k["產業"]] = OptionOf(fields, "產業", "製造"),
            [k["信用等級"]] = OptionOf(fields, "信用等級", "A")
        };
        if (k.TryGetValue("出貨地址", out var address)) values[address] = "示範市中正路 20 號";   // 進銷存沒開時沒有這個欄位
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Partner}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ContributedFields_OnlyShowToTheGroupsOfTheAppThatContributedThem()
    {
        await OpenCrmAsync();
        await OpenMesAsync();
        await OpenInventoryAsync();
        var (k, _) = await PartnerSchemaAsync();
        var id = await FullPartnerAsync();

        var buyer = await ValuesSeenAsync(_rig.App.AsUser(Buyer), id);
        buyer.Should().Contain([k["名稱"], k["出貨地址"]]).And.NotContain([k["產業"], k["信用等級"], k["網站"]], "採購看不到 CRM 的產業與 MES 的信用等級");

        var sales = await ValuesSeenAsync(_rig.App.AsUser(MesSales), id);
        sales.Should().Contain([k["名稱"], k["信用等級"]]).And.NotContain([k["出貨地址"], k["產業"]], "MES 業務看不到進銷存的出貨地址與 CRM 的產業");
    }

    [Fact]
    public async Task ClosingMes_TakesBackTheMesSalesWrites_AndFreezesTheCreditLevel_WhileCrmKeepsThePartnerOpen()
    {
        await OpenCrmAsync();
        await OpenMesAsync();
        var (k, fields) = await PartnerSchemaAsync();
        var id = await FullPartnerAsync();
        var sales = _rig.App.AsUser(MesSales);

        (await PutPartnerAsync(sales, id, new() { [k["電話"]] = "02-5550-1000" })).StatusCode.Should().Be(HttpStatusCode.OK, "MES 業務可以修改往來對象");

        await _rig.CloseOkAsync("mes-mold");
        _rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Open, "CRM 還在用");

        (await PutPartnerAsync(sales, id, new() { [k["電話"]] = "02-5550-2000" })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "權限隨 MES 關閉收回");
        var frozen = await PutPartnerAsync(_rig.Admin, id, new() { [k["信用等級"]] = OptionOf(fields, "信用等級", "B") });
        frozen.StatusCode.Should().Be(HttpStatusCode.Conflict, "信用等級是已關閉的 MES 的欄位，管理員也不能改");
        (await PutPartnerAsync(_rig.Admin, id, new() { [k["信用等級"]] = OptionOf(fields, "信用等級", "A"), [k["電話"]] = "02-5550-3000" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "值不變的凍結欄位照常通過");
    }

    [Fact]
    public async Task InventoryKeeperCannotEditThePartner_AndTheViewerGroupCannotReadIt()
    {
        var inv = await OpenInventoryAsync();
        var (k, _) = await PartnerSchemaAsync();
        var customer = await inv.CustomerAsync("晨曦機械");

        (await _rig.App.AsUser(Keeper).GetAsync($"{Base}/records/{customer}")).StatusCode.Should().Be(HttpStatusCode.OK, "倉管讀得到");
        (await PutPartnerAsync(_rig.App.AsUser(Keeper), customer, new() { [k["電話"]] = "02-5550-1000" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "倉管對往來對象只讀");
        (await _rig.App.AsUser(Viewer).GetAsync($"{Base}/records/{customer}")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "進銷存查詢群組只讀品項與倉庫，不讀往來對象");
        (await _rig.App.AsUser(Buyer).GetAsync($"{Base}/records/{customer}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InventoryDocuments_RoleGuardAlsoHoldsOnUpdate()
    {
        var inv = await OpenInventoryAsync();
        var supplier = await inv.SupplierAsync("昌盛鋼鐵");
        var customer = await inv.CustomerAsync("晨曦機械");
        var item = await inv.ItemAsync("導柱");
        var po = await inv.PoDraftAsync(supplier, item, 5);
        var h = await inv.F("po");

        var res = await _rig.Admin.PutAsJsonAsync($"{Base}/records/{InventoryRig.Id(po)}", new
        {
            values = new Dictionary<string, object> { [h["供應商"]] = customer }, rowVersion = po.GetProperty("rowVersion").GetString()
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "客戶不能改成採購單的供應商：" + await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConvertingAPo_ToAReceipt_Is400_WhenTheSupplierIsNoLongerASupplier()
    {
        var inv = await OpenInventoryAsync();
        var (k, fields) = await PartnerSchemaAsync();
        var supplier = await inv.SupplierAsync("昌盛鋼鐵");
        var item = await inv.ItemAsync("導柱");
        await inv.WarehouseAsync("A", "主倉");
        var draft = await inv.PoDraftAsync(supplier, item, 5);
        var po = await inv.ConfirmPoAsync(_rig.Admin, draft, _rig.Admin, "admin");
        var lineId = po.GetProperty("details").GetProperty(inv.Key("po-line"))[0].GetProperty("id").GetInt64();

        // 角色後來被改成客戶：原本合格的採購單轉到貨單時，到貨單的供應商守衛擋下
        var flip = await PutPartnerAsync(_rig.Admin, supplier, new() { [k["角色"]] = OptionOf(fields, "角色", "客戶") });
        flip.StatusCode.Should().Be(HttpStatusCode.OK, await flip.Content.ReadAsStringAsync());

        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/records/{InventoryRig.Id(po)}/conversions/inv-po-to-receipt/convert",
            new { lines = new[] { new { sourceLineId = lineId, quantity = 5 } } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }
}
