using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>內建範本之外，測試可以塞入匯出的套件再套用（往返測試用）。</summary>
public sealed class TemplateExportTestApp : TestApp
{
    public List<TemplatePackage> Extra { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ITemplateSource>();
            s.AddScoped<ITemplateSource>(sp => new ExtraSource(new TemplateSource(sp.GetRequiredService<TemplateCatalog>(), sp.GetRequiredService<IModuleTemplateRepository>()), Extra));
        });
    }

    private sealed class ExtraSource(TemplateSource inner, List<TemplatePackage> extra) : ITemplateSource
    {
        public async Task<TemplatePackage?> FindAsync(string key, CancellationToken ct) =>
            extra.FirstOrDefault(p => p.Key == key) ?? await inner.FindAsync(key, ct);

        public Task<IReadOnlyList<(TemplatePackage Package, string Source)>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
    }
}

/// <summary>每個測試一個新的資料庫：模組代碼固定（order、order-line），共用會互相撞。</summary>
public sealed partial class TemplateExportTests : IDisposable
{
    private readonly TemplateExportTestApp _app = new();
    private readonly HttpClient _admin;

    public TemplateExportTests() => _admin = _app.AsUser("admin");

    public void Dispose() => _app.Dispose();

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    private Task<HttpResponseMessage> Export(string key, HttpClient? c = null) =>
        (c ?? _admin).GetAsync($"{Base}/modules/{key}/export-template");

    private async Task<(TemplatePackage Package, string Json)> ExportPackage(string key)
    {
        var res = await Export(key);
        var json = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, json);
        var (package, error) = TemplateJson.Parse(json);
        error.Should().BeNull();
        TemplateValidator.Validate(package!).Should().BeEmpty(json);
        return (package!, json);
    }

    private async Task Instantiate(string key, params (string Code, string Key)[] modules)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/templates/{key}/instantiate",
            new { modules = modules.Select(m => new { code = m.Code, key = m.Key }), excludeFields = Array.Empty<string>() });
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Created, body);
        JsonDocument.Parse(body).RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0, body);
    }

    /// <summary>
    /// 等價比較用：模組鍵換回原本的鍵，欄位、區塊、選項代號（f_/b_/o_ 加 id）依出現順序換成序號，
    /// 所以比的是結構與位置，不是代號字串。
    /// </summary>
    private static string Normalize(TemplatePackage p, params (string From, string To)[] moduleKeys)
    {
        var json = JsonSerializer.Serialize(p, TemplateJson.Options);
        foreach (var (from, to) in moduleKeys.OrderByDescending(k => k.From.Length))
            json = Regex.Replace(json, $"(?<![a-z0-9-]){Regex.Escape(from)}(?![a-z0-9-])", to);
        var seen = new Dictionary<string, string>();
        return CodeToken().Replace(json, m =>
        {
            if (!seen.TryGetValue(m.Value, out var n))
                seen[m.Value] = n = $"{m.Groups[1].Value}#{seen.Count(x => x.Key[0] == m.Value[0])}";
            return n;
        });
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])([fbo])_\d+(?![0-9])")]
    private static partial Regex CodeToken();

    private static IEnumerable<TemplateField> Fields(TemplateModule m) => m.Blocks.SelectMany(b => b.Fields);

    private static TemplateField Field(TemplateModule m, string label) =>
        Fields(m).Single(f => f.Label.Resolve("zh-TW") == label);

    private async Task<(long Id, string Key)> AddField(string module, long blockId, string type, string label, object config)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/fields", new { blockId, type, label = Zh(label), config });
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var f = JsonDocument.Parse(body).RootElement;
        return (f.GetProperty("id").GetInt64(), f.GetProperty("key").GetString()!);
    }

    private async Task<long> AddOption(long fieldId, string label)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label) });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task<(long BlockId, long TitleId)> NewModule(string key, string name, string titleLabel)
    {
        (await _admin.PostAsJsonAsync($"{Base}/modules", new { key, name = Zh(name) })).StatusCode.Should().Be(HttpStatusCode.Created, key);
        var m = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}"));
        var blockId = m.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var titleId = m.GetProperty("titleFieldId").GetInt64();
        (await _admin.PutAsJsonAsync($"{Base}/fields/{titleId}", new { type = "text", label = Zh(titleLabel), config = new { required = true }, blockId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return (blockId, titleId);
    }

    private async Task Ok(Task<HttpResponseMessage> call)
    {
        var res = await call;
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_OrderFromTemplate_RoundTrip()
    {
        await Instantiate("order");
        var (first, json) = await ExportPackage("order");

        first.Key.Should().Be("order");
        first.Name.Resolve("zh-TW").Should().Be("訂單");
        first.Description.Should().BeNull();
        first.Warnings.Should().BeNullOrEmpty();
        first.Modules.Select(m => (m.Code, m.Key)).Should().Equal(("order", "order"), ("order-line", "order-line"));
        json.Should().NotContain("\"isEmpty\":").And.NotContain("FieldId").And.NotContain("optionIds");

        var order = first.Modules[0];
        var line = first.Modules[1];
        var lineRef = Field(line, "訂單");
        var subtotal = Field(order, "未稅合計").Config.GetProperty("rollup");
        subtotal.GetProperty("sourceField").GetString().Should().Be($"order-line.{lineRef.Code}");
        subtotal.GetProperty("aggregateField").GetString().Should().Be($"order-line.{Field(line, "金額").Code}");
        Field(order, "稅額").Config.GetProperty("formula").GetString()
            .Should().Be($"ROUND({{{Field(order, "未稅合計").Code}}} * {{{Field(order, "稅率").Code}}}, 0)");
        var status = Field(order, "狀態");
        status.Config.GetProperty("defaultValue").GetString().Should().Be($"opt_{status.Options[0].Code}");
        Field(line, "交期").Config.GetProperty("parentDefaultField").GetString().Should().Be($"order.{Field(order, "交期").Code}");
        lineRef.Config.GetProperty("refTarget").GetProperty("key").GetString().Should().Be("order");
        order.Rules.Should().HaveCount(4);
        order.Rules[1].Targets.Should().Equal($"field:{Field(order, "取消原因").Code}");
        order.Views.Should().ContainSingle().Which.Columns.Should().HaveCount(6);
        order.Permissions.Select(x => x.Group).Should().Equal("sales", "sales-manager", "floor");
        line.Permissions.Should().BeEmpty();

        _app.Extra.Add(first with { Key = "rt" });
        await Instantiate("rt", ("order", "order3"), ("order-line", "order3-line"));
        var (second, _) = await ExportPackage("order3");

        Normalize(second, ("order3", "order"), ("order3-line", "order-line")).Should().Be(Normalize(first));
    }

    [Fact]
    public async Task Export_HandBuiltModules_RoundTrip()
    {
        const string Order = "order";
        const string Line = "order-line";
        var (orderBlock, _) = await NewModule(Order, "訂單", "備註");
        var no = await AddField(Order, orderBlock, "autonumber", "訂單編號", new { format = "SO-{yyyyMM}-{0000}" });
        await Ok(_admin.PutAsJsonAsync($"{Base}/modules/{Order}", new { name = Zh("訂單"), icon = "receipt", section = "業務", titleFieldId = no.Id }));
        await AddField(Order, orderBlock, "reference", "客戶", new { refTarget = new { kind = "entity", key = "sales.customer" } });
        var orderDate = await AddField(Order, orderBlock, "date", "訂單日期", new { defaultToday = true });
        var dueDate = await AddField(Order, orderBlock, "date", "交期", new { isSortable = true });
        var status = await AddField(Order, orderBlock, "enum", "狀態", new { isFilterable = true });
        var draft = await AddOption(status.Id, "草稿");
        var closed = await AddOption(status.Id, "已結案");
        await Ok(_admin.PutAsJsonAsync($"{Base}/fields/{status.Id}", new
        {
            type = "enum", label = Zh("狀態"), config = new { isFilterable = true, defaultValue = $"opt_{draft}" }, blockId = orderBlock
        }));
        var rate = await AddField(Order, orderBlock, "percent", "稅率", new { defaultValue = 5 });
        await AddField(Order, orderBlock, "textarea", "說明", new { fullWidth = false });
        await AddField(Order, orderBlock, "text", "地址", new { fullWidth = true });
        var blockRes = await _admin.PostAsJsonAsync($"{Base}/modules/{Order}/blocks", new { label = Zh("金額") });
        blockRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var amountBlock = (await JsonOf(blockRes)).GetProperty("id").GetInt64();
        await Ok(_admin.PutAsJsonAsync($"{Base}/blocks/{amountBlock}", new { label = Zh("金額"), collapsed = true, columns = 3 }));

        var (lineBlock, _) = await NewModule(Line, "訂單明細", "品名");
        var parentRef = await AddField(Line, lineBlock, "reference", "訂單",
            new { refTarget = new { kind = "module", key = Order }, relation = "detail" });
        await AddField(Line, lineBlock, "autonumber", "項次", new { format = "{0}", scope = "parent" });
        var qty = await AddField(Line, lineBlock, "decimal", "數量", new { scale = 0 });
        var price = await AddField(Line, lineBlock, "currency", "單價", new { scale = 0 });
        var amount = await AddField(Line, lineBlock, "formula", "金額",
            new { formula = $"{{{qty.Id}}} * {{{price.Id}}}", resultType = "currency", scale = 0 });
        await AddField(Line, lineBlock, "date", "交期", new { parentDefaultFieldId = dueDate.Id });

        var sum = await AddField(Order, amountBlock, "rollup", "未稅合計",
            new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = amount.Id } });
        await AddField(Order, amountBlock, "rollup", "明細筆數", new { rollup = new { sourceFieldId = parentRef.Id, function = "count" } });
        var tax = await AddField(Order, amountBlock, "formula", "稅額",
            new { formula = $"ROUND({{{sum.Id}}} * {{{rate.Id}}}, 0)", resultType = "currency", scale = 0 });
        await AddField(Order, amountBlock, "formula", "含稅總額", new { formula = $"{{{sum.Id}}} + {{{tax.Id}}}", resultType = "currency", scale = 0 });

        await Ok(_admin.PutAsJsonAsync($"{Base}/modules/{Order}/rules", new
        {
            rules = new object[]
            {
                new
                {
                    kind = "validation", targets = new[] { $"field:{dueDate.Id}" }, message = Zh("交期不得早於訂單日期"), active = true,
                    condition = new { any = new object[]
                    {
                        new { field = dueDate.Id, op = "isEmpty" },
                        new { field = dueDate.Id, op = "gte", value = new { fieldRef = orderDate.Id } }
                    } }
                },
                new { kind = "readOnly", targets = new[] { "*" }, active = true, condition = new { field = status.Id, op = "eq", value = $"opt_{closed}" } },
                new { kind = "visibility", targets = new[] { $"block:{amountBlock}" }, active = false, condition = new { field = status.Id, op = "in", value = new[] { $"opt_{draft}" } } },
                new { kind = "optionFilter", targets = new[] { $"field:{status.Id}" }, optionIds = new[] { draft }, active = true, condition = new { all = Array.Empty<object>() } }
            }
        }));

        await Activate(_admin, Order);
        await Activate(_admin, Line);
        await Ok(_admin.PostAsJsonAsync($"{Base}/modules/{Order}/views", new
        {
            name = Zh("未結案"), shared = true, columns = new[] { no.Id, status.Id, dueDate.Id },
            filter = new { all = new[] { new { field = status.Id, op = "in", value = new[] { $"opt_{draft}" } } } },
            sort = new[] { new { field = dueDate.Id, order = "desc" } }, isDefault = true
        }));
        var groups = (await JsonOf(await _admin.GetAsync($"{Base}/groups"))).EnumerateArray()
            .ToDictionary(g => g.GetProperty("key").GetString()!, g => g.GetProperty("id").GetInt64());
        await Ok(_admin.PutAsJsonAsync($"{Base}/modules/{Order}/permissions", new
        {
            groups = new[]
            {
                new { groupId = groups["sales"], scope = "own",
                      privileges = new { read = true, add = true, edit = true, delete = false, printExport = false, pricing = false, approve = false } }
            }
        }));

        var (first, _) = await ExportPackage(Order);
        first.Warnings.Should().BeNullOrEmpty();
        first.Modules.Select(m => m.Code).Should().Equal(Order, Line);
        var order = first.Modules[0];
        order.Blocks.Should().HaveCount(2);
        order.Blocks[1].Collapsed.Should().BeTrue();
        (order.Blocks[0].Columns, order.Blocks[1].Columns).Should().Be((2, 3));
        Field(order, "說明").Config.GetProperty("fullWidth").GetBoolean().Should().BeFalse();
        Field(order, "地址").Config.GetProperty("fullWidth").GetBoolean().Should().BeTrue();
        order.Rules.Should().HaveCount(4);
        order.Rules[2].Active.Should().BeFalse();
        order.Rules[2].Targets.Should().Equal($"block:{order.Blocks[1].Code}");
        order.Rules[3].Options.Should().Equal($"o_{draft}");
        order.Views.Should().ContainSingle().Which.Filter!.Value.GetRawText().Should().Contain($"opt_o_{draft}");
        order.Permissions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Group = "sales", Scope = Domain.Entities.Dynamic.DataScope.Own });

        _app.Extra.Add(first with { Key = "hb" });
        await Instantiate("hb", (Order, "hb2"), (Line, "hb2-line"));
        var (second, _) = await ExportPackage("hb2");

        Normalize(second, ("hb2", Order), ("hb2-line", Line)).Should().Be(Normalize(first));
    }

    [Fact]
    public async Task Export_ExtensionModule_RoundTrip()
    {
        // 擴充模組沒有標題欄位（標題取自既有資料），匯出的 titleField 是 null
        var create = await _admin.PostAsJsonAsync($"{Base}/modules",
            new { key = "mold-ext", name = Zh("模具擴充"), kind = "extension", hostEntityKey = "design.mold" });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var blockId = (await JsonOf(await _admin.GetAsync($"{Base}/modules/mold-ext"))).GetProperty("blocks")[0].GetProperty("id").GetInt64();
        await AddField("mold-ext", blockId, "integer", "保養週期", new { });
        await AddField("mold-ext", blockId, "date", "上次保養日", new { });

        var (first, json) = await ExportPackage("mold-ext");
        var ext = first.Modules.Should().ContainSingle().Subject;
        ext.Kind.Should().Be(Domain.Entities.Dynamic.ModuleKind.Extension);
        ext.HostEntityKey.Should().Be("design.mold");
        ext.TitleField.Should().BeNull();
        json.Should().NotContain("\"titleField\"");

        // 同一個既有實體只能有一個擴充模組：換一個新的資料庫套用
        using var fresh = new TemplateExportTestApp();
        fresh.Extra.Add(first with { Key = "ext-rt" });
        var admin2 = fresh.AsUser("admin");
        var res = await admin2.PostAsJsonAsync($"{Base}/templates/ext-rt/instantiate",
            new { modules = Array.Empty<object>(), excludeFields = Array.Empty<string>() });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());

        var exported = await admin2.GetAsync($"{Base}/modules/mold-ext/export-template");
        var exportedJson = await exported.Content.ReadAsStringAsync();
        exported.StatusCode.Should().Be(HttpStatusCode.OK, exportedJson);
        var second = TemplateJson.Parse(exportedJson).Package!;
        TemplateValidator.Validate(second).Should().BeEmpty();
        Normalize(second).Should().Be(Normalize(first));
    }

    [Fact]
    public async Task Export_DetailModuleAlone_RefersExternalHeader()
    {
        await Instantiate("order");
        var (p, _) = await ExportPackage("order-line");

        p.Key.Should().Be("order-line");
        var line = p.Modules.Should().ContainSingle().Subject;
        var reference = Field(line, "訂單").Config;
        reference.GetProperty("refTarget").GetProperty("key").GetString().Should().Be("order");
        reference.GetProperty("relation").GetString().Should().Be("detail");
        line.Permissions.Should().BeEmpty();
        // 交期帶表頭的值：表頭不在套件裡，拿掉這個設定、欄位照樣匯出
        Field(line, "交期").Config.TryGetProperty("parentDefaultField", out _).Should().BeFalse();
        p.Warnings.Should().Equal("「交期」帶入的表頭欄位在範本外，未匯出這個設定");
    }

    [Fact]
    public async Task Export_RollupOverExternalSource_Omitted_WithWarning()
    {
        var (reqBlock, _) = await NewModule("repair-req", "維修申請", "申請人");
        var (logBlock, _) = await NewModule("repair-log", "維修紀錄", "處理內容");
        var reqRef = await AddField("repair-log", logBlock, "reference", "申請單", new { refTarget = new { kind = "module", key = "repair-req" } });
        var hours = await AddField("repair-log", logBlock, "decimal", "工時", new { scale = 1 });
        await Activate(_admin, "repair-log");
        await AddField("repair-req", reqBlock, "rollup", "總工時",
            new { rollup = new { sourceFieldId = reqRef.Id, function = "sum", aggregateFieldId = hours.Id } });

        var (p, _) = await ExportPackage("repair-req");

        var req = p.Modules.Should().ContainSingle().Subject;
        Fields(req).Select(f => f.Label.Resolve("zh-TW")).Should().Equal("申請人");
        p.Warnings.Should().Equal("「總工時」彙總了範本外的模組，未匯出");
    }

    [Fact]
    public async Task Export_SkipsInactiveFieldsAndPersonalViews()
    {
        var (block, titleId) = await NewModule("skip", "略過測試", "標題");
        var keep = await AddField("skip", block, "text", "保留", new { isSortable = true });
        var gone = await AddField("skip", block, "text", "停用", new { isSortable = true });
        var kind = await AddField("skip", block, "enum", "種類", new { });
        await AddOption(kind.Id, "甲");
        var oldOpt = await AddOption(kind.Id, "乙");
        await Ok(_admin.PutAsJsonAsync($"{Base}/options/{oldOpt}", new { label = Zh("乙"), active = false }));
        await Activate(_admin, "skip");
        await Ok(_admin.PostAsJsonAsync($"{Base}/modules/skip/views", new
        {
            name = Zh("共用"), shared = true, columns = new[] { titleId, keep.Id, gone.Id }, sort = new[] { new { field = gone.Id, order = "asc" } }
        }));
        await Ok(_admin.PostAsJsonAsync($"{Base}/modules/skip/views", new { name = Zh("我的"), shared = false, columns = new[] { titleId }, sort = Array.Empty<object>() }));
        await Ok(_admin.PutAsJsonAsync($"{Base}/fields/{gone.Id}/status", new { status = "inactive" }));

        var (p, _) = await ExportPackage("skip");

        var m = p.Modules.Single();
        Fields(m).Select(f => f.Label.Resolve("zh-TW")).Should().Equal("標題", "保留", "種類");
        Field(m, "種類").Options.Select(o => o.Label.Resolve("zh-TW")).Should().Equal("甲");
        var view = m.Views.Should().ContainSingle().Subject;
        view.Name.Resolve("zh-TW").Should().Be("共用");
        view.Columns.Should().Equal(Field(m, "標題").Code, Field(m, "保留").Code);
        view.Sort.Should().BeEmpty();
        p.Warnings.Should().Equal("檢視「共用」引用了停用或未匯出的欄位，已略過");
    }

    [Fact]
    public async Task Export_ContentDisposition_FileName()
    {
        await Instantiate("order");
        var res = await Export("order");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        res.Content.Headers.ContentDisposition!.FileName.Should().Be("order.template.json");
        (await res.Content.ReadAsStringAsync()).Should().Contain("\n  \"formatVersion\": 1");
    }

    [Fact]
    public async Task Export_UnknownModule_404()
    {
        var res = await Export("nope-nope");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("找不到模組");
    }

    [Fact]
    public async Task Export_NonDesigner_403()
    {
        await Instantiate("order");
        (await Export("order", _app.AsUser("sales01"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
