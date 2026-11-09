using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 共用主檔模組的權限（設計 §6）：來源記錄 → 重算，動作取聯集、範圍逐動作取最寬；關閉只收回寫入；管理員存檔只寫差異；
/// 貢獻欄位限定可見與手動授予。
/// </summary>
public class SharedMasterPermissionTests : IDisposable
{
    private const string Partner = "master-bp-partner";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>crmx 給業務「讀取／all」、stk 也映射到業務並給「讀取＋新增＋編輯／own」。</summary>
    private async Task OpenSalesTwiceAsync(params string[] order)
    {
        _rig.GroupMap["stk"] = ("clerk", "sales");
        await _rig.ImportAllAsync();
        foreach (var id in order) await _rig.OpenOkAsync(id);
    }

    private Dictionary<PermissionAction, DataScope> ActionScopes(string groupKey)
    {
        var group = _rig.GroupId(groupKey);
        var module = _rig.Db(db => db.Set<Module>().Single(m => m.Key == Partner).Id);
        return _rig.Db(db => db.Set<ModuleActionScope>().Where(s => s.ModuleId == module && s.GroupId == group).ToList()
            .ToDictionary(s => s.Action, s => s.Scope));
    }

    private List<(GrantSource, long?, long, PermissionAction, DataScope)> Grants() =>
        _rig.Db(db => db.Set<AppGrant>().ToList().Select(g => (g.SourceKind, g.AppId, g.GroupId, g.Action, g.Scope)).OrderBy(x => x.ToString()).ToList());

    private async Task<JsonNode> GetPermissionsAsync() =>
        JsonNode.Parse(await _rig.Admin.GetStringAsync($"{Base}/modules/{Partner}/permissions"))!;

    private async Task SavePermissionsAsync(JsonNode body)
    {
        var res = await _rig.Admin.PutAsync($"{Base}/modules/{Partner}/permissions",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private JsonNode GroupNode(JsonNode perms, string groupKey) =>
        perms["groups"]!.AsArray().Single(g => g!["groupId"]!.GetValue<long>() == _rig.GroupId(groupKey))!;

    private async Task<JsonElement> PartnerAsync(string name) =>
        await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object> { [_rig.FieldKey("bp", "partner.name")] = name });

    private static async Task<HttpResponseMessage> RenameAsync(HttpClient c, JsonElement record, string key, string name)
    {
        var id = record.GetProperty("id").GetInt64();
        var fresh = await JsonOf(await c.GetAsync($"{Base}/records/{id}"));
        return await c.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [key] = name }, rowVersion = fresh.GetProperty("rowVersion").GetString() });
    }

    public static TheoryData<string[]> Orders() => new()
    {
        new[] { "crmx", "stk", "mx" }, new[] { "crmx", "mx", "stk" }, new[] { "stk", "crmx", "mx" },
        new[] { "stk", "mx", "crmx" }, new[] { "mx", "crmx", "stk" }, new[] { "mx", "stk", "crmx" }
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task TwoAppsMappedToOneGroup_ReadAllAndEditOwn_NoEditAll_InEveryOrder(string[] order)
    {
        await OpenSalesTwiceAsync(order);

        ActionScopes("sales").Should().BeEquivalentTo(new Dictionary<PermissionAction, DataScope>
        {
            [PermissionAction.Read] = DataScope.All, [PermissionAction.PrintExport] = DataScope.All,
            [PermissionAction.Add] = DataScope.Own, [PermissionAction.Edit] = DataScope.Own
        });
        var module = _rig.Db(db => db.Set<Module>().Single(m => m.Key == Partner).Id);
        var sales = _rig.GroupId("sales");
        _rig.Db(db => db.Set<ModuleDataScope>().Single(s => s.ModuleId == module && s.GroupId == sales).Scope)
            .Should().Be(DataScope.All, "資料範圍列寫讀取動作的範圍");
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 審查必修 8：同一份套件的兩個群組都映射到業務。來源記錄先依實際群組合併（動作聯集、逐動作範圍取最寬、欄位限制取最嚴）再寫：
    /// 不撞唯一鍵，Hidden 也不會被後一筆 ReadOnly 放寬。
    /// </summary>
    [Fact]
    public async Task TwoGroupsOfOnePackageMappedToOneGroup_AreMergedBeforeWriting_TheStricterFieldAccessWins()
    {
        var crm = JsonNode.Parse(SharedMasterPackages.Crm)!;
        crm["groups"]!.AsArray().Add(new JsonObject { ["code"] = "sales2", ["name"] = new JsonObject { ["zh-TW"] = "業務二" } });
        var contribution = crm["contributions"]![0]!;
        contribution["permissions"]!.AsArray().Add(new JsonObject
        {
            ["group"] = "sales2", ["scope"] = "own",
            ["privileges"] = new JsonObject
            {
                ["read"] = true, ["add"] = true, ["edit"] = true, ["delete"] = false, ["printExport"] = false, ["pricing"] = false, ["approve"] = false
            }
        });
        contribution["fieldAccess"] = new JsonArray(
            new JsonObject { ["field"] = "bp/partner.memo", ["group"] = "sales", ["access"] = "hidden" },
            new JsonObject { ["field"] = "bp/partner.memo", ["group"] = "sales2", ["access"] = "readOnly" });
        foreach (var json in new[] { SharedMasterPackages.Bp, SharedMasterPackages.Ct, crm.ToJsonString() }) await _rig.ImportAsync(json);

        var sales = _rig.GroupId("sales");
        await _rig.OpenOkAsync("crmx", new
        {
            groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "map", groupId = sales }, ["sales2"] = new { mode = "map", groupId = sales } },
            masters = new Dictionary<string, object>
            {
                ["bp"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } },
                ["ct"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } }
            }
        });

        ActionScopes("sales").Should().BeEquivalentTo(new Dictionary<PermissionAction, DataScope>
        {
            [PermissionAction.Read] = DataScope.All, [PermissionAction.PrintExport] = DataScope.All,
            [PermissionAction.Add] = DataScope.Own, [PermissionAction.Edit] = DataScope.Own
        });
        var memo = _rig.FieldId("crmx", "bp/partner.memo");
        _rig.Db(db => db.Set<FieldPermission>().Where(p => p.FieldId == memo && p.GroupId == sales).Select(p => p.Access).ToList())
            .Should().Equal(FieldAccess.Hidden);
        var record = await PartnerAsync("甲公司");
        (await VisibleKeysAsync(_rig.App.AsUser("sales01"), record)).Should().NotContain($"f_{memo}", "Hidden 不被 ReadOnly 放寬");
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task MergedScopes_ApplyPerAction_ReadAll_EditOnlyOwn()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        var name = _rig.FieldKey("bp", "partner.name");
        var admins = await PartnerAsync("甲公司");
        var sales01 = _rig.App.AsUser("sales01");

        (await sales01.GetAsync($"{Base}/records/{admins.GetProperty("id").GetInt64()}")).StatusCode.Should().Be(HttpStatusCode.OK, "讀取 all");
        var paged = await JsonOf(await sales01.GetAsync($"{Base}/modules/{Partner}/records/paged"));
        paged.GetProperty("totalCount").GetInt32().Should().Be(1);
        (await RenameAsync(sales01, admins, name, "改名")).IsSuccessStatusCode.Should().BeFalse("編輯只有 own，別人的記錄不能改");

        var own = await _rig.CreateRecordAsync(sales01, Partner, new Dictionary<string, object> { [name] = "乙公司" });
        (await RenameAsync(sales01, own, name, "乙公司二")).StatusCode.Should().Be(HttpStatusCode.OK, "自己的記錄可以改");
    }

    private async Task SetActionScopeAsync(string groupKey, string action, string scope)
    {
        var perms = await GetPermissionsAsync();
        GroupNode(perms, groupKey)["actionScopes"]!.AsArray().Single(a => a!["action"]!.GetValue<string>() == action)!["scope"] = scope;
        await SavePermissionsAsync(perms);
    }

    /// <summary>
    /// 審查必修 2：「讀取／all、列印匯出／own」時，單筆列印、報表匯出只含自己的記錄（兩種範圍都要符合）；
    /// 反過來「讀取／own、列印匯出／all」時，記錄匯出也不能超出讀取的範圍。
    /// </summary>
    [Fact]
    public async Task PrintAndExports_StayWithinBothTheReadAndThePrintExportScope()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        await SetActionScopeAsync("sales", "printExport", "own");
        ActionScopes("sales")[PermissionAction.PrintExport].Should().Be(DataScope.Own);
        var sales01 = _rig.App.AsUser("sales01");
        var admins = await PartnerAsync("甲公司");
        var own = await _rig.CreateRecordAsync(sales01, Partner, new Dictionary<string, object> { [_rig.FieldKey("bp", "partner.name")] = "乙公司" });

        var template = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Partner}/print-templates",
            new { key = "card", name = new Dictionary<string, string> { ["zh-TW"] = "名片" }, body = "<p>{{_title}}</p>", active = true });
        template.StatusCode.Should().Be(HttpStatusCode.Created, await template.Content.ReadAsStringAsync());
        (await sales01.GetAsync($"{Base}/records/{admins.GetProperty("id").GetInt64()}/print/card")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "列印匯出只有 own");
        (await sales01.GetAsync($"{Base}/records/{own.GetProperty("id").GetInt64()}/print/card")).StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await _rig.Admin.PostAsJsonAsync($"{Base}/reports", new
        {
            name = "筆數",
            definition = new Dictionary<string, object?>
            {
                ["version"] = 1, ["mainModuleKey"] = Partner, ["child"] = null, ["columns"] = Array.Empty<object>(),
                ["groups"] = Array.Empty<object>(),
                ["measures"] = new object[] { new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null } },
                ["filters"] = Array.Empty<object>(), ["primaryDateField"] = null, ["dateRange"] = null,
                ["sort"] = Array.Empty<object>(), ["viz"] = "table"
            }
        });
        report.StatusCode.Should().Be(HttpStatusCode.Created, await report.Content.ReadAsStringAsync());
        var reportId = (await JsonOf(report)).GetProperty("id").GetInt64();
        (await _rig.Admin.PutAsJsonAsync($"{Base}/reports/{reportId}/shares", new { groupIds = new[] { _rig.GroupId("sales") } })).EnsureSuccessStatusCode();
        var run = await JsonOf(await sales01.PostAsJsonAsync($"{Base}/reports/{reportId}/run", new { }));
        run.GetProperty("total").GetProperty("n").GetDecimal().Should().Be(2, "畫面照讀取 all");
        var exported = await sales01.GetAsync($"{Base}/reports/{reportId}/export");
        exported.StatusCode.Should().Be(HttpStatusCode.OK, await exported.Content.ReadAsStringAsync());
        using (var wb = new ClosedXML.Excel.XLWorkbook(await exported.Content.ReadAsStreamAsync()))
            wb.Worksheet(1).Cell(2, 1).GetDouble().Should().Be(1, "匯出照列印匯出 own");

        await SetActionScopeAsync("sales", "printExport", "all");
        await SetActionScopeAsync("sales", "read", "own");
        var csv = await sales01.GetStringAsync($"{Base}/modules/{Partner}/export?format=csv");
        csv.Should().Contain("乙公司").And.NotContain("甲公司", "讀取只有 own，匯出不能超出");
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 審查必修 3：「讀取／all、價格／own」時，別人記錄上的金額（與金額公式）不能因為有價格動作就整個模組解開。
    /// 金額遮罩是整個模組一起判斷、沒有逐筆遮罩，所以價格範圍比這次動作的範圍窄時一律遮（fail closed），自己的記錄在讀取時也遮；
    /// 價格範圍放寬到 all 才看得到。
    /// </summary>
    [Fact]
    public async Task APricingScopeNarrowerThanRead_DoesNotUnmaskAmountsOnOtherPeoplesRecords()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        var price = await AddField(_rig.Admin, Partner, "currency");
        var doubled = await AddField(_rig.Admin, Partner, "formula", new { formula = $"{{{price.Id}}} * 2", resultType = "currency" });
        var perms = await GetPermissionsAsync();
        var sales = GroupNode(perms, "sales");
        sales["privileges"]!["pricing"] = true;
        sales["actionScopes"]!.AsArray().Add(new JsonObject { ["action"] = "pricing", ["scope"] = "own" });
        await SavePermissionsAsync(perms);
        ActionScopes("sales")[PermissionAction.Pricing].Should().Be(DataScope.Own);

        var sales01 = _rig.App.AsUser("sales01");
        var name = _rig.FieldKey("bp", "partner.name");
        var admins = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object> { [name] = "甲公司", [price.Key] = 100 });
        var own = await _rig.CreateRecordAsync(sales01, Partner, new Dictionary<string, object> { [name] = "乙公司" });

        (await VisibleKeysAsync(sales01, admins)).Should().Contain(name).And.NotContain([price.Key, doubled.Key], "別人的金額不能看");
        (await VisibleKeysAsync(sales01, own)).Should().NotContain(price.Key, "沒有逐筆遮罩，讀取範圍比價格寬就整個遮");
        var paged = await sales01.GetStringAsync($"{Base}/modules/{Partner}/records/paged");
        paged.Should().Contain("甲公司").And.NotContain(price.Key);

        await SetActionScopeAsync("sales", "pricing", "all");
        (await VisibleKeysAsync(sales01, admins)).Should().Contain([price.Key, doubled.Key], "價格範圍和讀取一樣寬才解開");
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 第三輪 1：主檔模組上個人 Pricing 沒有逐動作範圍，改套用該使用者的讀取範圍；schema 回報的金額權限和實際遮罩一致。
    /// 完全沒有 Pricing 的對照組看不到，schema 也回 false。
    /// </summary>
    [Fact]
    public async Task APersonalPricingGrant_OnAMasterModule_UsesTheReadScope_AndTheSchemaAgrees()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        var price = await AddField(_rig.Admin, Partner, "currency");
        var admins = await _rig.CreateRecordAsync(_rig.Admin, Partner, new Dictionary<string, object>
            { [_rig.FieldKey("bp", "partner.name")] = "甲公司", [price.Key] = 100 });
        var sales01 = _rig.App.AsUser("sales01");

        async Task<bool> SchemaPricingAsync() =>
            (await JsonOf(await sales01.GetAsync($"{Base}/modules/{Partner}/schema"))).GetProperty("permissions").GetProperty("pricing").GetBoolean();

        (await VisibleKeysAsync(sales01, admins)).Should().NotContain(price.Key, "沒有 Pricing");
        (await SchemaPricingAsync()).Should().BeFalse();

        var code = _rig.Db(db => db.Set<Module>().Single(m => m.Key == Partner).ProgramCode);
        _rig.Db(db => { db.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege { UserId = "sales01", ProgramCode = code, CanAmt = true }); });

        (await VisibleKeysAsync(sales01, admins)).Should().Contain(price.Key, "個人 Pricing 套用群組的讀取 all");
        (await SchemaPricingAsync()).Should().BeTrue("schema 回報和實際一致");
    }

    [Fact]
    public async Task GetPermissions_OnAMasterModule_ReturnsActionScopes_ButNotOnOrdinaryModules()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        var perms = await GetPermissionsAsync();
        perms["sharedMaster"]!.GetValue<bool>().Should().BeTrue();
        GroupNode(perms, "sales")["actionScopes"]!.AsArray().Select(a => $"{a!["action"]}:{a["scope"]}")
            .Should().BeEquivalentTo("read:all", "add:own", "edit:own", "printExport:all");

        var ordinary = JsonNode.Parse(await _rig.Admin.GetStringAsync($"{Base}/modules/crmx-lead/permissions"))!;
        ordinary["sharedMaster"]!.GetValue<bool>().Should().BeFalse();
        GroupNode(ordinary, "sales")["actionScopes"].Should().BeNull();
    }

    [Fact]
    public async Task ClosingOneApp_RevokesOnlyItsWrites_ReopenRestores()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        await _rig.CloseOkAsync("stk");
        ActionScopes("sales").Should().BeEquivalentTo(new Dictionary<PermissionAction, DataScope>
            { [PermissionAction.Read] = DataScope.All, [PermissionAction.PrintExport] = DataScope.All });
        await _rig.AssertIntegrityAsync();

        await _rig.ReopenOkAsync("stk");
        ActionScopes("sales").Keys.Should().Contain([PermissionAction.Add, PermissionAction.Edit]);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task SavingUnchanged_ChangesNoSourceAndNoEffectiveValue()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        var before = Grants();
        var scopes = ActionScopes("sales");

        await SavePermissionsAsync(await GetPermissionsAsync());

        Grants().Should().Equal(before, "原封不動存檔不產生手動記錄");
        ActionScopes("sales").Should().BeEquivalentTo(scopes);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AdminRemovesAnAction_ItDoesNotGrowBackAfterRecompute()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        var perms = await GetPermissionsAsync();
        GroupNode(perms, "sales")["privileges"]!["edit"] = false;
        await SavePermissionsAsync(perms);
        ActionScopes("sales").Should().NotContainKey(PermissionAction.Edit);

        await _rig.CloseOkAsync("stk");
        await _rig.ReopenOkAsync("stk");
        ActionScopes("sales").Should().NotContainKey(PermissionAction.Edit, "管理員拿掉的動作，重算後不會長回來");
        ActionScopes("sales").Should().ContainKey(PermissionAction.Add);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AdminSavesWhileTheWriterIsClosed_TheWriteComesBackOnReopen()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        await _rig.CloseOkAsync("stk");
        // 關閉中的 stk 授予的編輯不在畫面上；管理員只改一個欄位的存取再存檔
        var perms = await GetPermissionsAsync();
        perms["fields"]!.AsArray().Add(new JsonObject
        {
            ["fieldId"] = _rig.FieldId("bp", "partner.phone"), ["groupId"] = _rig.GroupId("sales"), ["access"] = "readOnly"
        });
        await SavePermissionsAsync(perms);
        _rig.Db(db => db.Set<AppGrant>().Count(g => g.SourceKind == GrantSource.Manual)).Should().Be(0);

        await _rig.ReopenOkAsync("stk");
        ActionScopes("sales")[PermissionAction.Edit].Should().Be(DataScope.Own);
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AdminWidensAScope_ItIsRecordedAsManual()
    {
        await OpenSalesTwiceAsync("crmx", "stk");
        var perms = await GetPermissionsAsync();
        var edit = GroupNode(perms, "sales")["actionScopes"]!.AsArray().Single(a => a!["action"]!.GetValue<string>() == "edit")!;
        edit["scope"] = "all";
        await SavePermissionsAsync(perms);

        ActionScopes("sales")[PermissionAction.Edit].Should().Be(DataScope.All);
        ActionScopes("sales")[PermissionAction.Add].Should().Be(DataScope.Own, "只改了編輯");
        _rig.Db(db => db.Set<AppGrant>().Where(g => g.SourceKind == GrantSource.Manual).Select(g => g.Action).ToList())
            .Should().Equal(PermissionAction.Edit);
        await _rig.AssertIntegrityAsync();
    }

    // ---- 貢獻欄位限定可見 ----

    private async Task<HashSet<string>> VisibleKeysAsync(HttpClient c, JsonElement record) =>
        (await JsonOf(await c.GetAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}")))
        .GetProperty("values").EnumerateObject().Select(p => p.Name).ToHashSet();

    [Fact]
    public async Task ContributedFields_VisibleOnlyToTheContributorsGroupsAndAdmin_UntilGranted_HiddenStillWins()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        var industry = _rig.FieldKey("crmx", "bp/partner.industry");
        var shipAddress = _rig.FieldKey("stk", "bp/partner.shipAddress");
        var name = _rig.FieldKey("bp", "partner.name");
        // 倉管只有讀取 own：記錄由倉管建立，分數由系統管理員補上
        var record = await _rig.CreateRecordAsync(_rig.App.AsUser("keeper01"), Partner, new Dictionary<string, object>
        {
            [name] = "甲公司", [shipAddress] = "某路一號"
        });
        var fresh = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}"));
        (await _rig.Admin.PutAsJsonAsync($"{Base}/records/{record.GetProperty("id").GetInt64()}", new
        {
            values = new Dictionary<string, object> { [_rig.FieldKey("crmx", "bp/partner.score")] = 3 },
            rowVersion = fresh.GetProperty("rowVersion").GetString()
        })).EnsureSuccessStatusCode();

        (await VisibleKeysAsync(_rig.Admin, record)).Should().Contain([shipAddress, _rig.FieldKey("crmx", "bp/partner.score")], "系統管理員看得到");
        var sales = await VisibleKeysAsync(_rig.App.AsUser("sales01"), record);
        sales.Should().Contain([name, _rig.FieldKey("crmx", "bp/partner.score")]).And.NotContain(shipAddress, "進銷存貢獻的欄位不給業務看");
        var keeper = await VisibleKeysAsync(_rig.App.AsUser("keeper01"), record);
        keeper.Should().Contain([name, shipAddress]).And.NotContain(_rig.FieldKey("crmx", "bp/partner.score"));

        // 管理員把 CRM 的分數授予倉管 → 看得到；倉管對它設 Hidden → 仍看不到（收緊優先）
        var score = _rig.FieldId("crmx", "bp/partner.score");
        var perms = await GetPermissionsAsync();
        perms["fieldGrants"]!.AsArray().Single(f => f!["fieldId"]!.GetValue<long>() == score)!["groups"]!.AsArray()
            .Select(g => $"{g!["groupId"]}:{g["source"]}:{g["appKey"]}").Should().Equal($"{_rig.GroupId("sales")}:app:crmx");
        perms["fieldGrants"] = new JsonArray(new JsonObject { ["fieldId"] = score, ["groupIds"] = new JsonArray(_rig.GroupId("warehouse")) });
        await SavePermissionsAsync(perms);
        (await VisibleKeysAsync(_rig.App.AsUser("keeper01"), record)).Should().Contain(_rig.FieldKey("crmx", "bp/partner.score"));
        _rig.Db(db => db.Set<FieldGrant>().Count(g => g.FieldId == score && g.SourceKind == GrantSource.Manual)).Should().Be(1);

        perms = await GetPermissionsAsync();
        perms["fieldGrants"] = new JsonArray(new JsonObject { ["fieldId"] = score, ["groupIds"] = new JsonArray(_rig.GroupId("warehouse")) });
        perms["fields"]!.AsArray().Add(new JsonObject { ["fieldId"] = score, ["groupId"] = _rig.GroupId("warehouse"), ["access"] = "hidden" });
        await SavePermissionsAsync(perms);
        (await VisibleKeysAsync(_rig.App.AsUser("keeper01"), record)).Should().NotContain(_rig.FieldKey("crmx", "bp/partner.score"));
        await _rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task AGroupCreatedLater_WithReadOnTheMaster_SeesOnlyBaseFields()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        var record = await PartnerAsync("甲公司");
        var created = await JsonOf(await _rig.Admin.PostAsJsonAsync($"{Base}/admin/groups", new { name = "後建群組" }));
        var groupId = created.GetProperty("id").GetInt64();
        (await _rig.Admin.PutAsJsonAsync($"{Base}/admin/groups/{groupId}/members", new { userIds = new[] { "qa01" } })).EnsureSuccessStatusCode();

        var perms = await GetPermissionsAsync();
        var node = perms["groups"]!.AsArray().Single(g => g!["groupId"]!.GetValue<long>() == groupId)!;
        node["privileges"]!["read"] = true;
        node["scope"] = "all";
        await SavePermissionsAsync(perms);

        var keys = await VisibleKeysAsync(_rig.App.AsUser("qa01"), record);
        keys.Should().Contain(_rig.FieldKey("bp", "partner.name")).And.NotContain(_rig.FieldKey("crmx", "bp/partner.industry"));
        await _rig.AssertIntegrityAsync();

        // 群組刪除時，它的來源記錄與可見列一起消失
        perms = await GetPermissionsAsync();
        perms["fieldGrants"] = new JsonArray(new JsonObject { ["fieldId"] = _rig.FieldId("crmx", "bp/partner.industry"), ["groupIds"] = new JsonArray(groupId) });
        await SavePermissionsAsync(perms);
        (await _rig.Admin.DeleteAsync($"{Base}/admin/groups/{groupId}")).EnsureSuccessStatusCode();
        _rig.Db(db => db.Set<AppGrant>().Count(g => g.GroupId == groupId) + db.Set<FieldGrant>().Count(g => g.GroupId == groupId)
                      + db.Set<ModuleActionScope>().Count(g => g.GroupId == groupId)).Should().Be(0);
        await _rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 審查必修 4：主檔上的事件把 CRM 限定可見的備忘（或引用限定可見欄位的公式）複製到別的模組，等於把它給看不到的群組；
    /// 跟隱藏欄位一樣，沒有在對應上明確 allowHiddenSource 就不能存。
    /// </summary>
    [Fact]
    public async Task AnEventCopyingARestrictedField_OrAFormulaOverIt_NeedsAllowHiddenSource()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        await _rig.OpenOkAsync("stk");
        var derived = await AddField(_rig.Admin, Partner, "formula",
            new { formula = $"{{{_rig.FieldId("crmx", "bp/partner.score")}}} + 1", resultType = "number" });
        var shipQty = await AddField(_rig.Admin, "stk-ship", "decimal");
        var shipNote = await AddField(_rig.Admin, "stk-ship", "text");

        Task<HttpResponseMessage> SaveAsync(long from, long to, bool allow) => _rig.Admin.PostAsJsonAsync($"{Base}/events", new
        {
            key = "ev-" + Guid.NewGuid().ToString("N")[..8], name = new Dictionary<string, string> { ["zh-TW"] = "抄到出貨單" },
            sourceModuleKey = Partner, active = true,
            definition = new
            {
                trigger = new { on = "create" },
                effects = new[]
                {
                    new
                    {
                        code = "mk", kind = "create", targetModuleKey = "stk-ship",
                        set = new object[]
                        {
                            new { to = _rig.FieldId("stk", "ship.no"), @const = "SH" },
                            new { to, from = new { side = "header", fieldId = from }, allowHiddenSource = allow }
                        }
                    }
                }
            }
        });

        var memo = _rig.FieldId("crmx", "bp/partner.memo");
        foreach (var (from, to) in new[] { (memo, shipNote.Id), (derived.Id, shipQty.Id) })
        {
            var rejected = await SaveAsync(from, to, allow: false);
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"來源 {from}：{await rejected.Content.ReadAsStringAsync()}");
            (await rejected.Content.ReadAsStringAsync()).Should().Contain("allowHiddenSource");
            var allowed = await SaveAsync(from, to, allow: true);
            allowed.StatusCode.Should().Be(HttpStatusCode.Created, await allowed.Content.ReadAsStringAsync());
        }
        (await SaveAsync(_rig.FieldId("bp", "partner.name"), shipNote.Id, allow: false)).StatusCode.Should().Be(HttpStatusCode.Created, "基本欄位照常");
    }

    [Fact]
    public async Task FieldGrants_OnlyForRestrictedFields()
    {
        await _rig.ImportAllAsync();
        await _rig.OpenOkAsync("crmx");
        var perms = await GetPermissionsAsync();
        perms["fieldGrants"] = new JsonArray(new JsonObject
            { ["fieldId"] = _rig.FieldId("bp", "partner.name"), ["groupIds"] = new JsonArray(_rig.GroupId("warehouse")) });
        var res = await _rig.Admin.PutAsync($"{Base}/modules/{Partner}/permissions",
            new StringContent(perms.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("限定可見");
    }
}
