using System.Net;
using System.Net.Http.Headers;
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
