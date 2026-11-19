using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

public class ImportApiTests(TestApp app) : IClassFixture<TestApp>
{
    private readonly HttpClient _admin = app.AsUser();

    private static MultipartFormDataContent Upload(byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", "import.csv" } };
    }

    private async Task<(string Key, string Title)> Module()
    {
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
        return (key, await TitleKey(_admin, key));
    }

    [Fact]
    public async Task Import_PreviewThenCommit()
    {
        var (key, title) = await Module();
        var csv = Encoding.UTF8.GetBytes($"{title}\r\na\r\nb\r\n");

        var preview = await _admin.PostAsync($"{Base}/modules/{key}/import", Upload(csv));
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var p = await JsonOf(preview);
        p.GetProperty("total").GetInt32().Should().Be(2);
        p.GetProperty("valid").GetInt32().Should().Be(2);
        p.GetProperty("columns")[0].GetProperty("fieldKey").GetString().Should().Be(title);
        p.GetProperty("columns")[0].TryGetProperty("ignoredReason", out _).Should().BeFalse();

        var commit = await _admin.PostAsync($"{Base}/modules/{key}/import?commit=true", Upload(csv));
        commit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(commit)).GetProperty("created").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Import_CommitInvalidRow_400WithRowKey()
    {
        var (key, title) = await Module();

        var res = await _admin.PostAsync($"{Base}/modules/{key}/import?commit=true",
            Upload(Encoding.UTF8.GetBytes($"{title},x\r\n,1\r\n")));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty($"rows[2].{title}")[0].GetString().Should().Be("此欄位必填");
    }

    [Fact]
    public async Task Import_OverOneMegabyte_400File()
    {
        var (key, _) = await Module();

        var res = await _admin.PostAsync($"{Base}/modules/{key}/import", Upload(new byte[1024 * 1024 + 10]));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("檔案不能超過 1 MB");
    }

    // ---- 檔案大小政策（A21）與請求上限：新開 TestApp，政策值不影響這個類別的其他測試 ----

    private static Task<HttpResponseMessage> PutSystemPolicy(HttpClient c, string code, object? value) =>
        c.PutAsJsonAsync($"{Base}/system/policies/{code}", new { value });

    private static async Task<(string Key, string Title)> Module(HttpClient c)
    {
        var key = await CreateModule(c);
        await Activate(c, key);
        return (key, await TitleKey(c, key));
    }

    [Fact]
    public async Task Import_LoweredFileLimit_400WithFormattedSize()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var (key, _) = await Module(admin);
        (await PutSystemPolicy(admin, "import.maxFileBytes", 524288)).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await admin.PostAsync($"{Base}/modules/{key}/import", Upload(new byte[600 * 1024]));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("檔案不能超過 0.5 MB");
    }

    [Fact]
    public async Task Import_RaisedFileLimit_AcceptsLargerBody()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var (key, title) = await Module(admin);
        (await PutSystemPolicy(admin, "import.maxFileBytes", 3145728)).StatusCode.Should().Be(HttpStatusCode.OK);
        // 2.5 MB：1000 列、每列帶一個對不到欄位（會被略過）的長字串
        var junk = new string('x', 2600);
        var csv = Encoding.UTF8.GetBytes($"{title},junk\r\n" + string.Concat(Enumerable.Range(0, 1000).Select(i => $"r{i},{junk}\r\n")));
        csv.Length.Should().BeGreaterThan(2_500_000);

        var res = await admin.PostAsync($"{Base}/modules/{key}/import", Upload(csv));

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("valid").GetInt32().Should().Be(1000);
    }

    [Fact]
    public async Task Import_HugeBody_413BeforeReading()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var (key, _) = await Module(admin);
        app.ResetBodyCount();

        // 出廠值 1 MB → 請求上限 2 MB＋64KB；ByteArrayContent 帶 Content-Length
        var res = await admin.PostAsync($"{Base}/modules/{key}/import", Upload(new byte[10 * 1024 * 1024]));

        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        app.BodyBytesRead.Should().BeLessThan(1024 * 1024, "超過請求上限時不該把主體收進來");
    }

    [Fact]
    public async Task Import_NoFile_400File()
    {
        var (key, _) = await Module();

        // 表單裡沒有 file 欄位
        var res = await _admin.PostAsync($"{Base}/modules/{key}/import",
            new MultipartFormDataContent { { new StringContent("x"), "other" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("請選擇檔案");
    }
}
