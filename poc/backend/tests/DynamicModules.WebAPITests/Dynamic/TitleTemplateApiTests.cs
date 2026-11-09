using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
            .Should().Be("標題樣板不能超過 200 字（每個欄位算 1 字）");
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

    // ---- 已固定的記錄（唯讀狀態、已記帳）：重組標題整筆回 409（必修 2） ----

    private async Task<long> Option(long fieldId, string label) =>
        (await JsonOf(await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label), color = (string?)null })))
        .GetProperty("id").GetInt64();

    private async Task<JsonElement> ModuleOf(string key) => await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));

    private static async Task ShouldBeFixedConflict(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("唯讀或已記帳的記錄");
    }

    [Fact]
    public async Task Retitling_IsRefusedWhole_WhenAnyRecordIsInAReadOnlyState()
    {
        var m = await MoldModule();
        var kind = await AddField(_admin, m.Key, "enum");
        var upper = await Option(kind.Id, "上模");
        var status = await AddField(_admin, m.Key, "enum");
        var draft = await Option(status.Id, "草稿");
        var done = await Option(status.Id, "完成");
        (await _admin.PutAsJsonAsync($"{Base}/modules/{m.Key}/workflow", new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done, readOnly = true } },
            transitions = new[] { new { from = draft, to = done, groupIds = Array.Empty<long>() } }
        })).EnsureSuccessStatusCode();
        var template = $"{{{m.NoId}}} {{{m.NameId}}}（{{{kind.Id}}}）";
        (await PutTitle(m.Key, m.NameId, template)).EnsureSuccessStatusCode();   // 還沒有固定的記錄：照常可改

        var id = await AddRecord(m.Key, new() { [m.NameKey] = "上蓋模", [m.NoKey] = "MD-01", [kind.Key] = $"opt_{upper}" });
        await AddRecord(m.Key, new() { [m.NameKey] = "下蓋模", [m.NoKey] = "MD-02" });
        var version = (await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"))).GetProperty("rowVersion").GetString();
        (await _admin.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [status.Key] = $"opt_{done}" }, rowVersion = version })).EnsureSuccessStatusCode();

        await ShouldBeFixedConflict(await PutTitle(m.Key, m.NameId, $"{{{m.NameId}}}"));            // 改樣板
        await ShouldBeFixedConflict(await PutTitle(m.Key, m.NameId, null));                         // 清除樣板
        await ShouldBeFixedConflict(await PutTitle(m.Key, m.NoId, template));                       // 換標題欄位
        await ShouldBeFixedConflict(await _admin.PutAsJsonAsync($"{Base}/options/{upper}",          // 改樣板用到的選項名稱
            new { label = Zh("公模"), color = (string?)null, active = true }));

        var module = await ModuleOf(m.Key);
        module.GetProperty("titleTemplate").GetString().Should().Be(template);
        module.GetProperty("titleFieldId").GetInt64().Should().Be(m.NameId);
        module.GetProperty("blocks")[0].GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("id").GetInt64() == kind.Id)
            .GetProperty("options").EnumerateArray().Single().GetProperty("label").GetProperty("zh-TW").GetString().Should().Be("上模");
        (await Titles(m.Key)).Should().BeEquivalentTo("MD-01 上蓋模（上模）", "MD-02 下蓋模（）");

        // 不重組標題的修改照常可做：模組名稱、不在樣板裡的選項
        (await _admin.PutAsJsonAsync($"{Base}/modules/{m.Key}", new { name = Zh("模具（改名）"), titleFieldId = m.NameId, titleTemplate = template }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PutAsJsonAsync($"{Base}/options/{draft}", new { label = Zh("草擬"), color = (string?)null, active = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Retitling_IsRefusedWhole_WhenAnyRecordHasEventFacts()
    {
        var m = await MoldModule();
        var move = await CreateModule(_admin);
        await Activate(_admin, move);
        var moveTitle = await TitleKey(_admin, move);
        var ev = await _admin.PostAsJsonAsync($"{Base}/events", new
        {
            key = "ev-" + Guid.NewGuid().ToString("N")[..8], name = Zh("建檔"), sourceModuleKey = m.Key, active = true,
            definition = new
            {
                trigger = new { on = "create" },
                effects = new[] { new { code = "log", kind = "create", targetModuleKey = move, set = new object[] { new { to = long.Parse(moveTitle[2..]), @const = "建檔" } } } }
            }
        });
        ev.StatusCode.Should().Be(HttpStatusCode.Created, await ev.Content.ReadAsStringAsync());
        await AddRecord(m.Key, new() { [m.NameKey] = "上蓋模", [m.NoKey] = "MD-01" });

        await ShouldBeFixedConflict(await PutTitle(m.Key, m.NameId, $"{{{m.NoId}}} {{{m.NameId}}}"));   // 設定樣板
        await ShouldBeFixedConflict(await PutTitle(m.Key, m.NoId, null));                              // 換標題欄位
        (await ModuleOf(m.Key)).TryGetProperty("titleTemplate", out _).Should().BeFalse();
        (await Titles(m.Key)).Should().Equal("上蓋模");
    }

    /// <summary>明細列自己沒有狀態與事實，鎖在表頭上：表頭在唯讀狀態時，明細模組也不能重組標題。</summary>
    [Fact]
    public async Task Retitling_ADetailModule_IsRefused_WhenAHeaderIsInAReadOnlyState()
    {
        var head = await CreateModule(_admin);
        var headTitle = await TitleKey(_admin, head);
        var status = await AddField(_admin, head, "enum");
        var draft = await Option(status.Id, "草稿");
        var done = await Option(status.Id, "完成");
        var line = await CreateModule(_admin);
        var lineName = await TitleKey(_admin, line);
        await AddField(_admin, line, "reference", new { refTarget = new { kind = "module", key = head }, relation = "detail" });
        var lineNo = await AddField(_admin, line, "text");
        await Activate(_admin, head);
        await Activate(_admin, line);
        (await _admin.PutAsJsonAsync($"{Base}/modules/{head}/workflow", new
        {
            statusFieldId = status.Id, initialOptionId = draft,
            states = new object[] { new { optionId = draft }, new { optionId = done, readOnly = true } },
            transitions = new[] { new { from = draft, to = done, groupIds = Array.Empty<long>() } }
        })).EnsureSuccessStatusCode();
        var create = await _admin.PostAsJsonAsync($"{Base}/modules/{head}/records", new
        {
            values = new Dictionary<string, object> { [headTitle] = "單一" },
            details = new Dictionary<string, object[]>
            {
                [line] = [new { values = new Dictionary<string, object> { [lineName] = "螺絲", [lineNo.Key] = "L-1" }, _delete = false }]
            }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var header = await JsonOf(create);
        (await _admin.PutAsJsonAsync($"{Base}/records/{header.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [status.Key] = $"opt_{done}" }, rowVersion = header.GetProperty("rowVersion").GetString()
        })).EnsureSuccessStatusCode();

        var lineNameId = long.Parse(lineName[2..]);
        await ShouldBeFixedConflict(await _admin.PutAsJsonAsync($"{Base}/modules/{line}",
            new { name = Zh("明細"), titleFieldId = lineNameId, titleTemplate = $"{{{lineNo.Id}}} {{{lineNameId}}}" }));
        (await ModuleOf(line)).TryGetProperty("titleTemplate", out _).Should().BeFalse();
    }

    // ---- 列表的標題欄（必修 5）：有樣板時依持久化的 Record.Title 排序 ----

    private async Task MakeSortable(string key, long fieldId)
    {
        var field = (await ModuleOf(key)).GetProperty("blocks")[0].GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("id").GetInt64() == fieldId);
        var config = System.Text.Json.Nodes.JsonNode.Parse(field.GetProperty("config").GetRawText())!.AsObject();
        config["isSortable"] = true;
        (await _admin.PutAsJsonAsync($"{Base}/fields/{fieldId}", new
        {
            type = field.GetProperty("type").GetString(), label = Zh("名稱"), config, blockId = field.GetProperty("blockId").GetInt64()
        })).EnsureSuccessStatusCode();
    }

    private async Task<List<string>> PagedTitles(HttpClient c, string key, string query)
    {
        var res = await c.GetAsync($"{Base}/modules/{key}/records/paged?currentPage=1&pagesSize=10{query}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();
    }

    /// <summary>審查反例：樣板 {no} {name}，兩筆標題是「01 Z」「02 A」；依標題欄升冪要排成 01 Z、02 A，不是依名稱。</summary>
    [Fact]
    public async Task ListSort_OnTheTitleField_OrdersByTheComposedTitle_WhenThereIsATemplate()
    {
        var m = await MoldModule();
        await MakeSortable(m.Key, m.NameId);
        await AddRecord(m.Key, new() { [m.NameKey] = "Z", [m.NoKey] = "01" });
        await AddRecord(m.Key, new() { [m.NameKey] = "A", [m.NoKey] = "02" });

        (await PagedTitles(_admin, m.Key, $"&sortBy={m.NameKey}&sortOrder=asc")).Should().Equal("A", "Z");   // 沒有樣板：依名稱，行為不變

        (await PutTitle(m.Key, m.NameId, $"{{{m.NoId}}} {{{m.NameId}}}")).EnsureSuccessStatusCode();
        (await PagedTitles(_admin, m.Key, $"&sortBy={m.NameKey}&sortOrder=asc")).Should().Equal("01 Z", "02 A");
        (await PagedTitles(_admin, m.Key, $"&sortBy={m.NameKey}&sortOrder=desc")).Should().Equal("02 A", "01 Z");
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{m.Key}/schema"))).GetProperty("hasTitleTemplate").GetBoolean().Should().BeTrue();
    }

    /// <summary>樣板用到的欄位對使用者隱藏（例如舊資料留下的例外）時，標題看不到，也不能拿標題排序。</summary>
    [Fact]
    public async Task ListSort_ByTitle_IsRefused_WhenATemplateFieldIsHiddenFromTheCaller()
    {
        var m = await MoldModule();
        await MakeSortable(m.Key, m.NameId);
        (await PutTitle(m.Key, m.NameId, $"{{{m.NoId}}} {{{m.NameId}}}")).EnsureSuccessStatusCode();
        app.GrantBusinessGroups(m.Key);
        await AddRecord(m.Key, new() { [m.NameKey] = "Z", [m.NoKey] = "01" });
        HideFromSales(m.NoId);

        var sales = app.AsUser("sales01");
        var res = await sales.GetAsync($"{Base}/modules/{m.Key}/records/paged?currentPage=1&pagesSize=10&sortBy={m.NameKey}&sortOrder=asc");
        (await ErrorOf(res, "sortBy")).Should().Contain(m.NameKey);
        (await PagedTitles(sales, m.Key, "")).Should().Equal("");
    }

    /// <summary>繞過設計器的防護直接寫入隱藏例外（模擬舊資料）。</summary>
    private void HideFromSales(long fieldId)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicModules.Domain.Data.DynamicDbContext>();
        var sales = db.Set<DynamicModules.Domain.Entities.Dynamic.PermissionGroup>().Single(g => g.Key == "sales");
        db.Set<DynamicModules.Domain.Entities.Dynamic.FieldPermission>().Add(
            new DynamicModules.Domain.Entities.Dynamic.FieldPermission { FieldId = fieldId, GroupId = sales.Id, Access = DynamicModules.Domain.Entities.Dynamic.FieldAccess.Hidden });
        db.SaveChanges();
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

    /// <summary>
    /// 審查反例（必修 4）：套件樣板開啟時換成欄位 Id 會變長（199 個 x 加 {no} 是 200 字以內的樣板，換成 Id 後超過 200 個字元）；反方向，設計器的 {Id} 匯出成代號也會變長。
    /// 上限量的是樣板本身（每個欄位算 1 字），兩個方向都要能來回。
    /// </summary>
    [Fact]
    public async Task Package_NearTheLimit_OpensAndExportsBack_AndADesignerTemplateNearTheLimitExportsAsAValidPackage()
    {
        var near = new string('x', 199) + "{no}";
        var json = Package.Replace("title-lite", "title-near").Replace("\"title-tool\"", "\"title-near-tool\"").Replace("{no} {name}", near);
        (await _admin.PostAsync($"{Base}/apps/packages/import", new StringContent(json, Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var open = await _admin.PostAsJsonAsync($"{Base}/apps/packages/title-near/open", new { });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());

        var module = await ModuleOf("title-near-tool");
        var ids = module.GetProperty("blocks")[0].GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("id").GetInt64());
        var stored = module.GetProperty("titleTemplate").GetString()!;
        stored.Should().Be(new string('x', 199) + $"{{{ids["編號"]}}}");
        stored.Length.Should().BeGreaterThan(200);
        (await JsonOf(await _admin.GetAsync($"{Base}/apps/title-near/export"))).GetProperty("modules")[0].GetProperty("titleTemplate").GetString()
            .Should().Be(near);

        // 設計器：199 字加一個欄位 Id（原樣超過 200 字）可以存；匯出成代號後仍是合法的套件
        var designer = new string('y', 199) + $"{{{ids["名稱"]}}}";
        (await PutTitle("title-near-tool", ids["名稱"], designer)).StatusCode.Should().Be(HttpStatusCode.OK);
        var exported = await _admin.GetStringAsync($"{Base}/apps/title-near/export");
        var parsed = DynamicModules.Application.Dynamic.AppService.AppJson.Parse(exported);
        parsed.Errors.Should().BeEmpty();
        parsed.V2!.Modules.Single().TitleTemplate.Should().Be(new string('y', 199) + "{name}");
        DynamicModules.Application.Dynamic.AppService.AppPackageValidator.Validate(parsed.V2).Errors.Should().BeEmpty();
    }
}
