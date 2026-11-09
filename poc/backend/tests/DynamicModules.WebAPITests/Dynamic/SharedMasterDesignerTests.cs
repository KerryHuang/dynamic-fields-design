using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 設計器在主檔模組上的歸屬（設計 §5.4）：加在基本區塊的歸主檔、加在貢獻者區塊的歸貢獻者，建立時就寫對照列與持久代號；
/// 跨歸屬拖曳 409；孤兒區塊不能新增或拖入、孤兒欄位不能拖出。
/// </summary>
public class SharedMasterDesignerTests : IDisposable
{
    private const string Partner = "master-bp-partner";
    private static readonly Regex PersistentCode = new("^u[a-z0-9]{8}$");
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task OpenCrmAndStockAsync()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
    }

    private long BlockOf(string appKey, string name) => _rig.Db(db =>
        (from a in db.Set<App>() where a.Key == appKey
         join b in db.Set<AppBlock>() on a.Id equals b.AppId
         where b.Name == name select b.BlockId).Single());

    private (string App, string Name)[] FieldBindings(long fieldId) => _rig.Db(db =>
        (from b in db.Set<AppField>() where b.FieldId == fieldId
         join a in db.Set<App>() on b.AppId equals a.Id select new { a.Key, b.Name }).ToList()
        .Select(x => (x.Key, x.Name)).ToArray());

    private (string App, string Name)[] OptionBindings(long optionId) => _rig.Db(db =>
        (from b in db.Set<AppOption>() where b.OptionId == optionId
         join a in db.Set<App>() on b.AppId equals a.Id select new { a.Key, b.Name }).ToList()
        .Select(x => (x.Key, x.Name)).ToArray());

    private (string App, string Name)[] BlockBindings(long blockId) => _rig.Db(db =>
        (from b in db.Set<AppBlock>() where b.BlockId == blockId
         join a in db.Set<App>() on b.AppId equals a.Id select new { a.Key, b.Name }).ToList()
        .Select(x => (x.Key, x.Name)).ToArray());

    private static Task<HttpResponseMessage> PostField(HttpClient c, long blockId, string type, string label) =>
        c.PostAsJsonAsync($"{Base}/modules/{Partner}/fields",
            new { blockId, type, label = new Dictionary<string, string> { ["zh-TW"] = label }, config = new { } });

    private async Task<long> AddFieldOk(long blockId, string type, string label)
    {
        var res = await PostField(_rig.Admin, blockId, type, label);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync() + _rig.LastError());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> PostOption(HttpClient c, long fieldId, string label) =>
        c.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = new Dictionary<string, string> { ["zh-TW"] = label }, color = (string?)null });

    private async Task<long> AddOptionOk(long fieldId, string label)
    {
        var res = await PostOption(_rig.Admin, fieldId, label);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    /// <summary>設計器的「存欄位」：型別、標籤、設定照現在的值，只換區塊。</summary>
    private async Task<HttpResponseMessage> MoveAsync(HttpClient c, long fieldId, long blockId)
    {
        var field = _rig.Db(db => db.Set<Field>().Single(f => f.Id == fieldId));
        var module = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{Partner}"));
        var dto = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .Single(f => f.GetProperty("id").GetInt64() == fieldId);
        return await c.PutAsJsonAsync($"{Base}/fields/{fieldId}", new
        {
            type = dto.GetProperty("type").GetString(), label = field.LabelI18n, config = dto.GetProperty("config"), blockId
        });
    }

    [Fact]
    public async Task AFieldOrOptionAddedInTheBaseBlock_BelongsToTheMaster_WithAPersistentCode()
    {
        await OpenCrmAndStockAsync();
        var main = BlockOf("bp", "partner.main");
        var field = await AddFieldOk(main, "enum", "等級");
        var option = await AddOptionOk(field, "金牌");

        var bound = FieldBindings(field).Should().ContainSingle().Subject;
        bound.App.Should().Be("bp");
        bound.Name.Should().StartWith("partner.");
        PersistentCode.IsMatch(bound.Name["partner.".Length..]).Should().BeTrue(bound.Name);
        bound.Name.Should().NotBe($"partner.f_{field}", "代號與資料庫 Id 無關");
        var optionBound = OptionBindings(option).Should().ContainSingle().Subject;
        optionBound.App.Should().Be("bp");
        PersistentCode.IsMatch(optionBound.Name["partner.".Length..]).Should().BeTrue(optionBound.Name);
        _rig.Db(db => db.Set<Field>().Single(f => f.Id == field).VisibilityRestricted).Should().BeFalse("基本欄位照一般權限");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task ABlockAddedOnTheMaster_BelongsToIt_AndStaysBeforeTheContributionBlocks()
    {
        await OpenCrmAndStockAsync();
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Partner}/blocks", new { label = new Dictionary<string, string> { ["zh-TW"] = "財務" } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var block = (await JsonOf(res)).GetProperty("id").GetInt64();

        var bound = BlockBindings(block).Should().ContainSingle().Subject;
        bound.App.Should().Be("bp");
        PersistentCode.IsMatch(bound.Name["partner.".Length..]).Should().BeTrue(bound.Name);
        _rig.Blocks(Partner).Select(b => b.Block).Should().Equal("基本資料", "財務", "CRM", "進銷存");

        // 客戶新建的區塊也是基本區塊：加進去的欄位歸主檔
        var field = await AddFieldOk(block, "text", "開戶銀行");
        FieldBindings(field).Should().ContainSingle().Which.App.Should().Be("bp");
    }

    [Fact]
    public async Task AFieldAddedInTheCrmBlock_BelongsToCrm_IsRestricted_AndFrozenWhenCrmCloses()
    {
        await OpenCrmAndStockAsync();
        var crm = BlockOf("crmx", "bp/partner.crm");
        var field = await AddFieldOk(crm, "enum", "拜訪頻率");
        var option = await AddOptionOk(field, "每月");

        var bound = FieldBindings(field).Should().ContainSingle().Subject;
        bound.App.Should().Be("crmx");
        bound.Name.Should().StartWith("bp/partner.");
        PersistentCode.IsMatch(bound.Name["bp/partner.".Length..]).Should().BeTrue(bound.Name);
        OptionBindings(option).Should().ContainSingle().Which.Should().Match<(string App, string Name)>(x => x.App == "crmx" && x.Name.StartsWith("bp/partner.u"));
        _rig.Db(db => db.Set<Field>().Single(f => f.Id == field).VisibilityRestricted).Should().BeTrue();
        var sales = _rig.GroupId("sales");
        _rig.Db(db => db.Set<FieldGrant>().Where(g => g.FieldId == field).Select(g => new { g.GroupId, g.SourceKind }).ToList())
            .Should().BeEquivalentTo([new { GroupId = sales, SourceKind = GrantSource.App }], "只替貢獻者映射到的群組寫可見列");

        // 寫一筆值，再關閉 CRM：這個欄位跟著貢獻欄位一起凍結
        var name = _rig.FieldKey("bp", "partner.name");
        var record = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object> { [name] = "甲公司", [$"f_{field}"] = $"opt_{option}" });
        await _rig.CloseOkAsync("crmx");
        var id = record.GetProperty("id").GetInt64();
        var fresh = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{id}"));
        var put = await _rig.Admin.PutAsJsonAsync($"{Base}/records/{id}", new
        {
            values = new Dictionary<string, object?> { [$"f_{field}"] = null }, rowVersion = fresh.GetProperty("rowVersion").GetString()
        });
        put.StatusCode.Should().Be(HttpStatusCode.Conflict, await put.Content.ReadAsStringAsync());
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task MovingAFieldAcrossOwners_Is409_WithinTheSameOwnerItIsFine()
    {
        await OpenCrmAndStockAsync();
        var main = BlockOf("bp", "partner.main");
        var crm = BlockOf("crmx", "bp/partner.crm");
        var stock = BlockOf("stk", "bp/partner.stock");
        var phone = _rig.FieldId("bp", "partner.phone");
        var memo = _rig.FieldId("crmx", "bp/partner.memo");
        var visits = await AddFieldOk(crm, "text", "拜訪紀錄");

        (await MoveAsync(_rig.Admin, phone, crm)).StatusCode.Should().Be(HttpStatusCode.Conflict, "基本欄位不能拖進貢獻者區塊");
        (await MoveAsync(_rig.Admin, memo, main)).StatusCode.Should().Be(HttpStatusCode.Conflict, "貢獻欄位不能拖進基本區塊");
        (await MoveAsync(_rig.Admin, visits, stock)).StatusCode.Should().Be(HttpStatusCode.Conflict, "不同貢獻者之間也不行");
        var body = await (await MoveAsync(_rig.Admin, memo, stock)).Content.ReadAsStringAsync();
        body.Should().Contain("不同的擁有者");
        _rig.Db(db => db.Set<Field>().Single(f => f.Id == phone).BlockId).Should().Be(main);

        // 同一個擁有者的區塊之間照常可以移
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Partner}/blocks", new { label = new Dictionary<string, string> { ["zh-TW"] = "聯絡" } });
        var extra = (await JsonOf(res)).GetProperty("id").GetInt64();
        (await MoveAsync(_rig.Admin, phone, extra)).StatusCode.Should().Be(HttpStatusCode.OK);
        FieldBindings(phone).Should().ContainSingle().Which.App.Should().Be("bp", "歸屬在建立時決定，移動不換主人");
    }

    [Fact]
    public async Task TheOrphanBlockOfADeletedApp_TakesNoNewFieldOrOption_NothingMovesInOrOut()
    {
        await OpenCrmAndStockAsync();
        var industry = _rig.FieldId("crmx", "bp/partner.industry");
        var option = _rig.Db(db => db.Set<FieldOption>().First(o => o.FieldId == industry).Id);
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
        {
            [_rig.FieldKey("bp", "partner.name")] = "甲公司", [$"f_{industry}"] = $"opt_{option}"
        });
        var crm = BlockOf("crmx", "bp/partner.crm");
        await _rig.CloseOkAsync("crmx");
        await DeleteAppAsync("crmx");
        _rig.Db(db => db.Set<AppOrphan>().Any(o => o.Kind == OrphanKind.Block && o.BlockId == crm)).Should().BeTrue();

        var add = await PostField(_rig.Admin, crm, "text", "新欄位");
        add.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await add.Content.ReadAsStringAsync()).Should().Contain("已刪除的應用程式").And.Contain("基本區塊");
        (await PostOption(_rig.Admin, industry, "服務")).StatusCode.Should().Be(HttpStatusCode.Conflict, "孤兒欄位不能加選項");
        var phone = _rig.FieldId("bp", "partner.phone");
        var into = await MoveAsync(_rig.Admin, phone, crm);
        into.StatusCode.Should().Be(HttpStatusCode.Conflict, "不能拖進孤兒區塊");
        (await into.Content.ReadAsStringAsync()).Should().Contain("已刪除的應用程式");
        (await MoveAsync(_rig.Admin, industry, BlockOf("bp", "partner.main"))).StatusCode.Should().Be(HttpStatusCode.Conflict, "孤兒欄位不能拖出");
        _rig.Db(db => db.Set<Field>().Count(f => f.BlockId == crm)).Should().Be(1);
    }

    [Fact]
    public async Task ADesignerFieldInTheCrmBlock_IsOrphanedOrDroppedWithCrm_LikeAContribution()
    {
        await OpenCrmAndStockAsync();
        var crm = BlockOf("crmx", "bp/partner.crm");
        var valued = await AddFieldOk(crm, "text", "拜訪紀錄");
        var empty = await AddFieldOk(crm, "text", "備用");
        var code = FieldBindings(valued).Single().Name;
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
        {
            [_rig.FieldKey("bp", "partner.name")] = "甲公司", [$"f_{valued}"] = "每週一次"
        });
        await _rig.CloseOkAsync("crmx");
        await DeleteAppAsync("crmx");

        _rig.Db(db => db.Set<Field>().Single(f => f.Id == valued).Status).Should().Be(FieldStatus.Inactive, "有值的停用保留");
        _rig.Db(db => db.Set<AppOrphan>().Any(o => o.Kind == OrphanKind.Field && o.FieldId == valued && o.Name == code)).Should().BeTrue();
        _rig.Db(db => db.Set<Field>().Any(f => f.Id == empty)).Should().BeFalse("沒有值的直接刪");
        await _rig.AssertIntegrityAsync();
    }

    private async Task DeleteAppAsync(string key)
    {
        var preview = await _rig.Admin.DeleteAsync($"{SharedMasterRig.Apps}/{key}?purged=-1");
        var check = await JsonOf(preview);
        check.GetProperty("canDelete").GetBoolean().Should().BeTrue(check.ToString());
        var res = await _rig.Admin.DeleteAsync($"{SharedMasterRig.Apps}/{key}?purged={check.GetProperty("purgedTotal").GetInt32()}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }
}
