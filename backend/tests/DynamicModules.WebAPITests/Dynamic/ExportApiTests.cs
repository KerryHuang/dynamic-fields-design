using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class ExportApiTests(TestApp app) : IClassFixture<TestApp>
{
    private readonly HttpClient _admin = app.AsUser();

    [Fact]
    public async Task ExportCsv_ReturnsCsvAttachmentWithTimestampedName()
    {
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
        app.GrantBusinessGroups(key);
        var title = await TitleKey(_admin, key);
        (await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "t1" } })).EnsureSuccessStatusCode();

        var res = await _admin.GetAsync($"{Base}/modules/{key}/export?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.ToString().Should().Be("text/csv; charset=utf-8");
        res.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        res.Content.Headers.ContentDisposition.FileName.Should().MatchRegex($"^{Regex.Escape(key)}-\\d{{8}}-\\d{{4}}\\.csv$");
        var bytes = await res.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
    }

    [Fact]
    public async Task Export_BadFormat_Returns400()
    {
        var key = await CreateModule(_admin);
        await Activate(_admin, key);

        var res = await _admin.GetAsync($"{Base}/modules/{key}/export?format=pdf");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty("format")[0].GetString().Should().Be("格式只能是 csv 或 xlsx");
    }
}
