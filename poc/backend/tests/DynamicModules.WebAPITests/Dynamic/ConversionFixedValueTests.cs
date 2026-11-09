using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 轉換的固定值 header.set（共用主檔設計 §4.5）：開啟時建立、轉換時寫入且不能被對話框蓋掉、隨應用程式匯出、
/// 設計器存檔不帶 set 時保留。用 <see cref="AppTestPackagesV6.LocalSet"/>（目標是一般模組）；目標是主檔的情境由內建 CRM 的測試涵蓋。
/// </summary>
public class ConversionFixedValueTests : IDisposable
{
    private const string Conversion = "set-lead-to-customer";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private long OptionId(string appKey, string name) => _rig.Db(db =>
        (from a in db.Set<App>() where a.Key == appKey
         join o in db.Set<AppOption>() on a.Id equals o.AppId
         where o.Name == name select o.OptionId).Single());

    private async Task<(string Company, string Name, string Role, long Customer, long Supplier)> OpenAsync()
    {
        await _rig.ImportAsync(AppTestPackagesV6.LocalSet);
        await _rig.OpenOkAsync("set-lite", new { groupMap = new Dictionary<string, object>() });
        return (_rig.FieldKey("set-lite", "lead.company"), _rig.FieldKey("set-lite", "customer.name"), _rig.FieldKey("set-lite", "customer.role"),
            OptionId("set-lite", "customer.customer"), OptionId("set-lite", "customer.supplier"));
    }

    private async Task<HttpResponseMessage> ConvertAsync(string companyKey, object? values = null)
    {
        var lead = await _rig.CreateRecordAsync(_rig.Admin, "set-lead", new Dictionary<string, object> { [companyKey] = "晨曦機械" });
        return await _rig.Admin.PostAsJsonAsync($"{Base}/records/{lead.GetProperty("id").GetInt64()}/conversions/{Conversion}/convert",
            new { values = values ?? new Dictionary<string, object>() });
    }

    [Fact]
    public async Task Converting_WritesTheFixedOption_AndTheDialogCannotOverrideIt()
    {
        var k = await OpenAsync();

        var plain = await ConvertAsync(k.Company);
        plain.StatusCode.Should().Be(HttpStatusCode.Created, await plain.Content.ReadAsStringAsync() + _rig.LastError());
        var created = await JsonOf(plain);
        created.GetProperty("values").GetProperty(k.Name).GetString().Should().Be("晨曦機械");
        created.GetProperty("values").GetProperty(k.Role).GetString().Should().Be($"opt_{k.Customer}");

        var overridden = await ConvertAsync(k.Company, new Dictionary<string, object> { [k.Role] = $"opt_{k.Supplier}" });
        overridden.StatusCode.Should().Be(HttpStatusCode.Created, await overridden.Content.ReadAsStringAsync());
        (await JsonOf(overridden)).GetProperty("values").GetProperty(k.Role).GetString().Should().Be($"opt_{k.Customer}", "固定值排在使用者覆寫之後");
    }

    [Fact]
    public async Task TheFixedValue_IsExported_WithFieldAndOptionCodes()
    {
        await OpenAsync();

        var (package, _) = await _rig.ExportAsync("set-lite");

        var header = package.GetProperty("conversions").EnumerateArray().Single().GetProperty("header");
        var set = header.GetProperty("set").EnumerateArray().Single();
        set.GetProperty("to").GetString().Should().Be("customer.role");
        set.GetProperty("option").GetString().Should().Be("customer");
    }

    [Fact]
    public async Task SavingInTheDesignerWithoutSet_KeepsIt_AnEmptyListClearsIt()
    {
        await OpenAsync();
        var current = await JsonOf(await _rig.Admin.GetAsync($"{Base}/conversions/{Conversion}"));
        current.GetProperty("definition").GetProperty("header").GetProperty("set").GetArrayLength().Should().Be(1);

        async Task<JsonElement> Save(object? set)
        {
            var header = current.GetProperty("definition").GetProperty("header");
            var definition = new
            {
                sourceStates = Array.Empty<long>(),
                header = new { sourceFieldId = header.GetProperty("sourceFieldId").GetInt64(), copy = header.GetProperty("copy"), set },
                lines = (object?)null
            };
            var res = await _rig.Admin.PutAsJsonAsync($"{Base}/conversions/{Conversion}", new
            {
                key = Conversion, name = current.GetProperty("name"), sourceModuleKey = "set-lead", targetModuleKey = "set-customer",
                active = true, definition
            });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
            return (await JsonOf(await _rig.Admin.GetAsync($"{Base}/conversions/{Conversion}"))).GetProperty("definition").GetProperty("header");
        }

        (await Save(null)).GetProperty("set").GetArrayLength().Should().Be(1, "前端設計器不送 set");
        (await Save(Array.Empty<object>())).GetProperty("set").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TheDesigner_RejectsAFixedValueOnANonEnumField_OrAnOptionOfAnotherField()
    {
        var k = await OpenAsync();
        var current = await JsonOf(await _rig.Admin.GetAsync($"{Base}/conversions/{Conversion}"));
        var header = current.GetProperty("definition").GetProperty("header");
        var nameId = long.Parse(k.Name[2..]);
        var roleId = long.Parse(k.Role[2..]);

        foreach (var set in new[] { new { fieldId = nameId, optionId = k.Customer }, new { fieldId = roleId, optionId = 999999L } })
        {
            var res = await _rig.Admin.PutAsJsonAsync($"{Base}/conversions/{Conversion}", new
            {
                key = Conversion, name = current.GetProperty("name"), sourceModuleKey = "set-lead", targetModuleKey = "set-customer", active = true,
                definition = new
                {
                    sourceStates = Array.Empty<long>(),
                    header = new { sourceFieldId = header.GetProperty("sourceFieldId").GetInt64(), copy = header.GetProperty("copy"), set = new[] { set } },
                    lines = (object?)null
                }
            });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await res.Content.ReadAsStringAsync()).Should().Contain("definition.header.set");
        }
    }
}
