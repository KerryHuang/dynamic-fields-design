using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>
/// 帶表頭的值依呼叫端對表頭的可見性（M6，PAX-14 的 API 部分；權限軸設計 §12.2）：
/// 小王看不到 H.薪資（隱藏）與 H.單價（沒有金額），直接呼叫 API、省略明細鍵時 D.X、D.Y 是空值；admin 照舊帶入。
/// DetailWriter 單元測試（表頭權限而非明細權限）在 ApplicationTests 的 PermissionAxesDetailWriterTests。
/// </summary>
public sealed class PermissionAxesHeaderDefaultTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private PaxWorld _w = null!;
    private string _hTitle = "", _dTitle = "";

    public async Task InitializeAsync()
    {
        _w = await CreateAsync(_app);
        _hTitle = await TitleKey(_w.As(Admin), H);
        _dTitle = await TitleKey(_w.As(Admin), D);
        // 表頭的薪資、單價給預設值：小王新增表頭時寫不到它們，值仍來自預設，才驗得出「新增表頭」那條路徑
        var admin = _w.As(Admin);
        foreach (var (id, type, label, value) in new[] { (_w.HSalary, "decimal", "薪資", 100m), (_w.HPrice, "currency", "單價", 50m) })
        {
            var blockId = _w.Db(db => db.Set<DynamicModules.Domain.Entities.Dynamic.Field>().Single(f => f.Id == id).BlockId);
            var res = await admin.PutAsJsonAsync($"{Base}/fields/{id}", new
            {
                blockId, type, label = new Dictionary<string, string> { ["zh-TW"] = label }, config = new { defaultValue = value },
            });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private object NewLine(string title) => new { values = new Dictionary<string, object> { [_dTitle] = title } };

    private async Task<JsonElement> Read(long id) => await JsonOf(await _w.As(Admin).GetAsync($"{Base}/records/{id}"));

    private JsonElement Line(JsonElement record, string title) => record.GetProperty("details").GetProperty(D).EnumerateArray()
        .Select(r => r.GetProperty("values")).Single(v => v.TryGetProperty(_dTitle, out var t) && t.GetString() == title);

    private static decimal? Num(JsonElement values, long fieldId) =>
        values.TryGetProperty(F(fieldId), out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    /// <summary>admin 建一張表頭（薪資 100、單價 50）帶一列既有明細，回傳 id。</summary>
    private async Task<long> AdminHeader()
    {
        var res = await _w.As(Admin).PostAsJsonAsync($"{Base}/modules/{H}/records", new
        {
            values = new Dictionary<string, object> { [_hTitle] = "H1", [F(_w.HSalary)] = 100, [F(_w.HPrice)] = 50 },
            details = new Dictionary<string, object[]> { [D] = [NewLine("既有")] },
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task AddLine(string user, long id, string title)
    {
        var rowVersion = (await Read(id)).GetProperty("rowVersion").GetString();
        var res = await _w.As(user).PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object> { [_hTitle] = "H1" }, rowVersion,
            details = new Dictionary<string, object[]> { [D] = [NewLine(title)] },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PAX14_HeaderDefaults_FollowCallerHeaderVisibility()
    {
        var id = await AdminHeader();
        var existing = Line(await Read(id), "既有");
        (Num(existing, _w.DX), Num(existing, _w.DY)).Should().Be((100m, 50m), "前提：admin 建的明細照舊帶入");

        await AddLine(Wang, id, "小王的列");
        var record = await Read(id);
        var wangLine = Line(record, "小王的列");
        (Num(wangLine, _w.DX), Num(wangLine, _w.DY)).Should().Be(((decimal?)null, (decimal?)null), "小王看不到表頭的薪資與單價");
        (Num(Line(record, "既有"), _w.DX), Num(Line(record, "既有"), _w.DY)).Should().Be((100m, 50m), "既有明細列不改寫");

        await AddLine(Admin, id, "admin 的列");
        var adminLine = Line(await Read(id), "admin 的列");
        (Num(adminLine, _w.DX), Num(adminLine, _w.DY)).Should().Be((100m, 50m));

        // 小王新增表頭：薪資、單價來自預設值（他寫不到），新明細列同樣是空值
        var created = await _w.As(Wang).PostAsJsonAsync($"{Base}/modules/{H}/records", new
        {
            values = new Dictionary<string, object> { [_hTitle] = "H2" },
            details = new Dictionary<string, object[]> { [D] = [NewLine("小王新表頭")] },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var h2 = await Read((await JsonOf(created)).GetProperty("id").GetInt64());
        (Num(h2.GetProperty("values"), _w.HSalary), Num(h2.GetProperty("values"), _w.HPrice)).Should().Be((100m, 50m), "前提：表頭值來自預設");
        var line = Line(h2, "小王新表頭");
        (Num(line, _w.DX), Num(line, _w.DY)).Should().Be(((decimal?)null, (decimal?)null));
    }
}
