using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建 MES 改用共用主檔往來對象（共用主檔設計 §11.2、§15.2「開啟」「匯出匯入」「往來對象與聯絡人行為」）：
/// 只開 MES 時自動開啟往來對象（沒有聯絡人）、MES 區塊、單據的客戶只收客戶或兩者、匯出內容。
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
}
