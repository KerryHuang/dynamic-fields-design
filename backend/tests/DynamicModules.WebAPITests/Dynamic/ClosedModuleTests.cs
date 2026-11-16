using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 模組狀態 Closed（應用程式關閉後的狀態）：讀取照權限、寫入一律擋（含系統管理員）。
/// 這裡直接用資料庫把模組設成 Closed，不經過應用程式的關閉流程，專測那三十處狀態判斷。
/// </summary>
public class ClosedModuleTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;
    private readonly HttpClient _sales;

    public ClosedModuleTests()
    {
        _admin = _app.AsUser();
        _sales = _app.AsUser("sales01");
    }

    public void Dispose() => _app.Dispose();

    private sealed record World(
        string Target, string TargetTitle, long TargetField, string TargetFieldKey, long TargetRecord, string TargetRowVersion,
        string Source, string SourceTitle, string RefKey, long RefFieldId, long SourceRecord, long ReportId, long ViewId);

    private void SetStatus(string moduleKey, ModuleStatus status)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<Module>().Single(m => m.Key == moduleKey).Status = status;
        db.SaveChanges();
    }

    private async Task<World> MakeWorld()
    {
        var target = await CreateModule(_admin);
        var field = await AddField(_admin, target, "text", new { isFilterable = true });
        var source = await CreateModule(_admin);
        var refField = await AddField(_admin, source, "reference", new { refTarget = new { kind = "module", key = target } });
        await Activate(_admin, target);
        await Activate(_admin, source);
        _app.GrantBusinessGroups(target);
        _app.GrantBusinessGroups(source);
        var targetTitle = await TitleKey(_admin, target);
        var sourceTitle = await TitleKey(_admin, source);

        var t = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{target}/records",
            new { values = new Dictionary<string, object> { [targetTitle] = "客戶甲", [field.Key] = "v" } }));
        var tid = t.GetProperty("id").GetInt64();
        var s = await JsonOf(await _admin.PostAsJsonAsync($"{Base}/modules/{source}/records",
            new { values = new Dictionary<string, object> { [sourceTitle] = "報價一", [refField.Key] = tid } }));

        var report = await _admin.PostAsJsonAsync($"{Base}/reports", new
        {
            name = "筆數",
            definition = new Dictionary<string, object?>
            {
                ["version"] = 1, ["mainModuleKey"] = target, ["child"] = null, ["columns"] = Array.Empty<object>(),
                ["groups"] = Array.Empty<object>(),
                ["measures"] = new object[] { new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null } },
                ["filters"] = Array.Empty<object>(), ["primaryDateField"] = null, ["dateRange"] = null,
                ["sort"] = Array.Empty<object>(), ["viz"] = "table"
            }
        });
        report.StatusCode.Should().Be(HttpStatusCode.Created, await report.Content.ReadAsStringAsync());

        var titleId = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{target}"))).GetProperty("titleFieldId").GetInt64();
        var view = await _admin.PostAsJsonAsync($"{Base}/modules/{target}/views", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "共用" }, shared = true, columns = new[] { titleId }, filter = (object?)null,
            sort = Array.Empty<object>(), isDefault = false
        });
        view.StatusCode.Should().Be(HttpStatusCode.Created, await view.Content.ReadAsStringAsync());

        return new World(target, targetTitle, field.Id, field.Key, tid, t.GetProperty("rowVersion").GetString()!,
            source, sourceTitle, refField.Key, refField.Id, s.GetProperty("id").GetInt64(),
            (await JsonOf(report)).GetProperty("id").GetInt64(), (await JsonOf(view)).GetProperty("id").GetInt64());
    }

    private static MultipartFormDataContent Upload(string csv)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", "import.csv" } };
    }

    // ---- 讀取：照權限 ----

    [Theory]
    [InlineData("admin")]
    [InlineData("sales01")]
    public async Task Closed_ReadsStillWork(string user)
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);
        var c = _app.AsUser(user);

        var schema = await c.GetAsync($"{Base}/modules/{w.Target}/schema");
        schema.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(schema)).GetProperty("readOnly").GetBoolean().Should().BeTrue();

        (await c.GetAsync($"{Base}/modules/{w.Target}/records/paged?page=1&size=20")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"{Base}/records/{w.TargetRecord}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"{Base}/records/{w.TargetRecord}/history")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"{Base}/records/{w.TargetRecord}/related")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"{Base}/records/{w.TargetRecord}/related/{w.RefFieldId}/paged?page=1&size=20"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"{Base}/modules/{w.Target}/views")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"{Base}/modules/{w.Target}/export?format=csv")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Closed_SourceModule_RelatedTabListedButCannotAdd()
    {
        var w = await MakeWorld();
        var before = await JsonOf(await _admin.GetAsync($"{Base}/records/{w.TargetRecord}/related"));
        before[0].GetProperty("canAdd").GetBoolean().Should().BeTrue();

        SetStatus(w.Source, ModuleStatus.Closed);

        var tabs = await JsonOf(await _admin.GetAsync($"{Base}/records/{w.TargetRecord}/related"));
        tabs.GetArrayLength().Should().Be(1);
        tabs[0].GetProperty("canAdd").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Closed_SchemaOfOpenModule_IsNotReadOnly()
    {
        var w = await MakeWorld();
        var schema = await JsonOf(await _admin.GetAsync($"{Base}/modules/{w.Target}/schema"));
        schema.GetProperty("readOnly").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Closed_ReferenceStillShowsTitle_NotRestricted()
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);

        var paged = await JsonOf(await _sales.GetAsync($"{Base}/modules/{w.Source}/records/paged?page=1&size=20"));
        paged.GetProperty("items")[0].GetProperty("references").GetProperty(w.RefKey)
            .GetProperty("state").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Closed_ReportsStillRunAndCatalogListsModule()
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);

        var run = await _admin.PostAsJsonAsync($"{Base}/reports/{w.ReportId}/run", new { range = (object?)null });
        run.StatusCode.Should().Be(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        (await JsonOf(run)).GetProperty("total").GetProperty("n").GetDecimal().Should().Be(1);

        (await _admin.GetAsync($"{Base}/reports/catalog/{w.Target}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var mains = await _admin.GetAsync($"{Base}/reports/catalog");
        mains.StatusCode.Should().Be(HttpStatusCode.OK);
        (await mains.Content.ReadAsStringAsync()).Should().Contain(w.Target);
    }

    [Fact]
    public async Task Closed_MenuListsModuleMarkedClosed_OnlyToAuthorizedUsers()
    {
        var w = await MakeWorld();
        var silent = await CreateModule(_admin);
        await AddField(_admin, silent, "text");
        await Activate(_admin, silent); // 沒授權給任何群組
        SetStatus(w.Target, ModuleStatus.Closed);
        SetStatus(silent, ModuleStatus.Closed);

        var menu = await JsonOf(await _sales.GetAsync($"{Base}/menu"));
        var items = menu.EnumerateArray().ToList();
        items.Single(i => i.GetProperty("key").GetString() == w.Target).GetProperty("closed").GetBoolean().Should().BeTrue();
        items.Single(i => i.GetProperty("key").GetString() == w.Source).GetProperty("closed").GetBoolean().Should().BeFalse();
        items.Any(i => i.GetProperty("key").GetString() == silent).Should().BeFalse("沒有 Read 授權的關閉模組不進選單");
    }

    // ---- 寫入：一律擋，連系統管理員 ----

    [Theory]
    [InlineData("admin")]
    [InlineData("sales01")]
    public async Task Closed_RecordWritesAreRejectedWith409(string user)
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);
        var c = _app.AsUser(user);

        var create = await c.PostAsJsonAsync($"{Base}/modules/{w.Target}/records",
            new { values = new Dictionary<string, object> { [w.TargetTitle] = "新" } });
        create.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var update = await c.PutAsJsonAsync($"{Base}/records/{w.TargetRecord}", new
        {
            rowVersion = w.TargetRowVersion, values = new Dictionary<string, object> { [w.TargetTitle] = "改" }
        });
        update.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await c.DeleteAsync($"{Base}/records/{w.TargetRecord}?rowVersion={w.TargetRowVersion}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await c.PostAsync($"{Base}/modules/{w.Target}/import?commit=true", Upload($"{w.TargetTitle}\r\na\r\n")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await c.PostAsync($"{Base}/modules/{w.Target}/import", Upload($"{w.TargetTitle}\r\na\r\n")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 資料真的沒被動
        var rec = await JsonOf(await _admin.GetAsync($"{Base}/records/{w.TargetRecord}"));
        rec.GetProperty("values").GetProperty(w.TargetTitle).GetString().Should().Be("客戶甲");
    }

    [Fact]
    public async Task Closed_ViewWritesAreRejectedWith409()
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);
        var titleId = long.Parse(w.TargetTitle[2..]);

        var create = await _admin.PostAsJsonAsync($"{Base}/modules/{w.Target}/views", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "新" }, shared = true, columns = new[] { titleId }, filter = (object?)null,
            sort = Array.Empty<object>(), isDefault = false
        });
        create.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await _admin.PutAsJsonAsync($"{Base}/views/{w.ViewId}", new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "改" }, shared = true, columns = new[] { titleId }, filter = (object?)null,
            sort = Array.Empty<object>(), isDefault = false
        })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.DeleteAsync($"{Base}/views/{w.ViewId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Closed_NewReferenceValuesPointingToIt_AreRejected_ButOtherEditsPass()
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);

        // 已存在的引用保留；改別的欄位不受影響（本例只有標題）
        var existing = await JsonOf(await _admin.GetAsync($"{Base}/records/{w.SourceRecord}"));
        var rv = existing.GetProperty("rowVersion").GetString();
        var edit = await _admin.PutAsJsonAsync($"{Base}/records/{w.SourceRecord}", new
        {
            rowVersion = rv, values = new Dictionary<string, object> { [w.SourceTitle] = "報價一改", [w.RefKey] = w.TargetRecord }
        });
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());

        // 新記錄指向它：不行
        var create = await _admin.PostAsJsonAsync($"{Base}/modules/{w.Source}/records", new
        {
            values = new Dictionary<string, object> { [w.SourceTitle] = "報價二", [w.RefKey] = w.TargetRecord }
        });
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 挑選器也看不到
        (await _admin.GetAsync($"{Base}/modules/{w.Target}/records/light")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Closed_CannotBeActivatedOrDeactivatedOnItsOwn()
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);

        (await _admin.PostAsync($"{Base}/modules/{w.Target}/activate", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.PostAsync($"{Base}/modules/{w.Target}/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _admin.DeleteAsync($"{Base}/modules/{w.Target}?purged=0")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Closed_WriteByUserWithoutAccess_Is403Not409()
    {
        var module = await CreateModule(_admin);
        await AddField(_admin, module, "text");
        await Activate(_admin, module); // 沒授權給任何群組
        var title = await TitleKey(_admin, module);
        SetStatus(module, ModuleStatus.Closed);

        var res = await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "x" } });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "不洩漏沒有權限的人這個模組存在且已關閉");
    }

    // ---- 不 fail-open：Draft／Inactive 照舊 404 ----

    [Theory]
    [InlineData(ModuleStatus.Draft)]
    [InlineData(ModuleStatus.Inactive)]
    public async Task NonActiveNonClosed_StillNotFoundForReadsAndWrites(ModuleStatus status)
    {
        var w = await MakeWorld();
        SetStatus(w.Target, status);

        (await _admin.GetAsync($"{Base}/records/{w.TargetRecord}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.GetAsync($"{Base}/modules/{w.Target}/records/paged")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.PostAsJsonAsync($"{Base}/modules/{w.Target}/records",
            new { values = new Dictionary<string, object> { [w.TargetTitle] = "x" } })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var paged = await JsonOf(await _sales.GetAsync($"{Base}/modules/{w.Source}/records/paged?page=1&size=20"));
        paged.GetProperty("items")[0].GetProperty("references").GetProperty(w.RefKey)
            .GetProperty("state").GetString().Should().NotBe("ok");
    }

    // ---- 啟動同步 ----

    [Fact]
    public async Task Seed_KeepsProgramActiveForClosedModules()
    {
        var w = await MakeWorld();
        SetStatus(w.Target, ModuleStatus.Closed);

        using (var scope = _app.Services.CreateScope())
            await SeedData.EnsureSeededAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());

        using var check = _app.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var code = await db.Set<Module>().Where(m => m.Key == w.Target).Select(m => m.ProgramCode).SingleAsync();
        (await db.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == code)).Active.Should().BeTrue();
        (await _sales.GetAsync($"{Base}/records/{w.TargetRecord}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
