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
/// 跨歸屬拖曳 409；孤兒區塊不能新增或拖入、孤兒欄位不能拖出；主檔模組的設計器只限系統管理員（§6 最後一點）。
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

    [Fact]
    public async Task TheMasterDesigner_NeedsASystemAdmin_OrdinaryModulesDoNot()
    {
        await OpenCrmAndStockAsync();
        _rig.Db(db => { db.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege
        {
            UserId = "planner01", ProgramCode = "DYN00000", CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
        }); });
        var designer = _rig.App.AsUser("planner01");
        var main = BlockOf("bp", "partner.main");
        var phone = _rig.FieldId("bp", "partner.phone");

        (await PostField(designer, main, "text", "官網")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostField(designer, BlockOf("crmx", "bp/partner.crm"), "text", "官網")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "貢獻欄位也一樣");
        (await designer.PostAsJsonAsync($"{Base}/modules/{Partner}/blocks", new { label = new Dictionary<string, string> { ["zh-TW"] = "財務" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await designer.PutAsJsonAsync($"{Base}/fields/{phone}/status", new { status = "Inactive" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PostOption(designer, _rig.FieldId("bp", "partner.role"), "外包商")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await designer.PutAsJsonAsync($"{Base}/blocks/{main}", new { label = new Dictionary<string, string> { ["zh-TW"] = "改名" }, collapsed = false }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 一般模組只要設計器權限
        var lead = await designer.PostAsJsonAsync($"{Base}/modules/crmx-lead/fields", new
        {
            blockId = BlockOf("crmx", "lead.main"), type = "text", label = new Dictionary<string, string> { ["zh-TW"] = "來源" }, config = new { }
        });
        lead.StatusCode.Should().Be(HttpStatusCode.Created, await lead.Content.ReadAsStringAsync());
        // 系統管理員照常可以改主檔
        (await PostField(_rig.Admin, main, "text", "官網")).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// 守衛涵蓋所有會改到主檔模組的設計器指令（§6 最後一點）：欄位、區塊、選項、規則、權限、模組刪除，
    /// 以及狀態機、分錄、列印範本、連動事件、跨記錄檢查、轉換（來源或目標是主檔都算）。只帶代碼的修改、刪除以既有定義的模組為準。
    /// </summary>
    [Fact]
    public async Task EveryDesignerCommandThatTouchesAMasterModule_NeedsASystemAdmin()
    {
        await OpenCrmAndStockAsync();
        _rig.Db(db => { db.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege
        {
            UserId = "planner01", ProgramCode = "DYN00000", CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
        }); });
        var designer = _rig.App.AsUser("planner01");
        var main = BlockOf("bp", "partner.main");
        var crm = BlockOf("crmx", "bp/partner.crm");
        var phone = _rig.FieldId("bp", "partner.phone");
        var role = _rig.FieldId("bp", "partner.role");
        var customer = _rig.Db(db => db.Set<FieldOption>().First(o => o.FieldId == role).Id);
        var partnerId = _rig.Db(db => db.Set<Module>().Single(m => m.Key == Partner).Id);
        _rig.Db(db =>
        {
            db.Set<ModuleEvent>().Add(new ModuleEvent { Key = "ev-partner", SourceModuleId = partnerId });
            db.Set<ModuleCheck>().Add(new ModuleCheck { Key = "chk-partner", ModuleId = partnerId });
        });
        static Dictionary<string, string> Name(string s) => new() { ["zh-TW"] = s };
        var ev = new
        {
            key = "ev-x", name = Name("x"), sourceModuleKey = Partner, active = true,
            definition = new { trigger = new { on = "create" }, effects = Array.Empty<object>() }
        };
        var check = new
        {
            key = "chk-x", name = Name("x"), moduleKey = Partner, active = true,
            definition = new
            {
                groupBy = Array.Empty<long>(), function = "count", assert = new { op = "gte", limit = new { @const = 0 } }, message = Name("x")
            }
        };
        object ConversionBody(string source, string target) => new
        {
            key = "cv-x", name = Name("x"), sourceModuleKey = source, targetModuleKey = target, active = true,
            definition = new { sourceStates = Array.Empty<long>(), header = new { sourceFieldId = 0, copy = Array.Empty<object>() } }
        };
        var print = new { key = "pt-x", name = Name("x"), body = "<p>x</p>", active = true };
        var noPrivileges = new { read = false, add = false, edit = false, delete = false, printExport = false, pricing = false, approve = false };

        var calls = new (string What, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("UpdateField", () => designer.PutAsJsonAsync($"{Base}/fields/{phone}", new { type = "phone", label = Name("電話"), config = new { }, blockId = main })),
            ("DeleteField", () => designer.DeleteAsync($"{Base}/fields/{phone}?purged=0")),
            ("DeleteBlock", () => designer.DeleteAsync($"{Base}/blocks/{crm}?purged=0")),
            ("UpdateOption", () => designer.PutAsJsonAsync($"{Base}/options/{customer}", new { label = Name("客戶"), color = (string?)null, active = false })),
            ("SaveRules", () => designer.PutAsJsonAsync($"{Base}/modules/{Partner}/rules", new { rules = Array.Empty<object>() })),
            ("SaveModulePermissions", () => designer.PutAsJsonAsync($"{Base}/modules/{Partner}/permissions", new
            {
                groups = new[] { new { groupId = _rig.GroupId("sales"), privileges = noPrivileges, scope = "all" } },
                users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>()
            })),
            ("DeleteModule", () => designer.DeleteAsync($"{Base}/modules/{Partner}?purged=0")),
            ("ActivateModule", () => designer.PostAsync($"{Base}/modules/{Partner}/activate", null)),
            ("SaveWorkflow", () => designer.PutAsJsonAsync($"{Base}/modules/{Partner}/workflow", new
            {
                statusFieldId = role, initialOptionId = customer, states = new[] { new { optionId = customer, readOnly = true } },
                transitions = Array.Empty<object>()
            })),
            ("DeleteWorkflow", () => designer.DeleteAsync($"{Base}/modules/{Partner}/workflow")),
            ("SaveLedger", () => designer.PutAsJsonAsync($"{Base}/modules/{Partner}/ledger", new
            {
                reversalFieldId = phone, negate = Array.Empty<long>(), swap = Array.Empty<long[]>(), confirmFreeze = false
            })),
            ("DeleteLedger", () => designer.DeleteAsync($"{Base}/modules/{Partner}/ledger")),
            ("CreatePrintTemplate", () => designer.PostAsJsonAsync($"{Base}/modules/{Partner}/print-templates", print)),
            ("UpdatePrintTemplate", () => designer.PutAsJsonAsync($"{Base}/modules/{Partner}/print-templates/pt-x", print)),
            ("DeletePrintTemplate", () => designer.DeleteAsync($"{Base}/modules/{Partner}/print-templates/pt-x")),
            ("CreateEvent", () => designer.PostAsJsonAsync($"{Base}/events", ev)),
            // 帶的是一般模組，但既有的事件來源是主檔：以既有定義為準
            ("UpdateEvent", () => designer.PutAsJsonAsync($"{Base}/events/ev-partner", ev with { key = "ev-partner", sourceModuleKey = "crmx-lead" })),
            ("DeleteEvent", () => designer.DeleteAsync($"{Base}/events/ev-partner")),
            ("CreateCheck", () => designer.PostAsJsonAsync($"{Base}/checks", check)),
            ("UpdateCheck", () => designer.PutAsJsonAsync($"{Base}/checks/chk-partner", check with { key = "chk-partner", moduleKey = "crmx-lead" })),
            ("DeleteCheck", () => designer.DeleteAsync($"{Base}/checks/chk-partner")),
            ("CreateConversion（來源是主檔）", () => designer.PostAsJsonAsync($"{Base}/conversions", ConversionBody(Partner, "crmx-order"))),
            ("CreateConversion（目標是主檔）", () => designer.PostAsJsonAsync($"{Base}/conversions", ConversionBody("crmx-lead", Partner))),
            // CRM 的「潛在客戶轉往來對象」目標是主檔；帶兩個一般模組也一樣以既有定義為準
            ("UpdateConversion", () => designer.PutAsJsonAsync($"{Base}/conversions/crmx-lead-to-partner", ConversionBody("crmx-lead", "crmx-order"))),
            ("DeleteConversion", () => designer.DeleteAsync($"{Base}/conversions/crmx-lead-to-partner")),
        };
        foreach (var (what, send) in calls)
        {
            var res = await send();
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{what}：{await res.Content.ReadAsStringAsync()}");
        }
        _rig.Db(db => db.Set<Conversion>().Any(c => c.Key == "crmx-lead-to-partner")).Should().BeTrue();
        _rig.Db(db => db.Set<ModuleEvent>().Any(e => e.Key == "ev-partner")).Should().BeTrue();
        _rig.Db(db => db.Set<Field>().Any(f => f.Id == phone)).Should().BeTrue();

        // 一般模組只要設計器權限
        var ordinary = await designer.PostAsJsonAsync($"{Base}/modules/crmx-lead/print-templates", print);
        ordinary.StatusCode.Should().Be(HttpStatusCode.Created, await ordinary.Content.ReadAsStringAsync());
        // 系統管理員照常可以
        var admin = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Partner}/print-templates", print);
        admin.StatusCode.Should().Be(HttpStatusCode.Created, await admin.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostFieldWith(HttpClient c, long blockId, string type, string label, object config) =>
        c.PostAsJsonAsync($"{Base}/modules/{Partner}/fields",
            new { blockId, type, label = new Dictionary<string, string> { ["zh-TW"] = label }, config });

    /// <summary>
    /// 設計器加在貢獻者區塊、或修改屬於貢獻者的欄位，套用和套件驗證相同的貢獻欄位規則（§4.3、§15.2「照貢獻欄位處理」），不合就 409；
    /// 基本區塊不受這些限制。
    /// </summary>
    [Fact]
    public async Task AFieldInTheCrmBlock_FollowsTheContributionRules_Otherwise409()
    {
        await OpenCrmAndStockAsync();
        var crm = BlockOf("crmx", "bp/partner.crm");
        var stockScore = await _rig.AddDesignerFieldAsync(Partner, BlockOf("stk", "bp/partner.stock"), "integer", "庫存評分");
        async Task Conflict(string what, string type, object config, string expected)
        {
            var res = await PostFieldWith(_rig.Admin, crm, type, what, config);
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.Conflict, $"{what}：{body}");
            body.Should().Contain(expected, what);
        }

        await Conflict("附件", "attachment", new { }, "貢獻欄位不能是 attachment");
        await Conflict("複選", "multienum", new { }, "貢獻欄位不能是 multienum");
        await Conflict("必填", "text", new { required = true }, "不得必填");
        await Conflict("串聯刪除", "reference", new { refTarget = new { kind = "module", key = "crmx-lead" }, onDelete = "cascade" }, "cascade");
        await Conflict("明細", "reference", new { refTarget = new { kind = "module", key = "crmx-order" }, relation = "detail" }, "明細");
        await Conflict("指向自己模組但 restrict", "reference", new { refTarget = new { kind = "module", key = "crmx-lead" }, onDelete = "restrict" }, "setNull");
        await Conflict("指向別的應用程式", "reference", new { refTarget = new { kind = "module", key = "stk-ship" }, onDelete = "setNull" }, "只能指向");
        await Conflict("引用別人的貢獻", "formula", new { formula = $"{{{stockScore}}} * 2", resultType = "number" }, "公式引用了");
        _rig.Db(db => db.Set<Field>().Count(f => f.BlockId == crm)).Should().Be(6, "一個都沒加進去");

        // 合規的照常可以
        await _rig.AddDesignerFieldAsync(Partner, crm, "reference", "轉自潛在客戶",
            new { refTarget = new { kind = "module", key = "crmx-lead" }, onDelete = "setNull" });
        await _rig.AddDesignerFieldAsync(Partner, crm, "reference", "窗口", new { refTarget = new { kind = "module", key = "master-ct-contact" } });
        var headcount = _rig.FieldId("bp", "partner.headcount");
        var score = _rig.FieldId("crmx", "bp/partner.score");
        await _rig.AddDesignerFieldAsync(Partner, crm, "formula", "加權", new { formula = $"{{{headcount}}} + {{{score}}}", resultType = "number" });
        // 基本區塊不受貢獻欄位的限制
        await _rig.AddDesignerFieldAsync(Partner, BlockOf("bp", "partner.main"), "attachment", "合約");

        // 修改屬於 CRM 的欄位也一樣
        var memo = _rig.FieldId("crmx", "bp/partner.memo");
        var required = await _rig.Admin.PutAsJsonAsync($"{Base}/fields/{memo}", new
        {
            type = "text", label = new Dictionary<string, string> { ["zh-TW"] = "備忘" }, config = new { required = true }, blockId = crm
        });
        required.StatusCode.Should().Be(HttpStatusCode.Conflict, await required.Content.ReadAsStringAsync());
        var fromLead = _rig.FieldId("crmx", "bp/partner.fromLead");
        var restrict = await _rig.Admin.PutAsJsonAsync($"{Base}/fields/{fromLead}", new
        {
            type = "reference", label = new Dictionary<string, string> { ["zh-TW"] = "來源潛在客戶" },
            config = new { readOnly = true, onDelete = "restrict", refTarget = new { kind = "module", key = "crmx-lead" } }, blockId = crm
        });
        restrict.StatusCode.Should().Be(HttpStatusCode.Conflict, await restrict.Content.ReadAsStringAsync());
        _rig.Db(db => db.Set<Field>().Single(f => f.Id == memo).Config.Required).Should().BeFalse();
        var ok = await _rig.Admin.PutAsJsonAsync($"{Base}/fields/{memo}", new
        {
            type = "text", label = new Dictionary<string, string> { ["zh-TW"] = "備註" }, config = new { }, blockId = crm
        });
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>貢獻者已關閉（凍結）時，設計器不能在它的區塊新增欄位或選項（§7 生命週期鎖）；重開後照常。</summary>
    [Fact]
    public async Task WhileCrmIsClosed_ItsBlockTakesNoNewFieldOrOption()
    {
        await OpenCrmAndStockAsync();
        var crm = BlockOf("crmx", "bp/partner.crm");
        var industry = _rig.FieldId("crmx", "bp/partner.industry");
        await _rig.CloseOkAsync("crmx");

        var field = await PostField(_rig.Admin, crm, "text", "新欄位");
        field.StatusCode.Should().Be(HttpStatusCode.Conflict, await field.Content.ReadAsStringAsync());
        (await field.Content.ReadAsStringAsync()).Should().Contain("已關閉");
        var option = await PostOption(_rig.Admin, industry, "服務");
        option.StatusCode.Should().Be(HttpStatusCode.Conflict, await option.Content.ReadAsStringAsync());
        (await option.Content.ReadAsStringAsync()).Should().Contain("已關閉");
        // 基本區塊照常
        await AddFieldOk(BlockOf("bp", "partner.main"), "text", "官網");

        await _rig.ReopenOkAsync("crmx");
        await AddFieldOk(crm, "text", "新欄位");
        await AddOptionOk(industry, "服務");
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
