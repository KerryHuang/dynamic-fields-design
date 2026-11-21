using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPITests.Dynamic.PermissionAxes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// PAX-22（權限軸 §12、mcp1a 計畫判斷 9，Task 5）：MCP 通道一律拒絕權限設定，系統管理員也一樣；畫面通道的同一位 admin 照常成功。
/// 經 HTTP 走完整管線；呼叫通道換成可切換的假實作（正式的 claim 讀取見 <see cref="McpPermissionDiTests"/>，MCP 認證在 Task 6 才有）。
/// 放在 WebAPITests 而不是計畫寫的 ApplicationTests：套件、範本、Demo 端點與 PaxWorld fixture 都在這一層。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class McpChannelGateTests : IAsyncLifetime
{
    private const string Denied = "mcpPermissionAdminDenied";
    private const string Apps = $"{Base}/apps";

    private sealed class SwitchChannel : IRequestChannel
    {
        public RequestChannel Channel { get; set; } = RequestChannel.Ui;
        public long? TokenId => Channel == RequestChannel.Mcp ? 1 : null;
        public IReadOnlyList<string> Scopes => [];
    }

    private sealed class App : TestApp
    {
        public SwitchChannel Channel { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IRequestChannel>();
                s.AddSingleton<IRequestChannel>(Channel);
                var packages = TemplateCatalog.Load(TemplateCatalog.DefaultDirectory).BuiltIns.ToList();
                packages.Add(TemplateJson.Parse(PermissionAxesTemplateTests.TPerm).Package!);
                s.RemoveAll<TemplateCatalog>();
                s.AddSingleton(new TemplateCatalog(packages));
            });
        }
    }

    private readonly App _app = new();
    private PaxWorld _w = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _w = await CreateAsync(_app);
        _admin = _w.As(Admin);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>權限表、群組、使用者逐列快照。</summary>
    private List<string> Snapshot() =>
    [
        .. _w.GuardRows(),
        .. _w.Db(db => db.Set<PermissionGroup>().AsNoTracking().OrderBy(g => g.Id).Select(g => $"G:{g.Id}|{g.Key}").ToList()),
        .. _w.Db(db => db.Set<AppUser>().AsNoTracking().OrderBy(u => u.UserId)
            .Select(u => $"U:{u.UserId}|{u.DisplayName}|{u.IsActive}|{u.IsSystemAdmin}|{u.PasswordHash}").ToList()),
    ];

    /// <summary>MCP 通道送一次：403 同碼、快照不變；再用畫面通道送同一個請求，回 <paramref name="ui"/>（null＝不送對照）。</summary>
    private async Task Check(string name, Func<Task<HttpResponseMessage>> send, HttpStatusCode? ui)
    {
        var before = Snapshot();
        _app.Channel.Channel = RequestChannel.Mcp;
        try { await PaxAssert.Forbidden(await send(), Denied); }
        finally { _app.Channel.Channel = RequestChannel.Ui; }
        Snapshot().Should().Equal(before, name);
        if (ui is { } expected)
        {
            var res = await send();
            res.StatusCode.Should().Be(expected, $"{name}（畫面通道）：{await res.Content.ReadAsStringAsync()}");
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PAX22_McpChannel_DeniesPermissionAdmin_EvenSystemAdmin()
    {
        var permissions = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{A}/permissions"))).GetRawText();
        await Check("模組權限存檔", () => _admin.PutAsync($"{Base}/modules/{A}/permissions", Json(permissions)), HttpStatusCode.OK);

        (await _admin.PostAsync($"{Apps}/packages/import", Json(AppTestPackages.Procure))).StatusCode.Should().Be(HttpStatusCode.Created);
        var opened = _w.Db(db => db.Set<Module>().Count());
        await Check("開啟套件", () => _admin.PostAsJsonAsync($"{Apps}/packages/procure-lite/open", new { }), HttpStatusCode.Created);
        _w.Db(db => db.Set<Module>().Count()).Should().BeGreaterThan(opened);
        await Check("關閉應用程式", () => _admin.PostAsJsonAsync($"{Apps}/procure-lite/close", new { cascade = false }), HttpStatusCode.OK);
        await Check("重新開啟應用程式", () => _admin.PostAsync($"{Apps}/procure-lite/reopen", null), HttpStatusCode.OK);
        (await _admin.PostAsJsonAsync($"{Apps}/procure-lite/close", new { cascade = false })).EnsureSuccessStatusCode();
        await Check("刪除應用程式", () => _admin.DeleteAsync($"{Apps}/procure-lite?purged=0"), HttpStatusCode.OK);

        // Demo 資料只驗拒絕：畫面通道會啟動背景產生，對照組見 PermissionAxesDemoTests
        await Check("產生 Demo 資料", () => _admin.PostAsync($"{Base}/demo-data/generate", null), null);

        var system = new { designer = new { read = true, add = false, edit = false, delete = false }, permissionAdmin = false };
        await Check("群組系統權限", () => _admin.PutAsJsonAsync($"{Base}/admin/groups/{_w.GDesign}/system-privileges", system), HttpStatusCode.OK);
        await Check("使用者系統權限", () => _admin.PutAsJsonAsync($"{Base}/admin/users/{Zhang}/system-privileges", system), HttpStatusCode.OK);
        await Check("群組成員", () => _admin.PutAsJsonAsync($"{Base}/admin/groups/{_w.GPerm}/members", new { userIds = new[] { Li, Wang } }),
            HttpStatusCode.OK);
        await Check("建立使用者", () => _admin.PostAsJsonAsync($"{Base}/admin/users",
            new { userId = "mcp-u1", displayName = "測試一", password = "pass-word-123", groupIds = Array.Empty<long>(), isSystemAdmin = false }),
            HttpStatusCode.Created);
        await Check("修改使用者", () => _admin.PutAsJsonAsync($"{Base}/admin/users/{Wang}",
            new { displayName = "小王二", groupIds = new[] { _w.GDesign }, isSystemAdmin = false }), HttpStatusCode.OK);
        var empty = Group("mcp-empty", "空群組");
        _w.Write(db => db.Add(empty));
        await Check("刪除群組", () => _admin.DeleteAsync($"{Base}/admin/groups/{empty.Id}"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task PAX22_McpChannel_ConditionalGates()
    {
        var tTitle = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{T}"))).GetProperty("titleFieldId").GetInt64();
        object Event(string key) => new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "抄到 T" }, sourceModuleKey = A, active = true,
            definition = new
            {
                trigger = new { on = "create" },
                effects = new[]
                {
                    new
                    {
                        code = "mk", kind = "create", targetModuleKey = T,
                        set = new object[] { new { to = _w.TN, from = new { side = "header", fieldId = _w.ASalary }, allowHiddenSource = true }, new { to = tTitle, @const = "T" } },
                    },
                },
            },
        };
        await Check("事件允許隱藏來源", () => _admin.PostAsJsonAsync($"{Base}/events", Event("ev-mcp")), HttpStatusCode.Created);

        var m = await JsonOf(await _admin.GetAsync($"{Base}/modules/{A}"));
        var blockId = m.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        await Check("新增欄位標不提供給 AI", () => _admin.PostAsJsonAsync($"{Base}/modules/{A}/fields", new
        {
            blockId, type = "decimal", label = new Dictionary<string, string> { ["zh-TW"] = "機密" }, config = new { }, excludeFromAi = true,
        }), HttpStatusCode.Created);

        var field = m.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("id").GetInt64() == _w.AF1);
        object Update() => new
        {
            type = field.GetProperty("type").GetString(), label = field.GetProperty("label"), config = field.GetProperty("config"),
            blockId = field.GetProperty("blockId").GetInt64(), excludeFromAi = true,
        };
        await Check("修改欄位改變旗標", () => _admin.PutAsJsonAsync($"{Base}/fields/{_w.AF1}", Update()), HttpStatusCode.OK);
        _w.Db(db => db.Set<Field>().AsNoTracking().Single(f => f.Id == _w.AF1).ExcludeFromAi).Should().BeTrue("畫面通道的對照組改成功");
    }

    [Fact]
    public async Task PAX22_McpChannel_TemplateSkipsPermissionSuggestions()
    {
        _app.Channel.Channel = RequestChannel.Mcp;
        HttpResponseMessage res;
        try
        {
            res = await _admin.PostAsJsonAsync($"{Base}/templates/t-perm/instantiate",
                new { modules = new[] { new { code = "p", key = "pax-tp-mcp" } }, excludeFields = Array.Empty<string>() });
        }
        finally { _app.Channel.Channel = RequestChannel.Ui; }

        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(PermissionAxesTemplateTests.SkippedWarning);
        var code = _w.Db(db => db.Set<Module>().Where(x => x.Key == "pax-tp-mcp").Select(x => x.ProgramCode).Single());
        _w.Db(db => db.Set<GroupProgramPrivilege>().Count(p => p.ProgramCode == code)).Should().Be(0, "權限建議略過");
    }
}
