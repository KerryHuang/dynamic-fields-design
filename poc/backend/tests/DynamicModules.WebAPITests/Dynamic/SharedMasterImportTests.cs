using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>格式 6 的套件經匯入端點：已匯入的主檔參與驗證與循環檢查；共用主檔的開啟在第 4 步之前一律擋下。</summary>
public class SharedMasterImportTests : IDisposable
{
    private const string Apps = Base + "/apps";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public SharedMasterImportTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private Task<HttpResponseMessage> Import(string json) =>
        _admin.PostAsync($"{Apps}/packages/import", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task ImportOk(string json)
    {
        var res = await Import(json);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MasterThenUser_Import()
    {
        await ImportOk(AppTestPackagesV6.Master);
        await ImportOk(AppTestPackagesV6.Crm);
    }

    /// <summary>開啟流程支援固定值之後，header.set 照常匯入（執行見 ConversionFixedValueTests）。</summary>
    [Fact]
    public async Task HeaderSet_OnAPlainModuleTarget_IsImported() => await ImportOk(AppTestPackagesV6.LocalSet);

    [Fact]
    public async Task Contribution_NullOption_Is400AtImport()
    {
        await ImportOk(AppTestPackagesV6.Master);
        var json = AppTestPackagesV6.Crm.Replace("\"options\": [ { \"code\": \"manufacturing\", \"label\": { \"zh-TW\": \"製造\" } } ]", "\"options\": [null]");
        json.Should().NotBe(AppTestPackagesV6.Crm);
        var res = await Import(json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("contributions[0].fields[industry].options: 不能有 null");
    }

    [Fact]
    public async Task Contribution_AnInactiveFieldUsedByAFormula_Is400AtImport()
    {
        await ImportOk(AppTestPackagesV6.Master);
        const string credit = "{ \"code\": \"credit\", \"type\": \"currency\", \"label\": { \"zh-TW\": \"信用額度\" }, \"config\": {} }";
        var json = AppTestPackagesV6.Crm.Replace(credit, credit.Replace("\"config\": {} }", "\"config\": {}, \"active\": false }"))
            .Replace("\"resultType\": \"number\" }, \"active\": false }", "\"resultType\": \"number\" } }");
        json.Should().NotBe(AppTestPackagesV6.Crm);
        var res = await Import(json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("此欄位被公式或規則引用，不能停用");
    }

    /// <summary>審查待決 8：主檔套件自己的模組欄位 active:false 又被公式引用，和貢獻欄位一樣在匯入時擋下。</summary>
    [Fact]
    public async Task Master_AnInactiveModuleFieldUsedByAFormula_Is400AtImport()
    {
        const string taxId = "{ \"code\": \"taxId\", \"type\": \"text\", \"label\": { \"zh-TW\": \"統一編號\" }, \"config\": {}, \"active\": false }";
        AppTestPackagesV6.Master.Should().Contain(taxId);
        var json = AppTestPackagesV6.Master.Replace(taxId, taxId
            + ", { \"code\": \"taxLen\", \"type\": \"formula\", \"label\": { \"zh-TW\": \"長度\" }, \"config\": { \"formula\": \"{taxId}\", \"resultType\": \"text\" } }");
        var res = await Import(json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("modules[partner].fields[taxId].active: 此欄位被公式或規則引用，不能停用（taxLen）");
        await ImportOk(AppTestPackagesV6.Master);
    }

    [Fact]
    public async Task Master_AnInactiveModuleFieldUsedByARule_Is400AtImport()
    {
        const string permissions = "\"permissions\": [ { \"group\": \"steward\"";
        var json = AppTestPackagesV6.Master.Replace(permissions,
            "\"rules\": [ { \"kind\": \"readOnly\", \"targets\": [\"field:taxId\"], \"active\": true, \"condition\": { \"all\": [] } } ],\n            " + permissions);
        json.Should().NotBe(AppTestPackagesV6.Master);
        var res = await Import(json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("modules[partner].fields[taxId].active: 此欄位被公式或規則引用，不能停用（rules）");
    }

    [Fact]
    public async Task User_ReferencingAFieldTheImportedMasterLacks_IsRejected()
    {
        await ImportOk(AppTestPackagesV6.Master);
        var res = await Import(AppTestPackagesV6.Crm.Replace("\"from\": \"partner-lite/partner.phone\"", "\"from\": \"partner-lite/partner.fax\""));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("partner-lite/partner.fax 不是主檔 partner-lite 的基本欄位");
    }

    [Fact]
    public async Task RequiresApps_ListingAnImportedMaster_IsRejected()
    {
        await ImportOk(AppTestPackagesV6.Master);
        var json = AppTestPackages.Quote.Replace("\"id\": \"crm-lite\", \"modules\": [\"customer\"]", "\"id\": \"partner-lite\", \"modules\": [\"partner\"]")
            .Replace("\"key\": \"customer\", \"app\": \"crm-lite\"", "\"key\": \"partner\", \"app\": \"partner-lite\"");
        var res = await Import(json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("partner-lite 是共用主檔，要列在 requires.masters");
    }

    [Fact]
    public async Task MastersRequiringEachOther_SecondImportIsRejectedAsACycle()
    {
        string Pkg(string id, string requires) => AppTestPackagesV6.Master
            .Replace("\"id\": \"partner-lite\"", $"\"id\": \"{id}\"").Replace("master-partner-lite", $"master-{id}")
            .Replace("\"name\": { \"zh-TW\": \"往來對象（測試）\" } },",
                $"\"name\": {{ \"zh-TW\": \"x\" }}, \"requires\": {{ \"masters\": [ {{ \"id\": \"{requires}\", \"modules\": [\"partner\"] }} ] }} }},");
        await ImportOk(Pkg("m-alpha", "m-beta"));
        var res = await Import(Pkg("m-beta", "m-alpha"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("循環");
    }

    [Fact]
    public async Task OpeningAMasterDirectly_Is409_ButItsUserOpensAndBindsIt()
    {
        await ImportOk(AppTestPackagesV6.Master);
        await ImportOk(AppTestPackagesV6.Crm);
        var direct = await _admin.PostAsJsonAsync($"{Apps}/packages/partner-lite/open", new { });
        direct.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await direct.Content.ReadAsStringAsync()).Should().Contain("共用主檔");
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/crm6-lite/open", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var apps = await _admin.GetStringAsync($"{Apps}");
        apps.Should().Contain("crm6-lite").And.Contain("partner-lite");
    }
}
