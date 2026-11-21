using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using ModelContextProtocol.Client;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// 寫入端的「不提供給 AI」（mcp2a 計畫 Task 5、D1、D2；使用者 2026-11-21 裁決：不能寫、當成看不到）。被遮欄位經
/// <c>ChannelPermissionRepository</c> 變成隱藏欄位，沿用畫面上隱藏欄位的語意：修改保留舊值、新增只套預設值、不檢查必填；
/// <c>ignoredFields</c> 和不存在的 key 呈現完全相同，AI 問不出它存在；稽核摘要不存它的值；回應不含它與依賴它的公式、彙總
/// （MCP 通道的系統管理員也一樣）。畫面通道不受影響。欄位標記照 <c>McpToolsApiTests.AiExcluded_NotInMcp_ButInRest</c>。
/// </summary>
[Trait("Category", "Postgres")]
public class McpAiWriteMaskTests : IAsyncLifetime
{
    private const string User = "sales01";
    private TestApp _app = null!;
    private HttpClient _admin = null!, _rest = null!;
    private McpClient _mcp = null!;
    private string _doc = "", _child = "";
    private string _title = "", _note = "", _secret = "", _secretReq = "", _price = "", _priceX2 = "", _rollup = "";
    private string _childTitle = "", _qty = "", _childSecret = "", _childBare = "";
    private long _secretId, _childSecretId;

    public async Task InitializeAsync()
    {
        _app = NewApp();
        EnableMcp(_app);
        EnsureUser(_app, User, u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        _admin = _app.AsUser();
        _rest = _app.AsUser(User);

        _doc = await CreateModule(_admin);
        _title = await TitleKey(_admin, _doc);
        (_, _note, _) = await AddField(_admin, _doc, "text");
        var secret = await AddField(_admin, _doc, "text", new { defaultValue = "預設值" });
        var secretReq = await AddField(_admin, _doc, "text", new { required = true });
        var price = await AddField(_admin, _doc, "decimal", new { scale = 2 });
        var priceX2 = await AddField(_admin, _doc, "formula", new { formula = $"{{{price.Id}}} * 2", resultType = "number", scale = 2 });

        _child = await CreateModule(_admin);
        var parentRef = await AddField(_admin, _child, "reference", new { refTarget = new { kind = "module", key = _doc }, relation = "detail" });
        (_, _qty, _) = await AddField(_admin, _child, "decimal", new { scale = 2 });
        var childSecret = await AddField(_admin, _child, "decimal", new { scale = 2, defaultValue = 5 });
        var childBare = await AddField(_admin, _child, "decimal", new { scale = 2 });
        var rollup = await AddField(_admin, _doc, "rollup",
            new { rollup = new { sourceFieldId = parentRef.Id, function = "sum", aggregateFieldId = childSecret.Id } });
        _childTitle = await TitleKey(_admin, _child);
        await Activate(_admin, _doc);
        await Activate(_admin, _child);
        _app.GrantBusinessGroups(_doc);

        foreach (var (module, id) in new[] { (_doc, secret.Id), (_doc, secretReq.Id), (_doc, price.Id), (_child, childSecret.Id), (_child, childBare.Id) })
            await ExcludeFromAi(module, id);
        (_secret, _secretReq, _price, _priceX2, _rollup, _childSecret, _childBare) =
            (secret.Key, secretReq.Key, price.Key, priceX2.Key, rollup.Key, childSecret.Key, childBare.Key);
        (_secretId, _childSecretId) = (secret.Id, childSecret.Id);
        _mcp = await Connect(User);
    }

    public async Task DisposeAsync()
    {
        await _mcp.DisposeAsync();
        _app.Dispose();
    }

    private async Task<McpClient> Connect(string user) =>
        await ConnectAsync(_app, IssueToken(_app, user, McpScopes.Join([McpScopes.BusinessRead, McpScopes.BusinessWrite])).Raw);

    private async Task ExcludeFromAi(string module, long fieldId)
    {
        var m = await JsonOf(await _admin.GetAsync($"{Base}/modules/{module}"));
        var f = m.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(x => x.GetProperty("id").GetInt64() == fieldId);
        var put = await _admin.PutAsJsonAsync($"{Base}/fields/{fieldId}", new Dictionary<string, object>
        {
            ["type"] = f.GetProperty("type").GetString()!, ["label"] = new Dictionary<string, string> { ["zh-TW"] = "欄位" },
            ["config"] = f.GetProperty("config"), ["blockId"] = f.GetProperty("blockId").GetInt64(), ["excludeFromAi"] = true,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    private static string NewIdem() => "k-" + Guid.NewGuid().ToString("N");

    private async Task<JsonElement> Mcp(string tool, object args, McpClient? client = null)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args));
        return (await (client ?? _mcp).CallToolAsync(tool, dict)).StructuredContent!.Value;
    }

    private static JsonElement Ok(JsonElement r)
    {
        r.TryGetProperty("error", out _).Should().BeFalse(r.GetRawText());
        return r;
    }

    private static string[] Ignored(JsonElement r) =>
        r.GetProperty("meta").GetProperty("ignoredFields").EnumerateArray().Select(x => x.GetString()!).ToArray();

    /// <summary>資料庫裡的值（不經任何通道的遮罩）。</summary>
    private JsonElement Stored(long id) =>
        JsonDocument.Parse(Query(_app, db => db.Set<Record>().Single(r => r.Id == id).ValuesJson)).RootElement.Clone();

    private static string? Text(JsonElement values, string key) =>
        values.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Number(JsonElement values, string key) =>
        values.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private async Task<JsonElement> RestCreate(Dictionary<string, object?> values, object? details = null)
    {
        var res = await _rest.PostAsJsonAsync($"{Base}/modules/{_doc}/records", new { values, details });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private Dictionary<string, object?> Full(string title) =>
        new() { [_title] = title, [_secret] = "原值", [_secretReq] = "必填原值", [_price] = 100 };

    [Fact]
    public async Task Update_MaskedField_KeepsOldValue()
    {
        var created = await RestCreate(Full("保留舊值"));
        var id = created.GetProperty("id").GetInt64();

        var r = Ok(await Mcp("update_record", new
        {
            recordId = id, rowVersion = created.GetProperty("rowVersion").GetString(), idempotencyKey = NewIdem(),
            values = new Dictionary<string, object> { [_secret] = "AI 新值", [_note] = "AI 改備註" },
        }));

        var stored = Stored(id);
        Text(stored, _secret).Should().Be("原值");
        Text(stored, _note).Should().Be("AI 改備註");
        Ignored(r).Should().Equal(_secret);
        Query(_app, db => db.Set<RecordHistory>().Count(h => h.RecordId == id && h.FieldId == _secretId && h.Operation == RecordHistoryOperation.Update))
            .Should().Be(0, "歷程沒有被遮欄位的變更");
    }

    [Fact]
    public async Task Create_MaskedField_DefaultOnly()
    {
        var r = Ok(await Mcp("create_record", new
        {
            moduleKey = _doc, idempotencyKey = NewIdem(),
            values = new Dictionary<string, object> { [_title] = "只套預設", [_secret] = "AI 值", [_secretReq] = "AI 必填值" },
        }));

        var stored = Stored(r.GetProperty("data").GetProperty("id").GetInt64());
        Text(stored, _secret).Should().Be("預設值", "有預設值 → 存預設值");
        Text(stored, _secretReq).Should().BeNull("沒有預設值 → 空");
    }

    /// <summary>D2：被遮欄位是必填、又沒有預設值 → AI 建立照樣成功、該欄位空（畫面上看得到它的人仍要填）。</summary>
    [Fact]
    public async Task Create_MaskedRequired_NotBlocked()
    {
        var rest = await _rest.PostAsJsonAsync($"{Base}/modules/{_doc}/records", new { values = new Dictionary<string, object> { [_title] = "畫面缺必填" } });
        rest.StatusCode.Should().Be(HttpStatusCode.BadRequest, "畫面通道看得到這個必填欄位");

        var r = Ok(await Mcp("create_record", new { moduleKey = _doc, idempotencyKey = NewIdem(), values = new Dictionary<string, object> { [_title] = "AI 缺必填" } }));

        Text(Stored(r.GetProperty("data").GetProperty("id").GetInt64()), _secretReq).Should().BeNull();
    }

    /// <summary>D1、判斷 7：送被遮欄位的 key 與一個不存在的 key，兩次結果的 meta 形狀完全相同（同一個陣列、沒有原因欄）。</summary>
    [Fact]
    public async Task IgnoredFields_NoExistenceLeak()
    {
        const string ghost = "f_987654321";
        var masked = Ok(await Mcp("create_record", new
        {
            moduleKey = _doc, idempotencyKey = NewIdem(), values = new Dictionary<string, object> { [_title] = "試探一", [_secret] = "x" },
        }));
        var missing = Ok(await Mcp("create_record", new
        {
            moduleKey = _doc, idempotencyKey = NewIdem(), values = new Dictionary<string, object> { [_title] = "試探二", [ghost] = "x" },
        }));
        var both = Ok(await Mcp("create_record", new
        {
            moduleKey = _doc, idempotencyKey = NewIdem(), values = new Dictionary<string, object> { [_title] = "試探三", [_secret] = "x", [ghost] = "x" },
        }));

        static string MetaShape(JsonElement r, string key) => r.GetProperty("meta").GetRawText().Replace(key, "KEY");
        MetaShape(masked, _secret).Should().Be(MetaShape(missing, ghost));
        Ignored(both).Should().Equal(new[] { _secret, ghost }.Order(StringComparer.Ordinal));
        masked.GetProperty("data").GetRawText().Should().NotContain(_secret);
    }

    [Fact]
    public async Task MaskedValue_NotInAudit()
    {
        const string value = "機密值-7Q3Z";
        Ok(await Mcp("create_record", new
        {
            moduleKey = _doc, idempotencyKey = NewIdem(), values = new Dictionary<string, object> { [_title] = "稽核", [_secret] = value },
        }));

        var rows = Query(_app, db => db.Set<AuditLogEntry>().Where(e => e.Action == "mcp.createRecord").Select(e => e.SummaryJson).ToList());
        rows.Should().ContainSingle();
        rows.Should().OnlyContain(s => s != null && !s.Contains(value));
    }

    private object Row(string title, decimal qty, decimal secret, decimal bare) => new
    {
        values = new Dictionary<string, object> { [_childTitle] = title, [_qty] = qty, [_childSecret] = secret, [_childBare] = bare },
    };

    /// <summary>複審 N3：修改既有明細列時送被遮明細欄位的新值 → 值不變、歷程沒有變更、ignoredFields 不列（只涵蓋表頭）。</summary>
    [Fact]
    public async Task Update_MaskedDetailField_KeepsOldValue()
    {
        var created = await RestCreate(Full("明細保留"), new Dictionary<string, object[]> { [_child] = [Row("第一行", 1, 7, 8)] });
        var id = created.GetProperty("id").GetInt64();
        var row = created.GetProperty("details").GetProperty(_child)[0];
        var rowId = row.GetProperty("id").GetInt64();

        var r = Ok(await Mcp("update_record", new
        {
            recordId = id, rowVersion = created.GetProperty("rowVersion").GetString(), idempotencyKey = NewIdem(),
            details = new Dictionary<string, object[]>
            {
                [_child] = [new { id = rowId, rowVersion = row.GetProperty("rowVersion").GetString(), values = new Dictionary<string, object> { [_qty] = 3, [_childSecret] = 99 } }],
            },
        }));

        var stored = Stored(rowId);
        Number(stored, _qty).Should().Be(3m);
        Number(stored, _childSecret).Should().Be(7m);
        Ignored(r).Should().BeEmpty("ignoredFields 只涵蓋表頭（判斷 7）");
        Query(_app, db => db.Set<RecordHistory>().Count(h => h.RecordId == rowId && h.FieldId == _childSecretId && h.Operation == RecordHistoryOperation.Update))
            .Should().Be(0);
    }

    [Fact]
    public async Task Create_MaskedDetailField_DefaultOnly()
    {
        var r = Ok(await Mcp("create_record", new
        {
            moduleKey = _doc, idempotencyKey = NewIdem(), values = new Dictionary<string, object> { [_title] = "明細預設" },
            details = new Dictionary<string, object[]> { [_child] = [Row("第一行", 2, 99, 98)] },
        }));

        var rowId = r.GetProperty("data").GetProperty("details").GetProperty(_child)[0].GetProperty("id").GetInt64();
        var stored = Stored(rowId);
        Number(stored, _qty).Should().Be(2m);
        Number(stored, _childSecret).Should().Be(5m, "有預設值 → 存預設值");
        Number(stored, _childBare).Should().BeNull("沒有預設值 → 空");
    }

    private void ShouldHideMasked(JsonElement data)
    {
        var values = data.GetProperty("values");
        foreach (var key in new[] { _secret, _secretReq, _price, _priceX2, _rollup })
            values.TryGetProperty(key, out _).Should().BeFalse($"{key} 被遮或依賴被遮欄位");
        foreach (var row in data.GetProperty("details").GetProperty(_child).EnumerateArray())
            foreach (var key in new[] { _childSecret, _childBare })
                row.GetProperty("values").TryGetProperty(key, out _).Should().BeFalse($"明細的 {key} 被遮");
    }

    private Task<JsonElement> CreateWithRow(McpClient client, string title) => Mcp("create_record", new
    {
        moduleKey = _doc, idempotencyKey = NewIdem(), values = new Dictionary<string, object> { [_title] = title, [_price] = 10 },
        details = new Dictionary<string, object[]> { [_child] = [Row("第一行", 1, 1, 1)] },
    }, client);

    [Fact]
    public async Task Response_HidesMaskedAndDependents()
    {
        var created = Ok(await CreateWithRow(_mcp, "回應遮罩"));
        ShouldHideMasked(created.GetProperty("data"));

        var data = created.GetProperty("data");
        var updated = Ok(await Mcp("update_record", new
        {
            recordId = data.GetProperty("id").GetInt64(), rowVersion = data.GetProperty("rowVersion").GetString(), idempotencyKey = NewIdem(),
            values = new Dictionary<string, object> { [_note] = "改" },
        }));
        ShouldHideMasked(updated.GetProperty("data"));
    }

    /// <summary>#74／#75 合併後：系統管理員的彙總短路只對畫面通道生效，MCP 通道的系統管理員照樣被遮；同一位管理員經 REST 看得到。</summary>
    [Fact]
    public async Task Response_HidesMaskedAndDependents_SystemAdmin()
    {
        await using var admin = await Connect("admin");
        var created = Ok(await CreateWithRow(admin, "管理員回應"));
        ShouldHideMasked(created.GetProperty("data"));

        // 被遮的金額 AI 寫不進去（公式因此是空的），被遮的必填也空著（D2：要有人補填）；管理員改由畫面補上，再比兩個通道
        var data = created.GetProperty("data");
        var id = data.GetProperty("id").GetInt64();
        var put = await _admin.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [_price] = 10, [_secretReq] = "補填" }, rowVersion = data.GetProperty("rowVersion").GetString() });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var rest = await JsonOf(await _admin.GetAsync($"{Base}/records/{id}"));
        var values = rest.GetProperty("values");
        foreach (var key in new[] { _secret, _price, _priceX2, _rollup }) values.TryGetProperty(key, out _).Should().BeTrue($"畫面通道的管理員看得到 {key}");
        rest.GetProperty("details").GetProperty(_child)[0].GetProperty("values").TryGetProperty(_childSecret, out _).Should().BeTrue();
        ShouldHideMasked(Ok(await Mcp("get_record", new { recordId = id }, admin)).GetProperty("data"));
    }

    [Fact]
    public async Task SameUser_Ui_CanWrite()
    {
        var created = await RestCreate(Full("畫面可寫"));
        var id = created.GetProperty("id").GetInt64();

        var put = await _rest.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [_secret] = "畫面新值" }, rowVersion = created.GetProperty("rowVersion").GetString() });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        Text(Stored(id), _secret).Should().Be("畫面新值");
    }

    [Fact(Skip = "送簽工具在 Task 7（mcp2a-3）；完成後補：標題樣板含被遮欄位時 MCP 送簽成功、get_record_approval 的標題快照照 mcp1a Q7 是空字串")]
    public Task Approval_Submit_MaskedTitleTemplate() => Task.CompletedTask;
}
