using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>連動事件「允許複製被隱藏的值」要權限設定（M5，PAX-13），與一般隱藏欄位也納入公式閉包（M5 擴大，PAX-13b）。權限軸設計 §7.3、§12.3。</summary>
public sealed class PermissionAxesEventTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private PaxWorld _w = null!;
    private long _tTitle;

    public async Task InitializeAsync()
    {
        _w = await CreateAsync(_app);
        _tTitle = (await JsonOf(await _w.As(Admin).GetAsync($"{Base}/modules/{T}"))).GetProperty("titleFieldId").GetInt64();
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static object Copy(long to, long from, bool allow) => new { to, from = new { side = "header", fieldId = from }, allowHiddenSource = allow };

    private static object Formula(long to, string expr, bool allow) => new { to, formula = new { side = "header", expr }, allowHiddenSource = allow };

    /// <summary>來源 A、目標 T 的事件；第一個對應就是受測的那一個（錯誤鍵是 set[0]），標題欄位放最後。</summary>
    private object Event(string key, object mapping, string name = "抄到 T") => new
    {
        key, name = new Dictionary<string, string> { ["zh-TW"] = name }, sourceModuleKey = A, active = true,
        definition = new
        {
            trigger = new { on = "create" },
            effects = new[] { new { code = "mk", kind = "create", targetModuleKey = T, set = new[] { mapping, new { to = _tTitle, @const = "T" } } } },
        },
    };

    private Task<HttpResponseMessage> Create(string user, object body) => _w.As(user).PostAsJsonAsync($"{Base}/events", body);

    private bool Exists(string key) => _w.Db(db => db.Set<ModuleEvent>().Any(e => e.Key == key));

    [Fact]
    public async Task PAX13_AllowHiddenSource_NeedsPermissionAdmin_OnCreateAndUpdate()
    {
        await PaxAssert.Forbidden(await Create(Wang, Event("ev-wang-flag", Copy(_w.TN, _w.ASalary, allow: true))), "eventHiddenSourceNeedsPermissionAdmin");
        Exists("ev-wang-flag").Should().BeFalse();

        await PaxAssert.Status(await Create(Wang, Event("ev-wang-plain", Copy(_w.TP, _w.APrice, allow: false))), HttpStatusCode.Created);
        await PaxAssert.Status(await Create(Chen, Event("ev-chen-flag", Copy(_w.TN, _w.ASalary, allow: true))), HttpStatusCode.Created);

        await PaxAssert.Forbidden(await Create(Li, Event("ev-li-flag", Copy(_w.TN, _w.ASalary, allow: true))), null);
        Exists("ev-li-flag").Should().BeFalse();

        await PaxAssert.Status(await Create(Admin, Event("ev-admin-flag", Copy(_w.TN, _w.ASalary, allow: true))), HttpStatusCode.Created);
        var renamed = Event("ev-admin-flag", Copy(_w.TN, _w.ASalary, allow: true), name: "改名");
        await PaxAssert.Forbidden(await _w.As(Wang).PutAsJsonAsync($"{Base}/events/ev-admin-flag", renamed), "eventHiddenSourceNeedsPermissionAdmin");
        await PaxAssert.Status(await _w.As(Chen).PutAsJsonAsync($"{Base}/events/ev-admin-flag", renamed), HttpStatusCode.OK);
    }

    private static async Task Rejected(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        JsonDocument.Parse(text).RootElement.GetProperty("errors").EnumerateObject().Select(e => e.Name)
            .Should().Contain(k => k.EndsWith("set[0].allowHiddenSource"), text);
    }

    [Fact]
    public async Task PAX13b_FormulasOverHiddenFields_AreHiddenSources()
    {
        await Rejected(await Create(Chen, Event("ev-f2", Copy(_w.TN, _w.AF2, allow: false))));
        await Rejected(await Create(Chen, Event("ev-expr", Formula(_w.TN, $"{{{_w.AF1}}} * 1", allow: false))));
        await PaxAssert.Status(await Create(Chen, Event("ev-price", Copy(_w.TP, _w.APrice, allow: false))), HttpStatusCode.Created);
        await PaxAssert.Status(await Create(Chen, Event("ev-f2-flag", Copy(_w.TN, _w.AF2, allow: true))), HttpStatusCode.Created);

        using (var scope = _app.Services.CreateScope())
        {
            var module = (await scope.ServiceProvider.GetRequiredService<IModuleRepository>().GetByKeyAsync(A, includeChildren: true, CancellationToken.None))!;
            var hidden = await scope.ServiceProvider.GetRequiredService<EventValidator>().HiddenAsync(module, CancellationToken.None);
            hidden.Should().Contain([_w.ASalary, _w.AF1, _w.AF2]);
        }

        // 既有事件從 F3 複製、旗標為 false；把 F3 改成引用 F1 → 改欄位 400（UpdateFieldHandler 比對新規則）
        var f3 = await Field(_w.As(Admin), A, "formula", "F3", new { formula = $"{{{_w.APrice}}}", resultType = "number" });
        await PaxAssert.Status(await Create(Chen, Event("ev-f3", Copy(_w.TN, f3, allow: false))), HttpStatusCode.Created);
        var blockId = _w.Db(db => db.Set<Field>().Single(f => f.Id == f3).BlockId);
        var res = await _w.As(Chen).PutAsJsonAsync($"{Base}/fields/{f3}", new
        {
            blockId, type = "formula", label = new Dictionary<string, string> { ["zh-TW"] = "F3" },
            config = new { formula = $"{{{_w.AF1}}}", resultType = "number" },
        });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("allowHiddenSource");
    }
}
