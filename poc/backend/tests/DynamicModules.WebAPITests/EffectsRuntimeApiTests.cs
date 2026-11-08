using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>
/// 事件與檢查經 HTTP 的端到端行為（檔案型 SQLite、真正的 WriteGate 與 BEGIN IMMEDIATE）：系統身分、重送冪等、併發下的餘額。
/// 情境：單據建立 → 每張產生一筆 −3 的異動；異動的同品項加總不得為負。
/// </summary>
public class EffectsRuntimeApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public EffectsRuntimeApiTests(TestApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
    }

    private static object Name(string key) => new Dictionary<string, string> { ["zh-TW"] = key };

    private sealed record Rig(string Doc, string DocTitle, string Move, string MoveTitle, string ItemKey, string QtyKey);

    /// <summary>單據（只有標題）與異動（標題、品項、數量）；單據建立事件產生 −3 的異動；異動同品項加總 ≥ 0。</summary>
    private async Task<Rig> BuildAsync(bool grantMove)
    {
        var doc = await CreateModule(_admin);
        await Activate(_admin, doc);
        _app.GrantBusinessGroups(doc);
        var move = await CreateModule(_admin);
        var item = await AddField(_admin, move, "text", new { isFilterable = true });
        var qty = await AddField(_admin, move, "decimal", new { isFilterable = true });
        await Activate(_admin, move);
        if (grantMove) _app.GrantBusinessGroups(move);
        var moveTitle = await TitleKey(_admin, move);

        var ev = await _admin.PostAsJsonAsync($"{Base}/events", new
        {
            key = "ev-" + Guid.NewGuid().ToString("N")[..8], name = Name("out"), sourceModuleKey = doc, active = true,
            definition = new
            {
                trigger = new { on = "create" },
                effects = new[]
                {
                    new
                    {
                        code = "out", kind = "create", targetModuleKey = move,
                        set = new object[]
                        {
                            new { to = long.Parse(moveTitle[2..]), @const = "出貨" },
                            new { to = item.Id, @const = "A" },
                            new { to = qty.Id, @const = -3 }
                        }
                    }
                }
            }
        });
        ev.StatusCode.Should().Be(HttpStatusCode.Created, await ev.Content.ReadAsStringAsync());

        var check = await _admin.PostAsJsonAsync($"{Base}/checks", new
        {
            key = "ck-" + Guid.NewGuid().ToString("N")[..8], name = Name("min"), moduleKey = move, active = true,
            definition = new
            {
                groupBy = new[] { item.Id }, function = "sum", fieldId = qty.Id,
                assert = new { op = "gte", limit = new { @const = 0 } }, message = Name("庫存不足")
            }
        });
        check.StatusCode.Should().Be(HttpStatusCode.Created, await check.Content.ReadAsStringAsync());
        return new Rig(doc, await TitleKey(_admin, doc), move, moveTitle, item.Key, qty.Key);
    }

    private async Task StockInAsync(Rig r, int qty)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{r.Move}/records",
            new { values = new Dictionary<string, object> { [r.MoveTitle] = "進貨", [r.ItemKey] = "A", [r.QtyKey] = qty } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    private async Task<int> CountAsync(string module) =>
        (await JsonOf(await _admin.GetAsync($"{Base}/modules/{module}/records/paged"))).GetProperty("totalCount").GetInt32();

    private static HttpRequestMessage NewDoc(Rig r, string? key = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/modules/{r.Doc}/records")
        {
            Content = JsonContent.Create(new { values = new Dictionary<string, object> { [r.DocTitle] = "單" } })
        };
        if (key is not null) req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    [Fact]
    public async Task A_user_without_any_permission_on_the_target_still_triggers_the_effect_with_system_identity()
    {
        var r = await BuildAsync(grantMove: false);
        await StockInAsync(r, 10);
        var sales = _app.AsUser("sales01");

        // 使用者看不到、也不能寫異動模組
        (await sales.GetAsync($"{Base}/modules/{r.Move}/records/paged")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PostAsJsonAsync($"{Base}/modules/{r.Move}/records", new { values = new Dictionary<string, object> { [r.MoveTitle] = "x" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 但建立單據時，事件以系統身分寫入異動
        var created = await sales.SendAsync(NewDoc(r));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        (await CountAsync(r.Move)).Should().Be(2);

        // 擁有者取來源記錄的擁有者（不是系統）
        var docOwner = (await JsonOf(created)).GetProperty("ownerUserId").GetString();
        var moves = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{r.Move}/records/paged"))).GetProperty("items");
        moves.EnumerateArray().Select(m => m.GetProperty("ownerUserId").GetString()).Should().Contain(docOwner);
    }

    [Fact]
    public async Task Twenty_resends_with_the_same_key_run_the_effect_once()
    {
        var r = await BuildAsync(grantMove: true);
        await StockInAsync(r, 100);
        var key = "resend-" + Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _admin.SendAsync(NewDoc(r, key))));

        responses.Select(x => x.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.Created);
        (await Task.WhenAll(responses.Select(async x => (await JsonOf(x)).GetProperty("id").GetInt64()))).Distinct().Should().HaveCount(1);
        (await CountAsync(r.Doc)).Should().Be(1);
        (await CountAsync(r.Move)).Should().Be(2);   // 進貨 + 一筆出貨
    }

    [Fact]
    public async Task Fifty_concurrent_shipments_against_thirty_four_units_of_stock_never_overdraw()
    {
        var r = await BuildAsync(grantMove: true);
        await StockInAsync(r, 100);   // 100 / 3 = 33 筆可成功

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => _admin.SendAsync(NewDoc(r))));

        responses.Count(x => x.StatusCode == HttpStatusCode.Created).Should().Be(33);
        responses.Where(x => x.StatusCode != HttpStatusCode.Created).Select(x => x.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.BadRequest);
        (await CountAsync(r.Doc)).Should().Be(33);
        (await CountAsync(r.Move)).Should().Be(34);

        var moves = (await JsonOf(await _admin.GetAsync($"{Base}/modules/{r.Move}/records/paged?currentPage=1&pagesSize=100"))).GetProperty("items");
        moves.EnumerateArray().Sum(m => m.GetProperty("values").GetProperty(r.QtyKey).GetDecimal()).Should().Be(1);
    }

    [Fact]
    public async Task Failed_check_message_has_no_numbers_by_default_and_the_whole_request_rolls_back()
    {
        var r = await BuildAsync(grantMove: true);
        // 沒有進貨：建立單據 → 事件產生 −3 → 檢查擋下 → 單據也不留
        var res = await _admin.SendAsync(NewDoc(r));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("庫存不足").And.NotContain("-3");
        (await CountAsync(r.Doc)).Should().Be(0);
        (await CountAsync(r.Move)).Should().Be(0);
    }

    [Fact]
    public async Task Ten_concurrent_reversals_of_one_ledger_record_succeed_exactly_once()
    {
        var module = await CreateModule(_admin);
        var qty = await AddField(_admin, module, "decimal", new { });
        var reversal = await AddField(_admin, module, "reference", new { readOnly = true, refTarget = new { kind = "module", key = module } });
        await Activate(_admin, module);
        var title = await TitleKey(_admin, module);
        var ledger = await _admin.PutAsJsonAsync($"{Base}/modules/{module}/ledger",
            new { reversalFieldId = reversal.Id, negate = new[] { qty.Id }, swap = Array.Empty<long[]>(), confirmFreeze = false });
        ledger.StatusCode.Should().Be(HttpStatusCode.OK, await ledger.Content.ReadAsStringAsync());
        var created = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [title] = "原始", [qty.Key] = 5 } });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await JsonOf(created)).GetProperty("id").GetInt64();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _admin.PostAsJsonAsync($"{Base}/records/{id}/reverse", new { values = new Dictionary<string, object>() })));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).Select(r => r.StatusCode).Should().OnlyContain(c => c == HttpStatusCode.Conflict);
        (await CountAsync(module)).Should().Be(2);
    }
}
