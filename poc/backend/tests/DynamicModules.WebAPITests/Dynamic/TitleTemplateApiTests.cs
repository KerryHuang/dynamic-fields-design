using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 模組的標題樣板（例如 {編號} {名稱}）：設計器設定、既有記錄重組標題、挑選器用任一段搜尋、匯入比對、
/// 以及樣板用到的欄位不能停用或設為隱藏。資料庫的樣板存欄位 Id。
/// </summary>
public class TitleTemplateApiTests(TestApp app) : IClassFixture<TestApp>
{
    private readonly HttpClient _admin = app.AsUser();

    private static Dictionary<string, string> Zh(string s) => new() { ["zh-TW"] = s };

    private sealed record Mold(string Key, long NameId, string NameKey, long NoId, string NoKey);

    /// <summary>啟用中的模組：預設的名稱標題欄位加一個文字欄位「編號」。</summary>
    private async Task<Mold> MoldModule()
    {
        var key = await CreateModule(_admin);
        var nameKey = await TitleKey(_admin, key);
        var no = await AddField(_admin, key, "text");
        await Activate(_admin, key);
        return new Mold(key, long.Parse(nameKey[2..]), nameKey, no.Id, no.Key);
    }

    private Task<HttpResponseMessage> PutTitle(string key, long? titleFieldId, string? titleTemplate) =>
        _admin.PutAsJsonAsync($"{Base}/modules/{key}", new { name = Zh("模具"), titleFieldId, titleTemplate });

    private async Task<long> AddRecord(string key, Dictionary<string, object> values)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task<List<string>> Titles(string key, string? keyword = null)
    {
        var res = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/records/light?keyword={Uri.EscapeDataString(keyword ?? "")}"));
        return res.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage res, string key)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("errors").GetProperty(key)[0].GetString()!;
    }

    [Fact]
    public async Task SetTemplate_RetitlesExistingRecords_NewRecordsFollow_AndPickerFindsEitherPart()
    {
        var m = await MoldModule();
        await AddRecord(m.Key, new() { [m.NameKey] = "上蓋模", [m.NoKey] = "MD-01" });

        var put = await PutTitle(m.Key, m.NameId, $"{{{m.NoId}}} {{{m.NameId}}}");
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await JsonOf(put)).GetProperty("titleTemplate").GetString().Should().Be($"{{{m.NoId}}} {{{m.NameId}}}");
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{m.Key}"))).GetProperty("titleTemplate").GetString()
            .Should().Be($"{{{m.NoId}}} {{{m.NameId}}}");

        await AddRecord(m.Key, new() { [m.NameKey] = "下蓋模" });   // 編號空白：去掉頭尾空白

        (await Titles(m.Key)).Should().BeEquivalentTo("MD-01 上蓋模", "下蓋模");
        (await Titles(m.Key, "MD-01")).Should().Equal("MD-01 上蓋模");
        (await Titles(m.Key, "上蓋")).Should().Equal("MD-01 上蓋模");
    }

    [Fact]
    public async Task ClearTemplate_TitleIsTheTitleFieldAgain()
    {
        var m = await MoldModule();
        (await PutTitle(m.Key, m.NameId, $"{{{m.NoId}}}-{{{m.NameId}}}")).EnsureSuccessStatusCode();
        await AddRecord(m.Key, new() { [m.NameKey] = "上蓋模", [m.NoKey] = "MD-01" });
        (await Titles(m.Key)).Should().Equal("MD-01-上蓋模");

        var put = await PutTitle(m.Key, m.NameId, null);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(put)).TryGetProperty("titleTemplate", out _).Should().BeFalse();   // null 不輸出
        (await Titles(m.Key)).Should().Equal("上蓋模");
    }

    [Fact]
    public async Task InvalidTemplates_AreRejectedWithReasons()
    {
        var m = await MoldModule();
        var amount = await AddField(_admin, m.Key, "integer");
        var memo = await AddField(_admin, m.Key, "text");
        (await _admin.PutAsJsonAsync($"{Base}/fields/{memo.Id}/status", new { status = "inactive" })).EnsureSuccessStatusCode();

        (await ErrorOf(await PutTitle(m.Key, m.NameId, "{999999} x"), "TitleTemplate")).Should().Be("標題樣板引用了不存在的欄位 999999");
        (await ErrorOf(await PutTitle(m.Key, m.NameId, "{abc}"), "TitleTemplate")).Should().Be("標題樣板引用了不存在的欄位 abc");
        (await ErrorOf(await PutTitle(m.Key, m.NameId, $"{{{amount.Id}}}"), "TitleTemplate"))
            .Should().Be("標題樣板不能引用 欄位（只能用文字、電子郵件、電話、網址、自動編號、日期或單選下拉欄位）");
        (await ErrorOf(await PutTitle(m.Key, m.NameId, $"{{{memo.Id}}}"), "TitleTemplate"))
            .Should().Be("標題樣板不能引用已停用的欄位 欄位");
        (await ErrorOf(await PutTitle(m.Key, m.NameId, $"{{{m.NoId}"), "TitleTemplate")).Should().Be("標題樣板的大括號沒有成對");
        (await ErrorOf(await PutTitle(m.Key, m.NameId, "只有文字"), "TitleTemplate")).Should().Be("標題樣板至少要引用一個欄位");
        (await ErrorOf(await PutTitle(m.Key, m.NameId, "   "), "TitleTemplate")).Should().Be("標題樣板是空的");
        (await ErrorOf(await PutTitle(m.Key, m.NameId, "{" + m.NoId + "}" + new string('x', 200)), "TitleTemplate"))
            .Should().Be("標題樣板不能超過 200 字");
    }

    [Fact]
    public async Task EnumAndDate_RenderAsText_AndRenamingTheOptionRetitles()
    {
        var m = await MoldModule();
        var kind = await AddField(_admin, m.Key, "enum");
        var optionRes = await _admin.PostAsJsonAsync($"{Base}/fields/{kind.Id}/options", new { label = Zh("上模"), color = (string?)null });
        var optionId = (await JsonOf(optionRes)).GetProperty("id").GetInt64();
        var made = await AddField(_admin, m.Key, "date");
        (await PutTitle(m.Key, m.NameId, $"{{{m.NameId}}}（{{{kind.Id}}}，{{{made.Id}}}）")).EnsureSuccessStatusCode();
        await AddRecord(m.Key, new() { [m.NameKey] = "A1", [kind.Key] = $"opt_{optionId}", [made.Key] = "2026-11-09" });
        (await Titles(m.Key)).Should().Equal("A1（上模，2026-11-09）");

        (await _admin.PutAsJsonAsync($"{Base}/options/{optionId}", new { label = Zh("公模"), color = (string?)null, active = true }))
            .EnsureSuccessStatusCode();
        (await Titles(m.Key)).Should().Equal("A1（公模，2026-11-09）");
    }

    [Fact]
    public async Task TemplateFields_CannotBeDeactivatedOrHidden()
    {
        var m = await MoldModule();
        (await PutTitle(m.Key, m.NameId, $"{{{m.NoId}}} {{{m.NameId}}}")).EnsureSuccessStatusCode();

        (await ErrorOf(await _admin.PutAsJsonAsync($"{Base}/fields/{m.NoId}/status", new { status = "inactive" }), "Status"))
            .Should().Be("標題樣板用到的欄位不能停用");

        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        var salesId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        var hide = await _admin.PutAsJsonAsync($"{Base}/modules/{m.Key}/permissions", new
        {
            fields = new[] { new { fieldId = m.NoId, groupId = salesId, access = "hidden" } }
        });
        (await ErrorOf(hide, "fields[0].access")).Should().Be("標題樣板用到的欄位不能設為隱藏");
    }

    [Fact]
    public async Task Import_ReferenceCellMatchesTheComposedTitle()
    {
        var m = await MoldModule();
        (await PutTitle(m.Key, m.NameId, $"{{{m.NoId}}} {{{m.NameId}}}")).EnsureSuccessStatusCode();
        var moldId = await AddRecord(m.Key, new() { [m.NameKey] = "上蓋模", [m.NoKey] = "MD-01" });

        var job = await CreateModule(_admin);
        var jobTitle = await TitleKey(_admin, job);
        var mold = await AddField(_admin, job, "reference", new { refTarget = new { kind = "module", key = m.Key }, onDelete = "restrict" });
        await Activate(_admin, job);

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"{jobTitle},{mold.Key}\r\n工單一,MD-01 上蓋模\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var commit = await _admin.PostAsync($"{Base}/modules/{job}/import?commit=true",
            new MultipartFormDataContent { { file, "file", "import.csv" } });
        commit.StatusCode.Should().Be(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());

        var list = await JsonOf(await _admin.GetAsync($"{Base}/modules/{job}/records/paged?currentPage=1&pagesSize=10"));
        var row = list.GetProperty("items")[0];
        row.GetProperty("values").GetProperty(mold.Key).GetRawText().Trim('"').Should().Be(moldId.ToString());
        row.GetProperty("references").GetProperty(mold.Key).GetProperty("title").GetString().Should().Be("MD-01 上蓋模");
    }

    private const string Package = """
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "title-lite", "version": "1.0.0", "name": { "zh-TW": "標題樣板（測試）" }, "requires": { "apps": [], "entities": [] } },
          "groups": [],
          "modules": [ {
            "code": "tool", "key": "title-tool", "name": { "zh-TW": "工具" }, "kind": "standalone",
            "titleField": "name", "titleTemplate": "{no} {name}",
            "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "fields": [
              { "code": "no", "type": "text", "label": { "zh-TW": "編號" }, "config": { "required": true } },
              { "code": "name", "type": "text", "label": { "zh-TW": "名稱" }, "config": { "required": true } }
            ] } ],
            "permissions": []
          } ]
        }
        """;

    [Fact]
    public async Task Package_OpensWithTheTemplate_AndExportsItAsCodes()
    {
        (await _admin.PostAsync($"{Base}/apps/packages/import", new StringContent(Package, Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var open = await _admin.PostAsJsonAsync($"{Base}/apps/packages/title-lite/open", new { });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());

        var module = await JsonOf(await _admin.GetAsync($"{Base}/modules/title-tool"));
        var ids = module.GetProperty("blocks")[0].GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("id").GetInt64());
        module.GetProperty("titleTemplate").GetString().Should().Be($"{{{ids["編號"]}}} {{{ids["名稱"]}}}");

        await AddRecord("title-tool", new() { [$"f_{ids["編號"]}"] = "T-7", [$"f_{ids["名稱"]}"] = "扳手" });
        (await Titles("title-tool", "T-7")).Should().Equal("T-7 扳手");

        var exported = await JsonOf(await _admin.GetAsync($"{Base}/apps/title-lite/export"));
        exported.GetProperty("modules")[0].GetProperty("titleTemplate").GetString().Should().Be("{no} {name}");
    }
}
