using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>
/// 權限軸驗收：系統管理員與 fail closed（PAX-04、05、07）、自我提權嘗試（PAX-08–10）、跨模組與共用主檔（PAX-15–17）。
/// 權限軸設計 §18；每個測試一個新的資料庫（會改權限列）。
/// </summary>
public sealed class PermissionAxesAccessTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private PaxWorld _w = null!;

    public async Task InitializeAsync() => _w = await CreateAsync(_app);

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> GetPerms(string user, string module) => _w.As(user).GetAsync($"{Base}/modules/{module}/permissions");

    /// <summary>讀出目前的權限設定（admin），原樣當 PUT body：不改任何格子。</summary>
    private async Task<JsonObject> CurrentPerms(string module)
    {
        var res = await GetPerms(Admin, module);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
    }

    private Task<HttpResponseMessage> PutPerms(string user, string module, JsonNode body) =>
        _w.As(user).PutAsync($"{Base}/modules/{module}/permissions",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

    private static object SystemBody(bool read, bool add, bool edit, bool delete, bool permissionAdmin) =>
        new { designer = new { read, add, edit, delete }, permissionAdmin };

    private int AuditCount(string action) => _w.Db(db => db.Set<AuditLogEntry>().Count(a => a.Action == action));

    private int PrivilegeIncidents() =>
        _w.Db(db => db.Set<OpsIncident>().Count(i => i.Code == SecurityIncidents.PrivilegeChangedCode));

    private static void Designer(JsonElement me, bool read, bool add, bool edit, bool delete, bool permissionAdmin)
    {
        var d = me.GetProperty("designer");
        (d.GetProperty("read").GetBoolean(), d.GetProperty("add").GetBoolean(), d.GetProperty("edit").GetBoolean(), d.GetProperty("delete").GetBoolean())
            .Should().Be((read, add, edit, delete));
        me.GetProperty("permissionAdmin").GetBoolean().Should().Be(permissionAdmin);
    }

    [Fact]
    public async Task PAX04_Admin_WithoutAnySystemRows_HasAllDesignerActionsAndPermissionAdmin()
    {
        _w.Db(db => db.Set<UserProgramPrivilege>().Count(p => p.UserId == Admin)).Should().Be(0, "前提：admin 沒有任何個人權限列");

        Designer(await JsonOf(await _w.As(Admin).GetAsync($"{Base}/me")), true, true, true, true, true);
        await PaxAssert.Status(await PutPerms(Admin, A, await CurrentPerms(A)), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("zh-TW", "修改權限需要「權限設定」權限，請洽系統管理員")]
    [InlineData("en-US", "Changing permissions requires the \"Permission settings\" right. Please contact your system administrator")]
    public async Task PAX05_DesignerWithoutPermissionAdmin_ReadsButSaveIs403(string locale, string message)
    {
        await PaxAssert.Status(await GetPerms(Zhang, A), HttpStatusCode.OK);
        var body = await CurrentPerms(A);
        var before = _w.GuardRows();

        var res = await _w.As(Zhang).SendAsync(PaxAssert.Req(HttpMethod.Put, $"{Base}/modules/{A}/permissions",
            JsonSerializer.Deserialize<JsonElement>(body.ToJsonString()), locale));

        (await PaxAssert.Forbidden(res, "permissionAdminRequired")).GetProperty("message").GetString().Should().Be(message);
        _w.GuardRows().Should().Equal(before);
    }

    [Fact]
    public async Task PAX07_PermissionAdminOnly_ImpliedDesignerRead_SavesPermissions_ButCannotEditDesign()
    {
        var li = _w.As(Li);
        await PaxAssert.Status(await li.GetAsync($"{Base}/modules"), HttpStatusCode.OK);
        await PaxAssert.Status(await li.GetAsync($"{Base}/modules/{A}"), HttpStatusCode.OK);
        await PaxAssert.Status(await GetPerms(Li, A), HttpStatusCode.OK);
        await PaxAssert.Status(await PutPerms(Li, A, await CurrentPerms(A)), HttpStatusCode.OK);

        var blockId = (await JsonOf(await li.GetAsync($"{Base}/modules/{A}"))).GetProperty("blocks")[0].GetProperty("id").GetInt64();
        await PaxAssert.Forbidden(await li.PostAsJsonAsync($"{Base}/modules/{A}/fields", new
        {
            blockId, type = "text", label = new Dictionary<string, string> { ["zh-TW"] = "新欄位" }, config = new { },
        }), null);

        Designer(await JsonOf(await li.GetAsync($"{Base}/me")), true, false, false, false, true);
    }

    [Fact]
    public async Task PAX08_DesignerGrantsHimselfOnUnreadableModule_403_AndStillCannotReadIt()
    {
        var before = _w.GuardRows();
        var body = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            groups = new[] { Grp(_w.GDesign, Priv(read: true, pricing: true)) },
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>(),
        }))!;

        await PaxAssert.Forbidden(await PutPerms(Wang, M, body), "permissionAdminRequired");

        (await _w.As(Wang).GetAsync($"{Base}/modules/{M}/schema")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _w.GuardRows().Should().Equal(before);
    }

    [Fact]
    public async Task PAX09_NonAdmins_GrantSystemPrivileges_403WithoutCode_NothingWritten()
    {
        var before = _w.GuardRows();
        var incidents = PrivilegeIncidents();
        var body = SystemBody(true, true, true, true, permissionAdmin: true);

        foreach (var user in new[] { Wang, Li })
            await PaxAssert.Forbidden(await _w.As(user).PutAsJsonAsync($"{Base}/admin/groups/{_w.GDesign}/system-privileges", body), null);

        _w.GuardRows().Should().Equal(before);
        AuditCount("group.systemPrivileges").Should().Be(0);
        PrivilegeIncidents().Should().Be(incidents);
    }

    [Fact]
    public async Task PAX10_NonAdmins_AddThemselvesToPermissionGroup_403WithoutCode()
    {
        var before = _w.Db(db => db.Set<UserGroupMapping>().Where(m => m.GroupId == _w.GPerm).Select(m => m.UserId).OrderBy(u => u).ToList());

        foreach (var user in new[] { Wang, Li })
            await PaxAssert.Forbidden(await _w.As(user).PutAsJsonAsync($"{Base}/admin/groups/{_w.GPerm}/members",
                new { userIds = before.Append(user).Distinct().ToArray() }), null);

        _w.Db(db => db.Set<UserGroupMapping>().Where(m => m.GroupId == _w.GPerm).Select(m => m.UserId).OrderBy(u => u).ToList()).Should().Equal(before);
    }

    /// <summary>只要求共用主檔往來對象的最小使用者套件：開啟後內建主檔 master-partner 存在（S）。</summary>
    private const string PartnerUser = """
        {
          "format": "formwright-app", "formatVersion": 6,
          "manifest": { "id": "pax-partner-user", "version": "1.0.0", "name": { "zh-TW": "主檔使用者（測試）" },
            "requires": { "masters": [ { "id": "partner", "modules": ["partner"] } ] } },
          "groups": [],
          "modules": [
            { "code": "doc", "key": "pax-partner-doc", "name": { "zh-TW": "單據" }, "kind": "standalone", "titleField": "no",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "no", "type": "text", "label": { "zh-TW": "單號" }, "config": { "required": true } },
                { "code": "partner", "type": "reference", "label": { "zh-TW": "對象" },
                  "config": { "onDelete": "restrict", "refTarget": { "kind": "module", "key": "partner", "app": "partner" } } }
              ] } ], "permissions": [] }
          ]
        }
        """;

    [Fact]
    public async Task PAX15_PermissionAdmin_SavesAnyModule_ButNotSharedMaster()
    {
        var rig = new SharedMasterRig(_app);
        await rig.ImportAsync(PartnerUser);
        await rig.OpenOkAsync("pax-partner-user", new
        {
            groupMap = new Dictionary<string, object>(),
            masters = new Dictionary<string, object> { ["partner"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } } },
        });
        await PaxAssert.Status(await PutPerms(Li, A, await CurrentPerms(A)), HttpStatusCode.OK);
        await PaxAssert.Status(await PutPerms(Li, M, await CurrentPerms(M)), HttpStatusCode.OK);

        var master = await CurrentPerms(S);
        var before = _w.GuardRows();
        await PaxAssert.Forbidden(await PutPerms(Li, S, master), null);
        _w.GuardRows().Should().Equal(before);

        await PaxAssert.Status(await PutPerms(Admin, S, master), HttpStatusCode.OK);
    }

    [Fact]
    public async Task PAX16_DetailModuleFieldException_PermissionAdmin200_DesignerOnly403()
    {
        var body = await CurrentPerms(D);
        body["groups"] = new JsonArray(); // 明細模組的群組權限沿用表頭，只存欄位例外
        body["fields"] = new JsonArray(new JsonObject { ["fieldId"] = _w.DX, ["groupId"] = _w.GDesign, ["access"] = "hidden" });

        await PaxAssert.Status(await PutPerms(Li, D, body), HttpStatusCode.OK);
        _w.Db(db => db.Set<FieldPermission>().Any(p => p.FieldId == _w.DX && p.GroupId == _w.GDesign && p.Access == FieldAccess.Hidden))
            .Should().BeTrue();

        body["fields"] = new JsonArray(new JsonObject { ["fieldId"] = _w.DY, ["groupId"] = _w.GDesign, ["access"] = "hidden" });
        var before = _w.GuardRows();
        await PaxAssert.Forbidden(await PutPerms(Zhang, D, body), "permissionAdminRequired");
        _w.GuardRows().Should().Equal(before);
    }

    [Fact]
    public async Task PAX17_PermissionAdminOpensOwnGroupRead_AuditedAsHim_NoSecurityIncident()
    {
        var incidents = PrivilegeIncidents();
        var body = await CurrentPerms(A);
        var mine = body["groups"]!.AsArray().Single(g => g!["groupId"]!.GetValue<long>() == _w.GPerm)!;
        mine["privileges"]!["read"] = true;

        await PaxAssert.Status(await PutPerms(Li, A, body), HttpStatusCode.OK);

        var audit = _w.Db(db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == "permission.update")
            .OrderByDescending(a => a.Id).First());
        audit.ActorUserId.Should().Be(Li);
        var cells = JsonDocument.Parse(audit.SummaryJson!).RootElement.GetProperty("changes").EnumerateArray()
            .Select(c => c.GetProperty("key").GetString()).ToList();
        cells.Should().Contain($"group:{_w.GPerm}.read");
        PrivilegeIncidents().Should().Be(incidents, "一般的模組權限存檔只記稽核、不發事件");
    }
}
