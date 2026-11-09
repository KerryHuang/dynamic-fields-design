using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>共用主檔測試的共同步驟：匯入 <see cref="SharedMasterPackages"/>、開關、查資料庫、跑完整性檢查。</summary>
internal sealed class SharedMasterRig : IDisposable
{
    public const string Apps = Base + "/apps";

    public TestApp App { get; }
    public HttpClient Admin { get; }

    /// <summary>每個使用者套件的群組預設對應到哪個既有群組（DemoPeople 的群組 key）。</summary>
    public Dictionary<string, (string Code, string GroupKey)> GroupMap { get; } = new()
    {
        ["crmx"] = ("sales", "sales"), ["stk"] = ("clerk", "warehouse"), ["mx"] = ("floor", "floor")
    };

    public SharedMasterRig(bool fileDatabase = false)
    {
        App = fileDatabase ? new TestApp(fileDatabase: true) : new TestApp();
        Admin = App.AsUser();
    }

    public void Dispose() => App.Dispose();

    public async Task ImportAllAsync()
    {
        foreach (var json in new[] { SharedMasterPackages.Bp, SharedMasterPackages.Ct, SharedMasterPackages.Crm, SharedMasterPackages.Stock,
                     SharedMasterPackages.Mes, SharedMasterPackages.ContactOnly })
            await ImportAsync(json);
    }

    public async Task ImportAsync(string json)
    {
        var res = await Admin.PostAsync($"{Apps}/packages/import", new StringContent(json, Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    public long GroupId(string key) => Db(db => db.Set<PermissionGroup>().Single(g => g.Key == key).Id);

    /// <summary>使用者套件的群組照 <see cref="GroupMap"/> 對應，主檔的 steward 群組一律新建。</summary>
    public object BodyFor(string id)
    {
        var groupMap = new Dictionary<string, object>();
        if (GroupMap.TryGetValue(id, out var g)) groupMap[g.Code] = new { mode = "map", groupId = GroupId(g.GroupKey) };
        return new
        {
            groupMap,
            masters = new Dictionary<string, object>
            {
                ["bp"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } },
                ["ct"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } }
            }
        };
    }

    public Task<HttpResponseMessage> OpenAsync(string id, object? body = null) =>
        Admin.PostAsJsonAsync($"{Apps}/packages/{id}/open", body ?? BodyFor(id));

    public async Task<JsonElement> OpenOkAsync(string id, object? body = null)
    {
        var res = await OpenAsync(id, body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync() + LastError());
        return await JsonOf(res);
    }

    public async Task CloseOkAsync(string key)
    {
        var res = await Admin.PostAsJsonAsync($"{Apps}/{key}/close", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    public async Task ReopenOkAsync(string key)
    {
        var res = await Admin.PostAsJsonAsync($"{Apps}/{key}/reopen", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    /// <summary>500 時帶上伺服器記下的最後一筆錯誤，才查得到原因。</summary>
    public string LastError() =>
        App.Logs.Entries.LastOrDefault(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error).Message ?? "";

    public AppStatus? StatusOf(string key) => Db(db => db.Set<App>().Where(a => a.Key == key).Select(a => (AppStatus?)a.Status).FirstOrDefault());

    public T Db<T>(Func<DynamicDbContext, T> query)
    {
        using var scope = App.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    public void Db(Action<DynamicDbContext> action)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        action(db);
        db.SaveChanges();
    }

    /// <summary>不變式（使用者閉包）與「權限列＝重算結果」都要成立。</summary>
    public async Task AssertIntegrityAsync()
    {
        using var scope = App.Services.CreateScope();
        var problems = await scope.ServiceProvider.GetRequiredService<MasterLifecycle>().CheckAsync(CancellationToken.None);
        problems.Should().BeEmpty();
    }

    /// <summary>主檔模組 key 的欄位 id（依對照列的符號，如 bp/partner.industry 或 bp 名下的 partner.name）。</summary>
    public long FieldId(string appKey, string name) => Db(db =>
        (from a in db.Set<App>() where a.Key == appKey
         join f in db.Set<AppField>() on a.Id equals f.AppId
         where f.Name == name select f.FieldId).Single());

    public string FieldKey(string appKey, string name) => "f_" + FieldId(appKey, name);

    /// <summary>主檔模組的區塊，依 Sequence；每個區塊回傳（標籤, 欄位標籤）。</summary>
    public List<(string Block, List<string> Fields)> Blocks(string moduleKey) => Db(db =>
    {
        var module = db.Set<Module>().Single(m => m.Key == moduleKey);
        return db.Set<Block>().Where(b => b.ModuleId == module.Id).OrderBy(b => b.Sequence).ToList()
            .Select(b => (b.LabelI18n.Resolve("zh-TW"),
                db.Set<Field>().Where(f => f.BlockId == b.Id).OrderBy(f => f.Sequence).ToList()
                    .Select(f => $"{f.LabelI18n.Resolve("zh-TW")}:{f.Type}:{f.Status}").ToList())).ToList();
    });

    /// <summary>GET /apps/{key}/export：回傳套件檔與 X-Export-Warnings 標頭裡的警告。</summary>
    public async Task<(JsonElement Package, List<string> Warnings)> ExportAsync(string key)
    {
        var res = await Admin.GetAsync($"{Apps}/{key}/export");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync() + LastError());
        var warnings = res.Headers.TryGetValues("X-Export-Warnings", out var values)
            ? values.SelectMany(v => v.Split('|')).Where(w => w.Length > 0).Select(Uri.UnescapeDataString).ToList()
            : [];
        return (await JsonOf(res), warnings);
    }

    /// <summary>設計器（Designer）在主檔模組的某個區塊新增欄位；回傳欄位 Id。</summary>
    public async Task<long> AddDesignerFieldAsync(string moduleKey, long blockId, string type, string label, object? config = null)
    {
        var res = await Admin.PostAsJsonAsync($"{Base}/modules/{moduleKey}/fields",
            new { blockId, type, label = new Dictionary<string, string> { ["zh-TW"] = label }, config = config ?? new { } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync() + LastError());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    public async Task<long> AddDesignerOptionAsync(long fieldId, string label)
    {
        var res = await Admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = new Dictionary<string, string> { ["zh-TW"] = label }, color = (string?)null });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    public async Task SetFieldStatusOkAsync(long fieldId, string status)
    {
        var res = await Admin.PutAsJsonAsync($"{Base}/fields/{fieldId}/status", new { status });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    /// <summary>應用程式名下某個區塊對照列的區塊 Id（例如 bp 的 partner.main、crmx 的 bp/partner.crm）。</summary>
    public long BlockId(string appKey, string name) => Db(db =>
        (from a in db.Set<App>() where a.Key == appKey
         join b in db.Set<AppBlock>() on a.Id equals b.AppId
         where b.Name == name select b.BlockId).Single());

    /// <summary>刪除應用程式（先預覽拿確認數）。</summary>
    public async Task DeleteAppOkAsync(string key)
    {
        var preview = await JsonOf(await Admin.DeleteAsync($"{Apps}/{key}?purged=-1"));
        preview.GetProperty("canDelete").GetBoolean().Should().BeTrue(preview.ToString());
        var res = await Admin.DeleteAsync($"{Apps}/{key}?purged={preview.GetProperty("purgedTotal").GetInt32()}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    public async Task<JsonElement> CreateRecordAsync(HttpClient c, string moduleKey, object values)
    {
        var res = await c.PostAsJsonAsync($"{Base}/modules/{moduleKey}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync() + LastError());
        return await JsonOf(res);
    }
}
