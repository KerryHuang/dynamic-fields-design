using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>
/// 授予與安全事件（權限軸設計 §8、§9；PAX-18–21b）：系統權限 API、實際持有者變化觸發 privilege.changed、原值存檔、交易失敗全回滾。
/// 雙跑：快照與持有者查詢走 repository 的新查詢。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class SystemPrivilegesApiTests : IAsyncLifetime
{
    private TestApp _app = new();
    private PaxWorld _w = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync() => await Init();

    private async Task Init()
    {
        _w = await CreateAsync(_app, modules: false);
        _admin = _app.AsUser();
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private const string Password = "pax-Passw0rd!";

    private static object Body(bool read, bool add, bool edit, bool delete, bool permissionAdmin) =>
        new { designer = new { read, add, edit, delete }, permissionAdmin };

    private static readonly object Grant = Body(true, false, true, false, true);
    private static readonly object None = Body(false, false, false, false, false);

    private void Users(params string[] ids) =>
        _w.Write(db => db.Set<AppUser>().AddRange(ids.Select(id => new AppUser { UserId = id, DisplayName = id })));

    /// <summary>建群組（直接寫庫），可帶成員與系統權限列。</summary>
    private long NewGroup(string key, string[]? members = null, (bool R, bool A, bool E, bool D)? designer = null, bool permissionAdmin = false)
    {
        var g = Group(key, key);
        _w.Write(db => db.Set<PermissionGroup>().Add(g));
        _w.Write(db =>
        {
            foreach (var u in members ?? []) db.Set<UserGroupMapping>().Add(new UserGroupMapping { GroupId = g.Id, UserId = u });
            if (designer is { } d)
                db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
                    { GroupId = g.Id, ProgramCode = SystemProgramCodes.Designer, CanRead = d.R, CanAdd = d.A, CanEdit = d.E, CanDelete = d.D });
            if (permissionAdmin)
                db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege { GroupId = g.Id, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true });
        });
        return g.Id;
    }

    private List<string> SystemRows(long groupId) => _w.Db(db => db.Set<GroupProgramPrivilege>().AsNoTracking()
        .Where(p => p.GroupId == groupId).ToList()
        .Select(p => $"{p.ProgramCode}:{p.CanRead}{p.CanAdd}{p.CanEdit}{p.CanDelete}{p.CanPrint}{p.CanAmt}{p.CanApprove}").Order().ToList());

    private OpsIncident? Incident() => _w.Db(db => db.Set<OpsIncident>().AsNoTracking()
        .SingleOrDefault(i => i.Code == SecurityIncidents.PrivilegeChangedCode));

    private long Occurrences() => Incident()?.OccurrenceCount ?? 0;

    private AuditLogEntry LastAudit(string action) => _w.Db(db => db.Set<AuditLogEntry>().AsNoTracking()
        .Where(a => a.Action == action).OrderByDescending(a => a.Id).First());

    private int AuditCount(string action) => _w.Db(db => db.Set<AuditLogEntry>().Count(a => a.Action == action));

    private static JsonElement Summary(AuditLogEntry a) => JsonDocument.Parse(a.SummaryJson ?? "{}").RootElement.Clone();

    /// <summary>稽核的 privilegeHolders 明細；沒有就是 null（沒有異動）。</summary>
    private static JsonElement? Holders(AuditLogEntry a) => Summary(a).TryGetProperty("privilegeHolders", out var h) ? h : null;

    private static string Item(JsonElement i) =>
        $"{i.GetProperty("userId").GetString()}:{State(i.GetProperty("before"))}->{State(i.GetProperty("after"))}";

    private static string State(JsonElement s) =>
        $"[{string.Join(",", s.GetProperty("designer").EnumerateArray().Select(x => x.GetString()))}]{(s.GetProperty("permissionAdmin").GetBoolean() ? "+P" : "")}";

    /// <summary>最近一次事件的種類（標題參數 change：granted／revoked）。</summary>
    private string? Kind() => Incident()?.TitleParamsJson is { } json
        ? JsonDocument.Parse(json).RootElement.GetProperty("change").GetString() : null;

    private int AdminNotifications() => _w.Db(db => db.Set<Notification>().Count(n => n.UserId == Admin));

    private int Outbox() => _w.Db(db => db.Set<AlertOutbox>().Count());

    private Task<HttpResponseMessage> PutGroup(long id, object body) => _admin.PutAsJsonAsync($"{Base}/admin/groups/{id}/system-privileges", body);

    private Task<HttpResponseMessage> Members(long id, params string[] users) =>
        _admin.PutAsJsonAsync($"{Base}/admin/groups/{id}/members", new { userIds = users });

    private static async Task Ok(Task<HttpResponseMessage> call)
    {
        var res = await call;
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PAX18_GroupGrant_RowsAuditIncident_RevokeDeletes_EmptyGroupNoIncident_MissingKeys400()
    {
        Users("u3");
        var gx = NewGroup("pax-g-x", ["u3"]);
        var occurrences = Occurrences();
        var notes = AdminNotifications();
        var outbox = Outbox();

        var res = await PutGroup(gx, Grant);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var saved = (await JsonOf(res)).GetRawText();
        saved.Should().Be(JsonSerializer.Serialize(Grant, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "回應等於請求");

        var row = (await JsonOf(await _admin.GetAsync($"{Base}/admin/groups"))).EnumerateArray().Single(g => g.GetProperty("id").GetInt64() == gx);
        row.GetProperty("systemPrivileges").GetRawText().Should().Be(saved);
        SystemRows(gx).Should().Equal($"{SystemProgramCodes.Designer}:TrueFalseTrueFalseFalseFalseFalse",
            $"{SystemProgramCodes.PermissionAdmin}:FalseFalseTrueFalseFalseFalseFalse");

        var audit = LastAudit("group.systemPrivileges");
        var summary = Summary(audit);
        summary.GetProperty("before").GetProperty("permissionAdmin").GetBoolean().Should().BeFalse();
        summary.GetProperty("after").GetProperty("permissionAdmin").GetBoolean().Should().BeTrue();
        summary.GetProperty("after").GetProperty("designer").GetProperty("edit").GetBoolean().Should().BeTrue();
        var holders = Holders(audit)!.Value;
        holders.GetProperty("total").GetInt32().Should().Be(1);
        holders.GetProperty("truncated").GetBoolean().Should().BeFalse();
        holders.GetProperty("items").EnumerateArray().Select(Item).Should().Equal("u3:[]->[read,edit]+P");

        Occurrences().Should().Be(occurrences + 1);
        Kind().Should().Be("granted");
        var incident = Incident()!;
        incident.Category.Should().Be(IncidentCategory.Security);
        _w.Db(db => db.Set<OpsIncidentSample>().AsNoTracking().Where(s => s.IncidentId == incident.Id).OrderByDescending(s => s.Id).First())
            .AuditLogId.Should().Be(audit.Id);
        AdminNotifications().Should().Be(notes + 1);
        Outbox().Should().Be(outbox + 1);

        // 全部取消：兩列都刪、種類 revoked
        await Ok(PutGroup(gx, None));
        SystemRows(gx).Should().BeEmpty();
        Occurrences().Should().Be(occurrences + 2);
        Kind().Should().Be("revoked");

        // 沒有成員的群組：有稽核、事件次數不變
        var empty = NewGroup("pax-g-empty");
        var audits = AuditCount("group.systemPrivileges");
        await Ok(PutGroup(empty, Grant));
        AuditCount("group.systemPrivileges").Should().Be(audits + 1);
        Holders(LastAudit("group.systemPrivileges")).Should().BeNull();
        Occurrences().Should().Be(occurrences + 2);

        // 缺 designer.edit、permissionAdmin 為 null：400，errors 含兩個鍵，權限列不變
        var bad = await PutGroup(gx, new { designer = new { read = true, add = false, delete = false }, permissionAdmin = (bool?)null });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await JsonOf(bad)).GetProperty("errors");
        errors.TryGetProperty("designer.edit", out _).Should().BeTrue(errors.GetRawText());
        errors.TryGetProperty("permissionAdmin", out _).Should().BeTrue(errors.GetRawText());
        SystemRows(gx).Should().BeEmpty();
    }

    [Fact]
    public async Task PAX19_UserGrant_AuditAndIncident_OtherAdminSavedWithoutIncident_Unknown404()
    {
        Users("u3");
        _w.Write(db => db.Set<AppUser>().Add(new AppUser { UserId = "admin2", DisplayName = "admin2", IsSystemAdmin = true }));
        var occurrences = Occurrences();

        await Ok(_admin.PutAsJsonAsync($"{Base}/admin/users/u3/system-privileges", Grant));
        LastAudit("user.systemPrivileges").TargetId.Should().Be("u3");
        Occurrences().Should().Be(occurrences + 1);
        Kind().Should().Be("granted");

        var audits = AuditCount("user.systemPrivileges");
        await Ok(_admin.PutAsJsonAsync($"{Base}/admin/users/admin2/system-privileges", Body(true, false, false, false, false)));
        AuditCount("user.systemPrivileges").Should().Be(audits + 1);
        _w.Db(db => db.Set<UserProgramPrivilege>().Count(p => p.UserId == "admin2" && p.ProgramCode == SystemProgramCodes.Designer && p.CanRead && !p.CanEdit))
            .Should().Be(1, "目標是系統管理員也照存");
        Occurrences().Should().Be(occurrences + 1, "管理員不在快照裡，不發事件");
        var me = await JsonOf(await _app.AsUser("admin2").GetAsync($"{Base}/me"));
        me.GetProperty("designer").GetProperty("edit").GetBoolean().Should().BeTrue();
        me.GetProperty("permissionAdmin").GetBoolean().Should().BeTrue();

        (await _admin.PutAsJsonAsync($"{Base}/admin/users/no-such-user/system-privileges", Grant)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PAX20_EffectiveHolderChanges_DriveTheIncident()
    {
        Users("u3", "u4", "u5", "u6", "u7");
        var p1 = NewGroup("pax-g-p1", ["u5"], permissionAdmin: true);
        var p2 = NewGroup("pax-g-p2", ["u5"], permissionAdmin: true);
        var r = NewGroup("pax-g-r", ["u6"], designer: (true, false, false, false));
        var re = NewGroup("pax-g-re", ["u6"], designer: (true, false, true, false));
        var plain = NewGroup("pax-g-plain");
        var occurrences = Occurrences();

        async Task<JsonElement?> Step(Task<HttpResponseMessage> call, string action)
        {
            await Ok(call);
            return Holders(LastAudit(action));
        }

        // 加進持有權限設定的群組 → granted，只有 u3
        var h = await Step(Members(p1, "u5", "u3"), "group.setMembers");
        h!.Value.GetProperty("items").EnumerateArray().Select(Item).Should().Equal("u3:[]->[read]+P");
        Kind().Should().Be("granted");
        Occurrences().Should().Be(occurrences + 1);

        // 加進沒有系統權限的群組 → 不發
        (await Step(Members(plain, "u4"), "group.setMembers")).Should().BeNull();
        Occurrences().Should().Be(occurrences + 1);

        // 重疊授權：u5 從 G-p2 移出，有效值不變 → 不發
        (await Step(Members(p2), "group.setMembers")).Should().BeNull();
        Occurrences().Should().Be(occurrences + 1);

        // 部分收回：u6 從 G-re 移出 → revoked，[read, edit] → [read]
        h = await Step(Members(re), "group.setMembers");
        h!.Value.GetProperty("items").EnumerateArray().Select(Item).Should().Equal("u6:[read,edit]->[read]");
        Kind().Should().Be("revoked");
        Occurrences().Should().Be(occurrences + 2);

        // 整份取代：u3 移出、u7 加入 → granted，兩人依 userId 排序
        h = await Step(Members(p1, "u5", "u7"), "group.setMembers");
        h!.Value.GetProperty("items").EnumerateArray().Select(Item).Should().Equal("u3:[read]+P->[]", "u7:[]->[read]+P");
        Kind().Should().Be("granted");

        // 一次加 25 人 → items 20 筆、total 25、truncated
        var many = Enumerable.Range(1, 25).Select(i => $"m{i:D2}").ToArray();
        Users(many);
        h = await Step(Members(p1, [.. many, "u5", "u7"]), "group.setMembers");
        h!.Value.GetProperty("items").GetArrayLength().Should().Be(20);
        h.Value.GetProperty("total").GetInt32().Should().Be(25);
        h.Value.GetProperty("truncated").GetBoolean().Should().BeTrue();

        // 影響與刪除：只靠 G-p1 的人列入、revoked；同時在 G-p2 的不列入
        await Ok(Members(p2, "u5"));
        var impact = await JsonOf(await _admin.GetAsync($"{Base}/admin/groups/{p1}/impact"));
        impact.GetProperty("systemPrivileges").GetProperty("permissionAdmin").GetBoolean().Should().BeTrue();
        h = await Step(_admin.DeleteAsync($"{Base}/admin/groups/{p1}"), "group.delete");
        var deleted = h!.Value;
        deleted.GetProperty("total").GetInt32().Should().Be(26);
        deleted.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetString()).Should().NotContain("u5");
        Kind().Should().Be("revoked");

        // UpdateUser 改群組、CreateUser 帶群組，同樣判斷
        h = await Step(_admin.PutAsJsonAsync($"{Base}/admin/users/u3", new { displayName = "u3", groupIds = new[] { p2 }, isSystemAdmin = false }), "user.update");
        h!.Value.GetProperty("items").EnumerateArray().Select(Item).Should().Equal("u3:[]->[read]+P");
        var created = await _admin.PostAsJsonAsync($"{Base}/admin/users",
            new { userId = "u9", displayName = "u9", password = Password, groupIds = new[] { p2 }, isSystemAdmin = false });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        Holders(LastAudit("user.create"))!.Value.GetProperty("items").EnumerateArray().Select(Item).Should().Equal("u9:[]->[read]+P");
        Kind().Should().Be("granted");
    }

    [Fact]
    public async Task PAX21_SameContentSaved_AuditedWithoutIncident()
    {
        Users("u3");
        var gx = NewGroup("pax-g-x", ["u3"]);
        await Ok(PutGroup(gx, Grant));
        await Ok(_admin.PutAsJsonAsync($"{Base}/admin/users/u3/system-privileges", Grant));
        var occurrences = Occurrences();
        var groupAudits = AuditCount("group.systemPrivileges");
        var userAudits = AuditCount("user.systemPrivileges");

        await Ok(PutGroup(gx, Grant));
        await Ok(_admin.PutAsJsonAsync($"{Base}/admin/users/u3/system-privileges", Grant));

        AuditCount("group.systemPrivileges").Should().Be(groupAudits + 1);
        AuditCount("user.systemPrivileges").Should().Be(userAudits + 1);
        Holders(LastAudit("group.systemPrivileges")).Should().BeNull();
        Occurrences().Should().Be(occurrences);
    }

    /// <summary>PAX-21b 的替身：寫安全事件時丟例外，整個請求（快照、寫入、稽核、事件、通知）要一起回滾。</summary>
    private sealed class ThrowingSecurityIncidents(IncidentRecorder recorder, IOpsEnvironment env) : SecurityIncidents(recorder, env)
    {
        public override Task PrivilegeChangedAsync(AuditLogEntry audit, bool granted, CancellationToken ct) =>
            throw new InvalidOperationException("PAX-21b：模擬寫安全事件失敗");
    }

    [Fact]
    public async Task PAX21b_IncidentFailure_RollsBackEverything()
    {
        _app.Dispose();
        _app = new TestApp
        {
            ExtraServices = s =>
            {
                s.RemoveAll<SecurityIncidents>();
                s.AddScoped<SecurityIncidents, ThrowingSecurityIncidents>();
            }
        };
        await Init();
        Users("u3");
        var gx = NewGroup("pax-g-x", ["u3"]);
        var guard = _w.GuardRows();
        int Count<T>() where T : class => _w.Db(db => db.Set<T>().Count());
        var (audits, incidents, notes, outbox) = (Count<AuditLogEntry>(), Count<OpsIncident>(), Count<Notification>(), Count<AlertOutbox>());

        (await PutGroup(gx, Grant)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        _w.GuardRows().Should().Equal(guard);
        (Count<AuditLogEntry>(), Count<OpsIncident>(), Count<Notification>(), Count<AlertOutbox>()).Should().Be((audits, incidents, notes, outbox));
    }
}
