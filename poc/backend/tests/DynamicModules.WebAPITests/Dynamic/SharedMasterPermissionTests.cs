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
