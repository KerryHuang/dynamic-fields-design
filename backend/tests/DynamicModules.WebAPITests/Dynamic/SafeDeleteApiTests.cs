using System.Net;
using System.Net.Http.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>安全刪除的 HTTP 行為與併發；用檔案型 SQLite，與正式站相同的鎖行為。</summary>
public class SafeDeleteApiTests : IDisposable
{
    private readonly TestApp _app = new(fileDatabase: true);
    private readonly HttpClient _admin;

    public SafeDeleteApiTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    /// <summary>建立啟用的模組、新增一筆再軟刪除，最後停用；回傳模組鍵與記錄 Id。</summary>
    private async Task<(string Key, long RecordId)> StoppedModuleWithSoftDeletedRecord()
    {
        var key = await CreateModule(_admin);
        var field = await AddField(_admin, key, "text", new { isFilterable = true });
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        var title = await TitleKey(_admin, key);
        var sales = _app.AsUser("sales01");
        var created = await JsonOf(await sales.PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "t", [field.Key] = "v" } }));
        var id = created.GetProperty("id").GetInt64();
        var rowVersion = created.GetProperty("rowVersion").GetString();
        (await sales.DeleteAsync($"{Base}/records/{id}?rowVersion={rowVersion}")).EnsureSuccessStatusCode();
        (await _admin.PostAsync($"{Base}/modules/{key}/deactivate", null)).EnsureSuccessStatusCode();
        return (key, id);
    }

    [Fact]
    public async Task DeleteModule_FullFlow_CheckWarnsThenConfirmedDeleteSucceeds()
    {
        var (key, _) = await StoppedModuleWithSoftDeletedRecord();

        var check = await JsonOf(await _admin.GetAsync($"{Base}/modules/{key}/delete-check"));
        check.GetProperty("canDelete").GetBoolean().Should().BeTrue();
        check.GetProperty("purge").GetProperty("records").GetInt32().Should().Be(1);

        (await _admin.DeleteAsync($"{Base}/modules/{key}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var wrong = await _admin.DeleteAsync($"{Base}/modules/{key}?purged=0");
        wrong.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(wrong)).GetProperty("purge").GetProperty("records").GetInt32().Should().Be(1);

        (await _admin.DeleteAsync($"{Base}/modules/{key}?purged=1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.GetAsync($"{Base}/modules/{key}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync($"{Base}/modules/{key}?purged=1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteModule_WithLiveRecord_Returns409WithBlockerList()
    {
        var key = await CreateModule(_admin);
        await Activate(_admin, key);
        _app.GrantBusinessGroups(key);
        var title = await TitleKey(_admin, key);
        (await _app.AsUser("sales01").PostAsJsonAsync($"{Base}/modules/{key}/records",
            new { values = new Dictionary<string, object> { [title] = "t" } })).EnsureSuccessStatusCode();
        await _admin.PostAsync($"{Base}/modules/{key}/deactivate", null);

        var res = await _admin.DeleteAsync($"{Base}/modules/{key}?purged=0");

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(res);
        var blocker = body.GetProperty("blockers").EnumerateArray().Single();
        blocker.GetProperty("kind").GetString().Should().Be("liveRecords");
        blocker.GetProperty("count").GetInt32().Should().Be(1);
        (await _admin.GetAsync($"{Base}/modules/{key}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteField_Block_Endpoints_Work()
    {
        var key = await CreateModule(_admin);
        var f = await AddField(_admin, key, "text");
        (await _admin.DeleteAsync($"{Base}/fields/{f.Id}?purged=0")).StatusCode.Should().Be(HttpStatusCode.Conflict); // 啟用中
        (await _admin.PutAsJsonAsync($"{Base}/fields/{f.Id}/status", new { status = "Inactive" })).EnsureSuccessStatusCode();
        var check = await JsonOf(await _admin.GetAsync($"{Base}/fields/{f.Id}/delete-check"));
        check.GetProperty("canDelete").GetBoolean().Should().BeTrue();
        (await _admin.DeleteAsync($"{Base}/fields/{f.Id}?purged=0")).StatusCode.Should().Be(HttpStatusCode.OK);

        var lastBlock = await _admin.GetAsync($"{Base}/blocks/{f.BlockId}/delete-check");
        (await JsonOf(lastBlock)).GetProperty("blockers").EnumerateArray()
            .Select(b => b.GetProperty("kind").GetString()).Should().Contain("lastBlock");
        (await _admin.DeleteAsync($"{Base}/blocks/{f.BlockId}?purged=0")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task WithoutDesignerDelete_Returns403_AndNothingIsDeleted()
    {
        var key = await CreateModule(_admin);
        var sales = _app.AsUser("sales01");

        (await sales.GetAsync($"{Base}/modules/{key}/delete-check")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.DeleteAsync($"{Base}/modules/{key}?purged=0")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.DeleteAsync($"{Base}/fields/1?purged=0")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.DeleteAsync($"{Base}/blocks/1?purged=0")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _admin.GetAsync($"{Base}/modules/{key}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var anon = _app.AsUser(null);
        (await anon.DeleteAsync($"{Base}/modules/x?purged=0")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync($"{Base}/modules/x/delete-check")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 刪除與啟用、重複刪除同時打進來：不論誰贏，資料庫都不能留下半套——
    /// 模組不在了就不該有它的記錄、索引、歷程；模組還在就原封不動。
    /// 落敗的請求可能拿到資料庫忙碌錯誤（5xx）而不是乾淨的 4xx，這裡不要求，只要求不留半套。
    /// </summary>
    [Fact]
    public async Task ConcurrentDeleteAndActivate_NeverLeavesAHalfDeletedModule()
    {
        for (var round = 0; round < 4; round++)
        {
            var (key, recordId) = await StoppedModuleWithSoftDeletedRecord();
            using var scope = _app.Services.CreateScope();
            var moduleId = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Module>()
                .Where(m => m.Key == key).Select(m => m.Id).SingleAsync();
            var (indexBefore, historyBefore) = await Counts(recordId);

            var tasks = new[]
            {
                _admin.DeleteAsync($"{Base}/modules/{key}?purged=1"),
                _app.AsUser().DeleteAsync($"{Base}/modules/{key}?purged=1"),
                _app.AsUser().PostAsync($"{Base}/modules/{key}/activate", null)
            };
            var responses = await Task.WhenAll(tasks);
            responses.Count(r => r.StatusCode == HttpStatusCode.OK && r.RequestMessage!.Method == HttpMethod.Delete)
                .Should().BeLessThanOrEqualTo(1, "同一個模組只能被刪一次");

            using var check = _app.Services.CreateScope();
            var db = check.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var exists = await db.Set<Module>().AnyAsync(m => m.Id == moduleId);
            var (index, history) = await Counts(recordId);
            if (exists)
            {
                (await db.Set<Record>().CountAsync(r => r.Id == recordId)).Should().Be(1, "模組還在，記錄不能少");
                index.Should().Be(indexBefore);
                history.Should().Be(historyBefore);
                (await db.Set<Field>().CountAsync(f => f.ModuleId == moduleId)).Should().BeGreaterThan(0);
            }
            else
            {
                (await db.Set<Record>().CountAsync(r => r.ModuleId == moduleId)).Should().Be(0);
                index.Should().Be(0, "模組已刪，索引不能殘留");
                history.Should().Be(0, "模組已刪，歷程不能殘留");
                (await db.Set<Field>().CountAsync(f => f.ModuleId == moduleId)).Should().Be(0);
                (await db.Set<Block>().CountAsync(b => b.ModuleId == moduleId)).Should().Be(0);
            }
        }
    }

    private async Task<(int Index, int History)> Counts(long recordId)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        return (await db.Set<RecordIndex>().CountAsync(i => i.RecordId == recordId),
            await db.Set<RecordHistory>().CountAsync(h => h.RecordId == recordId));
    }
}
