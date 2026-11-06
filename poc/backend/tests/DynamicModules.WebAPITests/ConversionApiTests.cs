using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>單據轉換經 HTTP：設計器權限、偽造系統欄位、以及真正同時發出的兩個轉換請求不能超轉。</summary>
public class ConversionApiTests : IClassFixture<ConversionApiTests.FileDbApp>
{
    // 併發測試需要真實檔案 SQLite（共用記憶體資料庫在寫入交易時會讓其他連線 table is locked）
    public sealed class FileDbApp : TestApp
    {
        public FileDbApp() : base(true, false, true) { }
    }

    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public ConversionApiTests(FileDbApp app)
    {
        _app = app;
        _admin = app.AsUser("admin");
    }

    private sealed record Chain(
        string Src, string SrcLine, string Dst, string DstLine,
        string SrcTitle, string LineTitle, string LineQty, string DstQty, string DstLineFrom,
        long SrcTitleId, long LineTitleId, long LineQtyId, long DstTitleId, long DstQtyId, long DstFromId, long DstLineFromId, long DstLineTitleId);

    private static long Id(string key) => long.Parse(key[2..]);

    private async Task<Chain> Setup()
    {
        var src = await CreateModule(_admin);
        var srcLine = await CreateModule(_admin);
        var dst = await CreateModule(_admin);
        var dstLine = await CreateModule(_admin);
        await AddField(_admin, srcLine, "reference", new { refTarget = new { kind = "module", key = src }, relation = "detail" });
        var qty = await AddField(_admin, srcLine, "decimal");
        var dstFrom = await AddField(_admin, dst, "reference", new { refTarget = new { kind = "module", key = src }, readOnly = true });
        await AddField(_admin, dstLine, "reference", new { refTarget = new { kind = "module", key = dst }, relation = "detail" });
        var dstQty = await AddField(_admin, dstLine, "decimal");
        var dstLineFrom = await AddField(_admin, dstLine, "integer", new { readOnly = true });
        foreach (var m in new[] { src, srcLine, dst, dstLine })
        {
            await Activate(_admin, m);
            _app.GrantBusinessGroups(m);
        }
        var c = new Chain(src, srcLine, dst, dstLine,
            await TitleKey(_admin, src), await TitleKey(_admin, srcLine), qty.Key, dstQty.Key, dstLineFrom.Key,
            Id(await TitleKey(_admin, src)), Id(await TitleKey(_admin, srcLine)), qty.Id,
            Id(await TitleKey(_admin, dst)), dstQty.Id, dstFrom.Id, dstLineFrom.Id, Id(await TitleKey(_admin, dstLine)));
        return c;
    }

    private object Definition(Chain c) => new
    {
        sourceStates = Array.Empty<long>(),
        header = new { sourceFieldId = c.DstFromId, copy = new[] { new { from = c.SrcTitleId, to = c.DstTitleId } } },
        lines = new
        {
            sourceModuleKey = c.SrcLine, targetModuleKey = c.DstLine, sourceLineFieldId = c.DstLineFromId,
            quantity = new { from = c.LineQtyId, to = c.DstQtyId },
            copy = new[] { new { from = c.LineTitleId, to = c.DstLineTitleId } }
        }
    };

    private async Task<string> Define(Chain c)
    {
        var key = "cv-" + Guid.NewGuid().ToString("N")[..8];
        var res = await _admin.PostAsJsonAsync($"{Base}/conversions", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "轉換" },
            sourceModuleKey = c.Src, targetModuleKey = c.Dst, active = true, definition = Definition(c)
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return key;
    }

    private async Task<(long Id, string RowVersion, long LineId)> NewSource(HttpClient client, Chain c, decimal qty)
    {
        var res = await client.PostAsJsonAsync($"{Base}/modules/{c.Src}/records", new
        {
            values = new Dictionary<string, object> { [c.SrcTitle] = "S" },
            details = new Dictionary<string, object[]>
            {
                [c.SrcLine] = [new { values = new Dictionary<string, object> { [c.LineTitle] = "L", [c.LineQty] = qty } }]
            }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        return (body.GetProperty("id").GetInt64(), body.GetProperty("rowVersion").GetString()!,
            body.GetProperty("details").GetProperty(c.SrcLine)[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Designer_Endpoints_RequireDesignerPermission()
    {
        var c = await Setup();
        var key = await Define(c);
        var sales = _app.AsUser("sales01");
        (await sales.GetAsync($"{Base}/conversions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.GetAsync($"{Base}/conversions/{key}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.DeleteAsync($"{Base}/conversions/{key}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _admin.GetAsync($"{Base}/conversions/{key}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Convert_ThenRemainingIsVisible_AndOverConvertIsRejected()
    {
        var c = await Setup();
        var key = await Define(c);
        var sales = _app.AsUser("sales01");
        var s = await NewSource(sales, c, 10);

        var ok = await sales.PostAsJsonAsync($"{Base}/records/{s.Id}/conversions/{key}/convert",
            new { lines = new[] { new { sourceLineId = s.LineId, quantity = 4 } } });
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());

        var status = await JsonOf(await sales.GetAsync($"{Base}/records/{s.Id}/conversions"));
        var line = status[0].GetProperty("lines")[0];
        line.GetProperty("converted").GetDecimal().Should().Be(4);
        line.GetProperty("remaining").GetDecimal().Should().Be(6);
        status[0].GetProperty("targets").GetArrayLength().Should().Be(1);

        var over = await sales.PostAsJsonAsync($"{Base}/records/{s.Id}/conversions/{key}/convert",
            new { lines = new[] { new { sourceLineId = s.LineId, quantity = 7 } } });
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ForgedSystemKeysInDetailsJson_AreIgnored()
    {
        var c = await Setup();
        var s = await NewSource(_app.AsUser("sales01"), c, 5);
        // 直接新增目標單據並在明細列夾帶 systemKeys，企圖寫入唯讀的追溯欄位
        var res = await _app.AsUser("sales01").PostAsJsonAsync($"{Base}/modules/{c.Dst}/records", new
        {
            values = new Dictionary<string, object> { [$"f_{c.DstTitleId}"] = "D" },
            details = new Dictionary<string, object[]>
            {
                [c.DstLine] = [new
                {
                    values = new Dictionary<string, object> { [$"f_{c.DstLineTitleId}"] = "x", [c.DstQty] = 1, [c.DstLineFrom] = s.LineId },
                    systemKeys = new[] { c.DstQty, c.DstLineFrom }
                }]
            }
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var row = (await JsonOf(res)).GetProperty("details").GetProperty(c.DstLine)[0].GetProperty("values");
        row.TryGetProperty(c.DstLineFrom, out _).Should().BeFalse("唯讀的追溯欄位不能由使用者（含偽造的 systemKeys）寫入");
        row.GetProperty(c.DstQty).GetDecimal().Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentConversions_NeverConvertMoreThanTheSource()
    {
        var c = await Setup();
        var key = await Define(c);
        var s = await NewSource(_app.AsUser("sales01"), c, 10);

        // 兩個人各轉 6（合計 12 > 10）：用 Barrier 讓兩個請求同時送出
        var users = new[] { "sales01", "sales02" };
        var gate = new Barrier(users.Length);
        var results = await Task.WhenAll(users.Select(u => Task.Run(async () =>
        {
            var client = _app.AsUser(u);
            gate.SignalAndWait();
            return (await client.PostAsJsonAsync($"{Base}/records/{s.Id}/conversions/{key}/convert",
                new { lines = new[] { new { sourceLineId = s.LineId, quantity = 6 } } })).StatusCode;
        })));

        results.Count(r => r == HttpStatusCode.Created).Should().Be(1, string.Join(",", results));
        results.Where(r => r != HttpStatusCode.Created).Should().OnlyContain(r => r == HttpStatusCode.Conflict || r == HttpStatusCode.BadRequest);

        var status = await JsonOf(await _admin.GetAsync($"{Base}/records/{s.Id}/conversions"));
        status[0].GetProperty("lines")[0].GetProperty("converted").GetDecimal().Should().Be(6);
        status[0].GetProperty("targets").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentConversions_ManyRounds_TotalNeverExceedsSource()
    {
        var c = await Setup();
        var key = await Define(c);
        var s = await NewSource(_app.AsUser("sales01"), c, 10);
        var users = new[] { "sales01", "sales02", "manager01", "admin" };
        for (var round = 0; round < 4; round++)
        {
            var gate = new Barrier(users.Length);
            await Task.WhenAll(users.Select(u => Task.Run(async () =>
            {
                var client = _app.AsUser(u);
                gate.SignalAndWait();
                await client.PostAsJsonAsync($"{Base}/records/{s.Id}/conversions/{key}/convert",
                    new { lines = new[] { new { sourceLineId = s.LineId, quantity = 3 } } });
            })));
        }
        var status = await JsonOf(await _admin.GetAsync($"{Base}/records/{s.Id}/conversions"));
        var line = status[0].GetProperty("lines")[0];
        line.GetProperty("converted").GetDecimal().Should().BeLessThanOrEqualTo(10);
        line.GetProperty("remaining").GetDecimal().Should().BeGreaterThanOrEqualTo(0);
        status[0].GetProperty("targets").GetArrayLength().Should().Be((int)(line.GetProperty("converted").GetDecimal() / 3));
    }
}
