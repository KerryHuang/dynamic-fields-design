using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建 MES、進銷存改用共用主檔往來對象與聯絡人（共用主檔設計 §11.2、§11.3、§11.5、§15.2「開啟」「生命週期」「匯出匯入」「往來對象與聯絡人行為」）：
/// 只開其中一個時自動開啟的主檔、各自的貢獻區塊、單據的客戶與供應商守衛、匯出內容、三個內建應用程式任意順序開啟後主檔相同。
/// </summary>
public class BuiltInAppsMasterTests
{
    private const string Partner = BuiltInMasterTests.Partner;

    private static readonly object NoGroups = new { groupMap = new Dictionary<string, object>() };

    private static string Key(JsonElement app, string code) =>
        app.GetProperty("modules").EnumerateArray().Single(m => m.GetProperty("code").GetString() == code).GetProperty("key").GetString()!;

    private static async Task<Dictionary<string, string>> FieldKeys(HttpClient c, string moduleKey)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"));
        return module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("key").GetString()!);
    }

    [Fact]
    public async Task OpeningOnlyMes_OpensThePartnerMaster_WithTheMesBlock()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("mes-mold", NoGroups);
        await rig.AssertIntegrityAsync();

        rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Open);
        rig.StatusOf("contact").Should().BeNull("MES 沒有用到聯絡人");
        var blocks = rig.Blocks(Partner);
        blocks.Select(b => b.Block).Should().Equal("基本資料", "MES");
        blocks.Last().Fields.Should().Equal("信用等級:Enum:Active", "網站:Url:Active");
        (await rig.Admin.GetAsync($"{Base}/modules/mes-customer-info")).StatusCode.Should().Be(HttpStatusCode.NotFound, "客戶擴充模組已移除");
    }

    [Fact]
    public async Task MesQuotes_TakeCustomersOrBoth_NotSuppliers()
    {
        using var rig = new SharedMasterRig();
        var mes = await rig.OpenOkAsync("mes-mold", NoGroups);
        var q = await FieldKeys(rig.Admin, Key(mes, "quote"));

        async Task<HttpStatusCode> QuoteFor(long partner) =>
            (await rig.Admin.PostAsJsonAsync($"{Base}/modules/{Key(mes, "quote")}/records",
                new { values = new Dictionary<string, object> { [q["客戶"]] = partner } })).StatusCode;

        (await QuoteFor(await PartnerAsync(rig.Admin, "嵐岳精密"))).Should().Be(HttpStatusCode.Created);
        (await QuoteFor(await PartnerAsync(rig.Admin, "恆展工業", "客戶與供應商"))).Should().Be(HttpStatusCode.Created);
        (await QuoteFor(await PartnerAsync(rig.Admin, "昌盛鋼鐵", "供應商"))).Should().Be(HttpStatusCode.BadRequest, "供應商不能當 MES 的客戶");
    }

    [Fact]
    public async Task ExportingTheBuiltInMes_PutsPartnerInRequiresMasters_AndRebuildsTheMesContribution()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("mes-mold", NoGroups);

        var (package, _) = await rig.ExportAsync("mes-mold");

        var requires = package.GetProperty("manifest").GetProperty("requires");
        (requires.TryGetProperty("apps", out var apps) ? apps.EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList() : [])
            .Should().NotContain("partner");
        (requires.TryGetProperty("entities", out var entities) ? entities.EnumerateArray().Select(e => e.GetString()).ToList() : [])
            .Should().BeEmpty("MES 不再依賴核心實體 sales.customer");
        requires.GetProperty("masters").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().Equal("partner");
        package.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("code").GetString()).Should().NotContain("customer");
        var contribution = package.GetProperty("contributions").EnumerateArray().Should().ContainSingle().Which;
        $"{contribution.GetProperty("master").GetString()}/{contribution.GetProperty("module").GetString()}".Should().Be("partner/partner");
        contribution.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("code").GetString()).Should().Equal("creditLevel", "website");
    }

    // ---- 進銷存 ----

    private static async Task<HttpStatusCode> CreateAsync(HttpClient c, string module, Dictionary<string, object> values) =>
        (await c.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values })).StatusCode;

    [Fact]
    public async Task OpeningOnlyInventory_OpensPartnerAndContact_WithTheInventoryBlock_AndClosingItClosesBoth()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("inventory", NoGroups);
        await rig.AssertIntegrityAsync();

        rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Open);
        rig.StatusOf("contact").Should().Be(Domain.Entities.Dynamic.AppStatus.Open, "進銷存的聯絡人改用共用主檔聯絡人");
        var blocks = rig.Blocks(Partner);
        blocks.Select(b => b.Block).Should().Equal("基本資料", "進銷存");
        blocks.Last().Fields.Should().Equal("出貨地址:Text:Active");
        rig.Blocks(BuiltInMasterTests.Contact).Select(b => b.Block).Should().HaveCount(1, "進銷存對聯絡人只授予權限，不加區塊");
        foreach (var gone in new[] { "inv-supplier", "inv-customer" })
            (await rig.Admin.GetAsync($"{Base}/modules/{gone}")).StatusCode.Should().Be(HttpStatusCode.NotFound, gone);

        await rig.CloseOkAsync("inventory");
        rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Closed, "進銷存是唯一的使用者");
        rig.StatusOf("contact").Should().Be(Domain.Entities.Dynamic.AppStatus.Closed);
        await rig.AssertIntegrityAsync();
        await rig.ReopenOkAsync("inventory");
        rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Open);
        rig.StatusOf("contact").Should().Be(Domain.Entities.Dynamic.AppStatus.Open);
    }

    [Fact]
    public async Task InventoryDocuments_PurchasesTakeSuppliersOrBoth_ShipmentsTakeCustomersOrBoth()
    {
        using var rig = new SharedMasterRig();
        var inv = await rig.OpenOkAsync("inventory", NoGroups);
        var po = await FieldKeys(rig.Admin, Key(inv, "po"));
        var sh = await FieldKeys(rig.Admin, Key(inv, "shipment"));
        var warehouse = await FieldKeys(rig.Admin, Key(inv, "warehouse"));
        var wh = (await JsonOf(await rig.Admin.PostAsJsonAsync($"{Base}/modules/{Key(inv, "warehouse")}/records",
            new { values = new Dictionary<string, object> { [warehouse["倉庫代號"]] = "A", [warehouse["名稱"]] = "主倉" } }))).GetProperty("id").GetInt64();
        var supplier = await PartnerAsync(rig.Admin, "昌盛鋼鐵", "供應商");
        var customer = await PartnerAsync(rig.Admin, "遠航機械");
        var both = await PartnerAsync(rig.Admin, "恆展工業", "客戶與供應商");

        Task<HttpStatusCode> Po(long partner) => CreateAsync(rig.Admin, Key(inv, "po"), new() { [po["供應商"]] = partner });
        Task<HttpStatusCode> Ship(long partner) => CreateAsync(rig.Admin, Key(inv, "shipment"), new() { [sh["客戶"]] = partner, [sh["出庫倉"]] = wh });

        (await Po(supplier)).Should().Be(HttpStatusCode.Created);
        (await Po(both)).Should().Be(HttpStatusCode.Created);
        (await Po(customer)).Should().Be(HttpStatusCode.BadRequest, "客戶不能當採購單的供應商");
        (await Ship(customer)).Should().Be(HttpStatusCode.Created);
        (await Ship(both)).Should().Be(HttpStatusCode.Created);
        (await Ship(supplier)).Should().Be(HttpStatusCode.BadRequest, "供應商不能當出貨單的客戶");
    }

    [Fact]
    public async Task InventoryGroups_GetTheirRightsOnTheMastersFromTheContributions()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        await InventoryRig.OpenAsync(admin);
        var supplier = await PartnerAsync(admin, "昌盛鋼鐵", "供應商");
        var c = await FieldKeys(admin, BuiltInMasterTests.Contact);
        var p = await FieldKeys(admin, Partner);

        // 採購：往來對象與聯絡人都能新增修改，但不能刪除（刪除留給主檔的 steward）
        var buyer = app.AsUser(InventoryRig.Buyer);
        (await CreateAsync(buyer, Partner, new() { [p["名稱"]] = "宏達五金", [p["出貨地址"]] = "示範市中正路 20 號" })).Should().Be(HttpStatusCode.Created);
        var contact = await buyer.PostAsJsonAsync($"{Base}/modules/{BuiltInMasterTests.Contact}/records",
            new { values = new Dictionary<string, object> { [c["姓名"]] = "林經理", [c["目前公司"]] = supplier } });
        contact.StatusCode.Should().Be(HttpStatusCode.Created, await contact.Content.ReadAsStringAsync());
        var record = await JsonOf(await buyer.GetAsync($"{Base}/records/{supplier}"));
        (await buyer.DeleteAsync($"{Base}/records/{supplier}?rowVersion={Uri.EscapeDataString(record.GetProperty("rowVersion").GetString()!)}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 倉管：只讀
        var keeper = app.AsUser(InventoryRig.Keeper);
        (await keeper.GetAsync($"{Base}/records/{supplier}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(keeper, BuiltInMasterTests.Contact, new() { [c["姓名"]] = "陳小姐", [c["目前公司"]] = supplier }))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExportingTheBuiltInInventory_PutsPartnerAndContactInRequiresMasters()
    {
        using var rig = new SharedMasterRig();
        // 只授權限、沒有欄位的聯絡人貢獻要有群組拿到權限才會匯出
        await rig.OpenOkAsync("inventory", new { groupMap = new Dictionary<string, object> { ["buyer"] = new { mode = "create" } } });

        var (package, _) = await rig.ExportAsync("inventory");

        var requires = package.GetProperty("manifest").GetProperty("requires");
        (requires.TryGetProperty("apps", out var apps) ? apps.EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList() : [])
            .Should().NotContain(["partner", "contact"]);
        requires.GetProperty("masters").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().BeEquivalentTo(["partner", "contact"]);
        package.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("code").GetString()).Should().NotContain(["supplier", "customer"]);
        var contributions = package.GetProperty("contributions").EnumerateArray().ToList();
        contributions.Select(x => $"{x.GetProperty("master").GetString()}/{x.GetProperty("module").GetString()}")
            .Should().BeEquivalentTo(["partner/partner", "contact/contact"]);
        contributions.Single(x => x.GetProperty("master").GetString() == "partner").GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("code").GetString()).Should().Equal("shipAddress");
    }

    // ---- 三個內建應用程式的開啟順序（§11.5 最後一點、§15.2「開啟」）----

    public static IEnumerable<object[]> OpeningOrders =>
        new[] { "crm-sales", "mes-mold", "inventory" }.Permutations().Select(o => new object[] { o });

    [Theory]
    [MemberData(nameof(OpeningOrders))]
    public async Task AnyOpeningOrderOfTheThreeBuiltInApps_GivesTheSameMasterFieldsAndBlockSequence(string[] order)
    {
        using var rig = new SharedMasterRig();
        foreach (var id in order) await rig.OpenOkAsync(id, id == "crm-sales" ? CrmMasterTests.CrmBody : NoGroups);
        await rig.AssertIntegrityAsync();

        var partner = rig.Blocks(Partner);
        partner.Select(b => b.Block).Should().Equal(["基本資料", "CRM", "進銷存", "MES"], "基本區塊在前，貢獻區塊依應用程式 id（crm-sales、inventory、mes-mold）排序");
        partner[0].Fields.Should().Equal("編號:AutoNumber:Active", "名稱:Text:Active", "角色:Enum:Active", "統一編號:Text:Active", "電話:Phone:Active",
            "Email:Email:Active", "地址:Text:Active", "負責人:User:Active", "銷售付款條件:Text:Active", "採購付款條件:Text:Active", "啟用:Boolean:Active");
        partner[1].Fields.Should().Equal("產業:Enum:Active", "來源潛在客戶:Reference:Active");
        partner[2].Fields.Should().Equal("出貨地址:Text:Active");
        partner[3].Fields.Should().Equal("信用等級:Enum:Active", "網站:Url:Active");
        rig.Blocks(BuiltInMasterTests.Contact).Should().ContainSingle("CRM 與進銷存對聯絡人只授予權限");
        rig.Db(db => db.Set<Domain.Entities.Dynamic.App>().Count(a => a.Kind == Domain.Entities.Dynamic.AppKind.Master)).Should().Be(2);
    }
}

internal static class PermutationExtensions
{
    public static IEnumerable<T[]> Permutations<T>(this IReadOnlyList<T> items) =>
        items.Count <= 1
            ? [items.ToArray()]
            : items.SelectMany((x, i) => items.Where((_, j) => j != i).ToList().Permutations().Select(rest => (T[])[x, .. rest]));
}
