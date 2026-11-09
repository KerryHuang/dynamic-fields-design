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

    [Fact]
    public async Task HeaderSet_OnAPlainModuleTarget_IsRejectedAtImport()
    {
        var res = await Import(AppTestPackagesV6.LocalSet);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("conversions[0].header.set").And.Contain("還不能執行轉換的固定值");
        (await _admin.PostAsJsonAsync($"{Apps}/packages/set-lite/open", new { })).IsSuccessStatusCode.Should().BeFalse();
    }

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
        // 這份套件的停用貢獻欄位 credit 被同一份的公式引用，開啟時停用會被既有規則擋下（400）；這裡只驗開啟流程，改成啟用
        await ImportOk(AppTestPackagesV6.Crm.Replace("\"config\": {}, \"active\": false }", "\"config\": {} }"));
        var direct = await _admin.PostAsJsonAsync($"{Apps}/packages/partner-lite/open", new { });
        direct.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await direct.Content.ReadAsStringAsync()).Should().Contain("共用主檔");
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/crm6-lite/open", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var apps = await _admin.GetStringAsync($"{Apps}");
        apps.Should().Contain("crm6-lite").And.Contain("partner-lite");
    }
}
