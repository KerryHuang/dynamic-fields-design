using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>第 1 階段驗收情境：模具維修申請，從建模組到刪除記錄。</summary>
public class Phase1AcceptanceTests : IClassFixture<TestApp>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _sales;
    private readonly TestApp _app;

    public Phase1AcceptanceTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
        _sales = app.AsUser("sales01");
    }

    private static Dictionary<string, string> Zh(string t) => new() { ["zh-TW"] = t };

    private async Task<(long Id, string Key)> AddField(long blockId, string key, string type, string label, object? config = null)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{key}/fields",
            new { blockId, type, label = Zh(label), config = config ?? new { } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        var f = await JsonOf(res);
        return (f.GetProperty("id").GetInt64(), f.GetProperty("key").GetString()!);
    }

    private async Task<long> AddOption(long fieldId, string label)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh(label) });
        res.StatusCode.Should().Be(HttpStatusCode.Created, label);
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Phase1_Scenario()
    {
        const string module = "repair-request";

        // 1. 建立模組，代碼 DYN00001
        var created = await _admin.PostAsJsonAsync($"{Base}/modules", new { key = module, name = Zh("模具維修申請") });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await JsonOf(created)).GetProperty("programCode").GetString().Should().Be("DYN00001");
        _app.GrantBusinessGroups(module); // 新模組沒有授權，業務才能操作記錄

        // 2. 加區塊
        var blockRes = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/blocks", new { label = Zh("故障資訊") });
        blockRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var blockId = (await JsonOf(blockRes)).GetProperty("id").GetInt64();

        // 3. 加欄位
        var applyDate = await AddField(blockId, module, "date", "申請日", new { required = true });
        var category = await AddField(blockId, module, "enum", "故障類別");
        await AddOption(category.Id, "模具損壞");
        await AddOption(category.Id, "機台異常");
        var outsourced = await AddOption(category.Id, "外包");
        await AddField(blockId, module, "multienum", "檢查項目");
        var cost = await AddField(blockId, module, "currency", "預估費用", new { scale = 0 });
        var urgent = await AddField(blockId, module, "boolean", "是否急件", new { defaultValue = false });
        var email = await AddField(blockId, module, "email", "聯絡 Email");
        var handler = await AddField(blockId, module, "user", "承辦人");
        var note = await AddField(blockId, module, "textarea", "說明");

        var detail = await JsonOf(await _admin.GetAsync($"{Base}/modules/{module}"));
        var fieldTypes = detail.GetProperty("blocks").EnumerateArray()
            .Single(b => b.GetProperty("id").GetInt64() == blockId).GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("type").GetString()).ToList();
        fieldTypes.Should().Equal("date", "enum", "multienum", "currency", "boolean", "email", "user", "textarea");

        // 4. 啟用
        (await _admin.PostAsync($"{Base}/modules/{module}/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{module}"))).GetProperty("status").GetString().Should().Be("Active");

        // 5. sales01 新增記錄：owner、預設值
        var title = await TitleKey(_admin, module);
        var createRec = await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records", new
        {
            values = new Dictionary<string, object>
            {
                [title] = "沖壓模維修",
                [applyDate.Key] = "2026-11-03",
                [category.Key] = $"opt_{outsourced}",
                [cost.Key] = 12000,
                [email.Key] = "a@b.com",
                [handler.Key] = "sales01",
            }
        });
        createRec.StatusCode.Should().Be(HttpStatusCode.Created);
        var rec = await JsonOf(createRec);
        var id = rec.GetProperty("id").GetInt64();
        rec.GetProperty("ownerUserId").GetString().Should().Be("sales01");
        rec.GetProperty("values").GetProperty(urgent.Key).GetBoolean().Should().BeFalse();
        var rv1 = rec.GetProperty("rowVersion").GetString();

        // 6. 修改說明，rowVersion 改變
        var update = await _sales.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object>
            {
                [title] = "沖壓模維修",
                [applyDate.Key] = "2026-11-03",
                [category.Key] = $"opt_{outsourced}",
                [note.Key] = "模面磨損",
            },
            rowVersion = rv1
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await JsonOf(update);
        var rv2 = updated.GetProperty("rowVersion").GetString();
        rv2.Should().NotBe(rv1);
        (await JsonOf(await _sales.GetAsync($"{Base}/records/{id}")))
            .GetProperty("values").GetProperty(note.Key).GetString().Should().Be("模面磨損");

        // 7. 停用「外包」後，原本選外包的記錄改其他欄位仍成功；新記錄不能再選外包
        (await _admin.PutAsJsonAsync($"{Base}/options/{outsourced}", new { label = Zh("外包"), active = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var keepOld = await _sales.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object>
            {
                [title] = "沖壓模維修",
                [applyDate.Key] = "2026-11-03",
                [category.Key] = $"opt_{outsourced}",
                [note.Key] = "改說明",
            },
            rowVersion = rv2
        });
        keepOld.StatusCode.Should().Be(HttpStatusCode.OK);
        var rv3 = (await JsonOf(keepOld)).GetProperty("rowVersion").GetString();

        var newWithInactive = await _sales.PostAsJsonAsync($"{Base}/modules/{module}/records", new
        {
            values = new Dictionary<string, object>
            {
                [title] = "另一筆",
                [applyDate.Key] = "2026-11-03",
                [category.Key] = $"opt_{outsourced}",
            }
        });
        newWithInactive.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(newWithInactive)).GetProperty("errors").GetProperty(category.Key)[0].GetString()
            .Should().Be("選項不存在或已停用");

        // 8. 刪除，不在 paged 列表
        (await _sales.DeleteAsync($"{Base}/records/{id}?rowVersion={rv3}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await JsonOf(await _sales.GetAsync($"{Base}/modules/{module}/records/paged?currentPage=1&pagesSize=10"));
        paged.GetProperty("totalCount").GetInt32().Should().Be(0);
        paged.ToString().Should().NotContain("沖壓模維修");
    }
}
