using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 稽核（維運設計 §17.2.1、§17.8 A02–A04、A06–A08、A11）：管理與設計類變更同交易寫一列、登入成功與失敗、稽核 API、保留天數清理。
/// 寫入路徑與查詢都經 repository 與交易，雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public class AuditApiTests
{
    private const string Audit = $"{Base}/system/ops/audit";
    private const string Auth = $"{Base}/auth";
    private const string Password = "audit-pass-1";

    private static async Task<List<AuditLogEntry>> Rows(TestApp app, string? action = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var q = scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking();
        if (action is not null) q = q.Where(a => a.Action == action);
        return await q.OrderBy(a => a.Id).ToListAsync();
    }

    private static async Task Db(TestApp app, Action<DynamicDbContext> act)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        act(db);
        await db.SaveChangesAsync();
    }

    private static Task AddUser(TestApp app, string id) => Db(app, db => db.Set<AppUser>().Add(new AppUser
    {
        UserId = id, DisplayName = id + " 名稱", IsActive = true,
        PasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Password),
    }));

    /// <summary>新 scope 裡寫不進稽核表（登入的 TryWriteNowAsync 走這條）。</summary>
    private sealed class ThrowingRepository : IAuditLogRepository
    {
        public void Add(AuditLogEntry entry) => throw new InvalidOperationException("稽核表寫不進去（測試注入）");
        public Task<List<AuditLogEntry>> PageAsync(AuditLogQuery query, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<AuditLogEntry?> GetAsync(long id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountSinceAsync(string action, DateTime sinceUtc, CancellationToken ct) => throw new NotSupportedException();
    }

    private static object Priv(bool read = false, bool edit = false) =>
        new { read, add = false, edit, delete = false, printExport = false, pricing = false, approve = false };

    private static object PermissionBody(long groupId, object privileges) =>
        new { groups = new[] { new { groupId, privileges, scope = (string?)null } }, users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>() };

    private static async Task<long> SomeGroup(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<PermissionGroup>().Where(g => g.Key != "mis").OrderBy(g => g.Id).Select(g => g.Id).FirstAsync();
    }

    // ---- A02 ----

    [Fact]
    public async Task A02_Permissions_WriteOneHighRiskRow_WithOnlyChangedCells()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        var group = await SomeGroup(app);

        (await admin.PutAsJsonAsync($"{Base}/modules/{module}/permissions", PermissionBody(group, Priv(read: true)))).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{Base}/modules/{module}/permissions", PermissionBody(group, Priv(read: true, edit: true)))).EnsureSuccessStatusCode();

        var rows = await Rows(app, "permission.update");
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.RiskLevel == AuditRiskLevel.HighRisk && r.Result == AuditResult.Success
            && r.ActorUserId == "admin" && r.ActorKind == AuditActorKind.User && r.Channel == AuditChannel.Ui
            && r.TargetKind == "module" && r.TargetId == module && r.RequestId != null);
        using var second = JsonDocument.Parse(rows[1].SummaryJson!);
        second.RootElement.GetProperty("changeCount").GetInt32().Should().Be(1);
        var cell = second.RootElement.GetProperty("changes")[0];
        cell.GetProperty("key").GetString().Should().Be($"group:{group}.edit");
        cell.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        cell.GetProperty("after").GetString().Should().Be("true");
        using var first = JsonDocument.Parse(rows[0].SummaryJson!);
        first.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("key").GetString()).Should().Equal($"group:{group}.read");
    }

    [Fact]
    public async Task RejectedRequests_400And403_WriteNoAudit()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        var group = await SomeGroup(app);
        var before = (await Rows(app)).Count;

        (await app.AsUser("sales01").PutAsJsonAsync($"{Base}/modules/{module}/permissions", PermissionBody(group, Priv(read: true))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync($"{Base}/modules", new { key = "不合法 key", name = new Dictionary<string, string> { ["zh-TW"] = "x" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"{Base}/system/policies/ops.auditRetentionDays", new { value = 1 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Rows(app)).Should().HaveCount(before);
    }

    [Fact]
    public async Task A02_MoreThan200ChangedCells_ListsFirst200_CountsAll_MarksTruncated()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        var groups = new List<long>();
        for (var i = 0; i < 30; i++)
        {
            var res = await admin.PostAsJsonAsync($"{Base}/admin/groups", new { name = $"稽核群組{i:D2}", locale = "zh-TW" });
            res.StatusCode.Should().Be(HttpStatusCode.Created);
            groups.Add((await JsonOf(res)).GetProperty("id").GetInt64());
        }
        var all = new { read = true, add = true, edit = true, delete = true, printExport = true, pricing = true, approve = true };
        (await admin.PutAsJsonAsync($"{Base}/modules/{module}/permissions", new
        {
            groups = groups.Select(g => new { groupId = g, privileges = all, scope = (string?)null }).ToArray(),
            users = Array.Empty<object>(), fields = Array.Empty<object>(), options = Array.Empty<object>(),
        })).EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse((await Rows(app, "permission.update")).Single().SummaryJson!);
        doc.RootElement.GetProperty("changeCount").GetInt32().Should().BeGreaterThanOrEqualTo(210);
        doc.RootElement.GetProperty("changes").GetArrayLength().Should().Be(200);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A02_WhenAuditWriteFails_PermissionIsNotChanged()
    {
        var failing = new AuditSwitch();
        using var app = new TestApp { ExtraServices = failing.Install };
        var admin = app.AsUser();
        var module = await CreateModule(admin);
        var group = await SomeGroup(app);

        failing.On = true;
        var res = await admin.PutAsJsonAsync($"{Base}/modules/{module}/permissions", PermissionBody(group, Priv(read: true)));
        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        failing.On = false;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var code = await db.Set<Module>().Where(m => m.Key == module).Select(m => m.ProgramCode).SingleAsync();
        (await db.Set<GroupProgramPrivilege>().CountAsync(p => p.ProgramCode == code)).Should().Be(0, "稽核寫不進去整筆回滾");
        (await Rows(app, "permission.update")).Should().BeEmpty();
    }

    /// <summary>可切換的稽核寫入：打開時 Add 與立即寫入都失敗（A02、A04）。</summary>
    private sealed class AuditSwitch
    {
        public volatile bool On;

        public void Install(IServiceCollection s)
        {
            s.AddScoped<AuditLog>();
            s.RemoveAll<IAuditLog>();
            s.AddScoped<IAuditLog>(sp => new Switchable(sp.GetRequiredService<AuditLog>(), this));
        }

        private sealed class Switchable(AuditLog inner, AuditSwitch owner) : IAuditLog
        {
            public AuditLogEntry Add(AuditEntry entry)
            {
                if (owner.On) throw new InvalidOperationException("稽核寫不進去（測試注入）");
                return inner.Add(entry);
            }

            public Task WriteNowAsync(AuditEntry entry, CancellationToken ct) =>
                owner.On ? throw new AuditWriteException(new InvalidOperationException("測試注入")) : inner.WriteNowAsync(entry, ct);

            public Task<bool> TryWriteNowAsync(AuditEntry entry, CancellationToken ct) => inner.TryWriteNowAsync(entry, ct);
        }
    }

    // ---- A03、A04、A11：登入 ----

    private static Task<HttpResponseMessage> Login(HttpClient c, string user, string password) =>
        c.PostAsJsonAsync($"{Auth}/login", new { userId = user, password });

    [Fact]
    public async Task A03_FailedLogins_RecordTargetAndReason_NeverTheInput()
    {
        using var app = new TestApp(realAuth: true);
        await AddUser(app, "audit01");
        var c = app.CreateClient();

        (await Login(c, "audit01", "wrong-pass-9")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(c, "ghost-user-x", "whatever-1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var rows = await Rows(app, "login.failed");
        rows.Should().HaveCount(2);
        (rows[0].TargetKind, rows[0].TargetId, rows[0].ReasonCode, rows[0].Result).Should().Be(("user", "audit01", "wrongPassword", AuditResult.Denied));
        (rows[1].TargetKind, rows[1].TargetId, rows[1].ReasonCode).Should().Be(((string?)null, (string?)null, "unknownAccount"));
        rows.Should().OnlyContain(r => r.ActorUserId == null && r.Channel == AuditChannel.Ui);
        string.Join("|", rows.Select(r => r.SummaryJson)).Should().NotContain("ghost-user-x").And.NotContain("wrong-pass-9").And.NotContain("whatever-1");
    }

    [Fact]
    public async Task A04_AuditWriteFails_LoginStillSucceeds_WithOneErrorLog()
    {
        using var app = new TestApp(realAuth: true) { ExtraServices = s => { s.RemoveAll<IAuditLogRepository>(); s.AddScoped<IAuditLogRepository, ThrowingRepository>(); } };
        await AddUser(app, "audit01");

        (await Login(app.CreateClient(), "audit01", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        app.Logs.Records.Where(r => r.Level == LogLevel.Error && r.Category == typeof(AuditLog).FullName).Should().ContainSingle();
    }

    [Fact]
    public async Task A04_SuccessRow_ActorAndTargetAreTheSignedInUser()
    {
        using var app = new TestApp(realAuth: true);
        await AddUser(app, "audit01");
        (await Login(app.CreateClient(), "audit01", Password)).StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await Rows(app, "login.succeeded")).Should().ContainSingle().Subject;
        (row.ActorUserId, row.ActorKind, row.TargetKind, row.TargetId, row.TargetLabel, row.Result)
            .Should().Be(("audit01", AuditActorKind.User, "user", "audit01", "audit01 名稱", AuditResult.Success));
    }

    [Fact]
    public async Task A11_SwitchingAccounts_ActorIsTheNewAccount_NeverTheCookie()
    {
        using var app = new TestApp(realAuth: true);
        foreach (var id in new[] { "sales91", "stale91", "gone91", "admin91" }) await AddUser(app, id);
        // 三種瀏覽器：sales91 的有效 cookie、stale91 的 cookie（之後重設密碼換 stamp 而失效）、gone91 的 cookie（之後停用）
        var valid = app.CreateClient();
        var stale = app.CreateClient();
        var disabled = app.CreateClient();
        (await Login(valid, "sales91", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Login(stale, "stale91", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Login(disabled, "gone91", Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        await Db(app, db =>
        {
            db.Set<AppUser>().Single(u => u.UserId == "stale91").SecurityStamp = Guid.NewGuid().ToString("N");
            db.Set<AppUser>().Single(u => u.UserId == "gone91").IsActive = false;
        });
        var before = (await Rows(app)).Count;

        // 帶 sales91 的有效 cookie 輸錯 admin91 的密碼
        (await Login(valid, "admin91", "wrong-pass-9")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var c in new[] { valid, stale, disabled })
        {
            var res = await Login(c, "admin91", Password);
            res.StatusCode.Should().Be(HttpStatusCode.OK, "環境已有身分也不因覆寫行為者而丟例外");
            res.Headers.Should().ContainKey("Set-Cookie", "登入回應照常寫入新 cookie");
        }

        var rows = (await Rows(app)).Skip(before).ToList();
        rows.Where(r => r.Action == "login.succeeded").Should().HaveCount(3)
            .And.OnlyContain(r => r.ActorUserId == "admin91" && r.TargetId == "admin91");
        var failed = rows.Should().ContainSingle(r => r.Action == "login.failed").Subject;
        (failed.ActorUserId, failed.TargetId, failed.ReasonCode).Should().Be(((string?)null, "admin91", "wrongPassword"));
    }

    [Fact]
    public async Task DemoLogin_FailuresAreThrottledLikePasswordLogin_ThrottledAttemptsWriteNoAudit()
    {
        using var app = new DemoSiteApp();
        var c = app.CreateClient();
        for (var i = 0; i < 30; i++)
            (await c.PostAsJsonAsync($"{Auth}/demo-login", new { userId = $"ghost{i:D2}" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // 與密碼登入同一個來源節流：同一來源 15 分鐘 20 次失敗後擋下，擋下的不寫稽核
        (await Rows(app, "login.failed")).Should().HaveCount(20);
        (await Rows(app)).Should().NotContain(r => r.Action == "login.succeeded");
    }

    [Fact]
    public async Task DemoLogin_SuccessesCountTowardsPerSourceTotal_AuditStopsGrowing()
    {
        // 審查 P1：Demo 登入不需要秘密，成功也要計入同一來源的總嘗試量，否則匿名者逐次登入公開帳號就能無限寫稽核
        using var app = new DemoSiteApp();
        var c = app.CreateClient();
        for (var i = 0; i < 30; i++)
            (await c.PostAsJsonAsync($"{Auth}/demo-login", new { userId = "admin" })).StatusCode.Should().Be(HttpStatusCode.OK);
        for (var i = 0; i < 5; i++)
            (await c.PostAsJsonAsync($"{Auth}/demo-login", new { userId = "admin" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Rows(app, "login.succeeded")).Should().HaveCount(30);
        (await Rows(app, "login.failed")).Should().BeEmpty("被總量擋下的嘗試不寫稽核");
    }

    // ---- 匯入缺名稱：驗證先於補稽核目標（審查 P2：曾因 Name 為 null 變 500） ----

    [Fact]
    public async Task Imports_MissingName_400_WriteNoAudit()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var before = (await Rows(app)).Count;
        (await admin.PostAsync($"{Base}/templates/import", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"{Base}/apps/packages/import", new StringContent("""{"formatVersion":1}""", System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Rows(app)).Should().HaveCount(before);
    }

    // ---- 稽核目標可辨識（審查 P2：匯入補目標、模組內代碼帶模組） ----

    [Fact]
    public async Task Targets_ImportsCarryParsedIdAndName_ModuleScopedKeysCarryTheModule()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        (await admin.PostAsync($"{Base}/apps/packages/import", new StringContent(Dynamic.AppTestPackages.Crm, System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var import = (await Rows(app, "app.import")).Should().ContainSingle().Subject;
        (import.TargetKind, import.TargetId, import.TargetLabel).Should().Be(("app", "crm-lite", "客戶與聯絡人"));

        (await admin.PostAsJsonAsync($"{Base}/templates/order/instantiate", new { })).StatusCode.Should().Be(HttpStatusCode.Created);
        var exported = System.Text.Json.Nodes.JsonNode.Parse(await (await admin.GetAsync($"{Base}/modules/order/export-template")).Content.ReadAsStringAsync())!.AsObject();
        exported["key"] = "order-audit";
        (await admin.PostAsync($"{Base}/templates/import", new StringContent(exported.ToJsonString(), System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var template = (await Rows(app, "template.import")).Should().ContainSingle().Subject;
        (template.TargetKind, template.TargetId).Should().Be(("template", "order-audit"));
        template.TargetLabel.Should().NotBeNullOrEmpty();

        var module = await CreateModule(admin);
        var name = new Dictionary<string, string> { ["zh-TW"] = "報價單" };
        (await admin.PostAsJsonAsync($"{Base}/modules/{module}/print-templates", new { key = "quote", name, body = "<p>a</p>", active = true }))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{Base}/modules/{module}/print-templates/quote", new { key = "quote", name, body = "<p>b</p>", active = true }))
            .EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"{Base}/modules/{module}/print-templates/quote")).EnsureSuccessStatusCode();
        foreach (var action in new[] { "printTemplate.create", "printTemplate.update", "printTemplate.delete" })
            (await Rows(app, action)).Should().ContainSingle().Which.TargetId.Should().Be($"{module}:quote");

        (await admin.PutAsJsonAsync($"{Base}/modules/{module}/policies/task.remindOverdue", new { value = false })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await Rows(app, "policy.moduleSet")).Should().ContainSingle().Which.TargetId.Should().Be($"{module}:task.remindOverdue");
    }

    // ---- A06：變更前後值與遮蔽 ----

    [Fact]
    public async Task A06_PolicyChange_RecordsOldAndNewValue_ResetPasswordNeverRecordsThePassword()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        const string code = "ops.auditRetentionDays";
        (await admin.PutAsJsonAsync($"{Base}/system/policies/{code}", new { value = 400 })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{Base}/system/policies/{code}", new { value = 500 })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"{Base}/admin/users/sales02/reset-password", new { password = "fwp_secret-pass-77" })).EnsureSuccessStatusCode();

        var policy = await Rows(app, "policy.set");
        policy.Should().HaveCount(2).And.OnlyContain(r => r.TargetKind == "policy" && r.TargetId == code);
        Values(policy[0]).Should().Be((null, "400"));
        Values(policy[1]).Should().Be(("400", "500"));

        var reset = (await Rows(app, "user.resetPassword")).Should().ContainSingle().Subject;
        (reset.TargetId, reset.RiskLevel).Should().Be(("sales02", AuditActions.Find("user.resetPassword")!.Risk));
        (reset.SummaryJson ?? "").Should().NotContain("secret-pass").And.NotContain("fwp_");
    }

    private static (string? Before, string? After) Values(AuditLogEntry row)
    {
        using var doc = JsonDocument.Parse(row.SummaryJson!);
        static string? Read(JsonElement e) => e.GetProperty("value") is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        return (Read(doc.RootElement.GetProperty("before")), Read(doc.RootElement.GetProperty("after")));
    }

    // ---- A07：稽核 API ----

    private static async Task SeedAudit(TestApp app, int count, Func<int, AuditLogEntry> make)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        for (var i = 0; i < count; i++) db.Set<AuditLogEntry>().Add(make(i));
        await db.SaveChangesAsync();
    }

    private static AuditLogEntry Fake(DateTime at, int i) => new()
    {
        OccurredUtc = at, ActorUserId = i % 2 == 0 ? "admin" : "sales01", ActorKind = AuditActorKind.User,
        Channel = i % 4 == 3 ? AuditChannel.Mcp : AuditChannel.Ui, Action = i % 3 == 0 ? "module.create" : "policy.set",
        RiskLevel = AuditRiskLevel.Write, TargetKind = i % 3 == 0 ? "module" : "policy", TargetId = $"t{i % 5}",
        Result = i % 7 == 0 ? AuditResult.Denied : AuditResult.Success, ClientIp = "203.0.113.9", SummaryJson = "{\"n\":" + i + "}",
    };

    private static async Task<List<long>> AllPages(HttpClient c, string query)
    {
        var ids = new List<long>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var res = await c.GetAsync($"{Audit}?{query}&limit=100" + (cursor is null ? "" : $"&cursor={cursor}"));
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
            var body = await JsonOf(res);
            ids.AddRange(body.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt64()));
            cursor = body.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            (++pages).Should().BeLessThan(20);
        } while (cursor is not null);
        return ids;
    }

    [Fact]
    public async Task A07_List_FiltersAndKeysetPaging_WithoutGapsOrDuplicates()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow().UtcDateTime;
        await SeedAudit(app, 400, i => Fake(now.AddMinutes(-(i % 50)).AddSeconds(-i), i));
        await SeedAudit(app, 1, i => Fake(now.AddDays(-30), 0));   // 預設 7 天範圍外
        var admin = app.AsUser();

        var all = await AllPages(admin, "");
        all.Should().HaveCount(400).And.OnlyHaveUniqueItems().And.BeInDescendingOrder();

        (await AllPages(admin, "actorUserId=sales01")).Should().HaveCount(200);
        (await AllPages(admin, "action=module.create")).Should().HaveCount(134);
        (await AllPages(admin, "actionGroup=design")).Should().HaveCount(134);
        (await AllPages(admin, "actionGroup=design&action=policy.set")).Should().BeEmpty("動作與分組取交集");
        (await AllPages(admin, "channel=mcp")).Should().HaveCount(100);
        (await AllPages(admin, "result=denied")).Should().HaveCount(Enumerable.Range(0, 400).Count(i => i % 7 == 0));
        (await AllPages(admin, "targetKind=policy&targetId=t1")).Should().HaveCount(Enumerable.Range(0, 400).Count(i => i % 3 != 0 && i % 5 == 1));
        var wide = $"fromUtc={Uri.EscapeDataString(now.AddDays(-40).ToString("O"))}&toUtc={Uri.EscapeDataString(now.ToString("O"))}";
        (await AllPages(admin, wide)).Should().HaveCount(401);

        var page = await JsonOf(await admin.GetAsync($"{Audit}?limit=1"));
        var first = page.GetProperty("items")[0];
        first.GetProperty("actorName").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("clientIp").GetString().Should().Be("203.0.113.9");
        first.TryGetProperty("summary", out _).Should().BeFalse("列表不帶摘要");
        var detail = await JsonOf(await admin.GetAsync($"{Audit}/{first.GetProperty("id").GetInt64()}"));
        detail.GetProperty("summary").GetProperty("n").ValueKind.Should().Be(JsonValueKind.Number);
        (await admin.GetAsync($"{Audit}/999999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var actions = await JsonOf(await admin.GetAsync($"{Audit}/actions"));
        actions.EnumerateArray().Select(a => a.GetProperty("code").GetString()).Should().Contain(["login.failed", "permission.update"]);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("cursor=abc")]
    [InlineData("channel=Ui")]
    [InlineData("result=nope")]
    [InlineData("actionGroup=nope")]
    [InlineData("fromUtc=2026-11-10T00:00:00Z&toUtc=2026-11-01T00:00:00Z")]
    [InlineData("fromUtc=2024-01-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z")]
    [InlineData("toUtc=0001-01-01T00:00:00Z")]
    [InlineData("toUtc=0001-01-05T00:00:00Z")]
    [InlineData("fromUtc=0001-01-01T00:00:00Z&toUtc=0001-01-02T00:00:00Z")]
    public async Task A07_InvalidQuery_400(string query)
    {
        using var app = new TestApp();
        (await app.AsUser().GetAsync($"{Audit}?{query}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A07_Messages_FollowXLocale()
    {
        using var app = new TestApp();
        var c = app.AsUser();
        async Task<JsonElement> Get(string url, string? locale, HttpStatusCode expected)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (locale is not null) req.Headers.Add("X-Locale", locale);
            var res = await c.SendAsync(req);
            res.StatusCode.Should().Be(expected);
            return await JsonOf(res);
        }

        (await Get($"{Audit}/999999", "en-US", HttpStatusCode.NotFound)).GetProperty("message").GetString().Should().Be("Audit record not found");
        (await Get($"{Audit}/999999", null, HttpStatusCode.NotFound)).GetProperty("message").GetString().Should().Be("找不到稽核紀錄");
        var errors = (await Get($"{Audit}?limit=0&cursor=x&channel=Ui&toUtc=1960-01-01T00:00:00Z", "en-US", HttpStatusCode.BadRequest)).GetProperty("errors");
        errors.GetProperty("limit")[0].GetString().Should().Be("Page size must be between 1 and 100");
        errors.GetProperty("cursor")[0].GetString().Should().Be("The page position is invalid");
        errors.GetProperty("channel")[0].GetString().Should().Be("The filter is invalid");
        errors.GetProperty("toUtc")[0].GetString().Should().Be("Dates cannot be earlier than 1970-01-01");
        (await Get($"{Audit}?fromUtc=2025-01-01T00:00:00Z&toUtc=2026-11-01T00:00:00Z", "en-US", HttpStatusCode.BadRequest))
            .GetProperty("errors").GetProperty("toUtc")[0].GetString().Should().Be("The range can be at most 366 days");
        (await Get($"{Audit}?fromUtc=2026-11-10T00:00:00Z&toUtc=2026-11-01T00:00:00Z", "en-US", HttpStatusCode.BadRequest))
            .GetProperty("errors").GetProperty("fromUtc")[0].GetString().Should().Be("The start time cannot be later than the end time");
    }

    [Fact]
    public async Task A07_NonAdmin_403()
    {
        using var app = new TestApp();
        var c = app.AsUser("sales01");
        (await c.GetAsync(Audit)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.GetAsync($"{Audit}/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await c.GetAsync($"{Audit}/actions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed class DemoSiteApp : TestApp
    {
        public DemoSiteApp() => Settings["Demo:LoginEnabled"] = "true";
    }

    [Fact]
    public async Task A07_DemoLoginEnabled_HidesClientIp()
    {
        using var app = new DemoSiteApp();
        await SeedAudit(app, 1, i => Fake(app.Time.GetUtcNow().UtcDateTime, i));
        var admin = app.AsUser();
        var item = (await JsonOf(await admin.GetAsync(Audit))).GetProperty("items")[0];
        item.TryGetProperty("clientIp", out _).Should().BeFalse();
        var detail = await JsonOf(await admin.GetAsync($"{Audit}/{item.GetProperty("id").GetInt64()}"));
        detail.TryGetProperty("clientIp", out _).Should().BeFalse();
    }

    // ---- A08：保留天數清理 ----

    private sealed class CountingPurge(AuditLogRepositoryHolder holder, int? failOnCall = null) : IAuditLogRepository
    {
        public void Add(AuditLogEntry entry) => holder.Inner.Add(entry);
        public Task<List<AuditLogEntry>> PageAsync(AuditLogQuery query, int take, CancellationToken ct) => holder.Inner.PageAsync(query, take, ct);
        public Task<AuditLogEntry?> GetAsync(long id, CancellationToken ct) => holder.Inner.GetAsync(id, ct);
        public Task<int> CountSinceAsync(string action, DateTime sinceUtc, CancellationToken ct) => holder.Inner.CountSinceAsync(action, sinceUtc, ct);
        public async Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
        {
            if (holder.Batches.Count + 1 == failOnCall) throw new InvalidOperationException("第二批失敗（測試注入）");
            var n = await holder.Inner.PurgeBatchAsync(cutoffUtc, batchSize, ct);
            holder.Batches.Add(n);
            return n;
        }
    }

    private sealed class AuditLogRepositoryHolder(IAuditLogRepository inner, List<int> batches)
    {
        public IAuditLogRepository Inner => inner;
        public List<int> Batches => batches;
    }

    private static TestApp UpkeepApp(List<int> batches, int? failOnCall = null) => new()
    {
        ExtraServices = s =>
        {
            s.RemoveAll<OpsUpkeepOptions>();
            s.AddSingleton(new OpsUpkeepOptions { PurgeBatchSize = 3 });
            s.AddScoped<DynamicModules.Persistence.Repositories.Dynamic.AuditLogRepository>();
            s.RemoveAll<IAuditLogRepository>();
            s.AddScoped<IAuditLogRepository>(sp => new CountingPurge(new AuditLogRepositoryHolder(
                sp.GetRequiredService<DynamicModules.Persistence.Repositories.Dynamic.AuditLogRepository>(), batches), failOnCall));
        },
    };

    private static async Task RunUpkeep(ManualClock clock, JobRunner runner)
    {
        clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);
    }

    private static async Task<JobRunner> UpkeepRunner(TestApp app, ManualClock clock)
    {
        var runner = Runner(app, clock, app.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.upkeep"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        return runner;
    }

    private static int Deleted(AuditLogEntry purge)
    {
        using var doc = JsonDocument.Parse(purge.SummaryJson!);
        return doc.RootElement.GetProperty("deleted").GetProperty("auditLog").GetInt32();
    }

    [Fact]
    public async Task A08_Upkeep_PurgesExpiredRowsInBatches_KeepsNewer_EachBatchAuditedInItsOwnTransaction()
    {
        var batches = new List<int>();
        using var app = UpkeepApp(batches);
        var clock = new ManualClock(Start);
        var now = Start.UtcDateTime;
        await SeedAudit(app, 7, i => Fake(now.AddDays(-366).AddMinutes(-i), i));    // 超過出廠 365 天
        await SeedAudit(app, 4, i => Fake(now.AddDays(-364).AddMinutes(-i), i));    // 還在保留期

        var runner = await UpkeepRunner(app, clock);
        await RunUpkeep(clock, runner);

        (await Row(app, "ops.upkeep")).LastStatus.Should().Be(ScheduledJobStatus.Succeeded);
        batches.Should().Equal(3, 3, 1);
        var rows = await Rows(app);
        rows.Where(r => r.Action != "ops.retention.purge").Should().HaveCount(4).And.OnlyContain(r => r.OccurredUtc > now.AddDays(-365));
        var purges = rows.Where(r => r.Action == "ops.retention.purge").ToList();
        purges.Select(Deleted).Should().Equal(3, 3, 1);
        purges.Should().OnlyContain(p => p.ActorUserId == null && p.ActorKind == AuditActorKind.System && p.Channel == AuditChannel.Job
            && p.TargetKind == "job" && p.TargetId == "ops.upkeep" && p.RequestId == null);

        // 沒東西可刪時不再寫摘要列
        await RunUpkeep(clock, runner);
        (await Rows(app, "ops.retention.purge")).Should().HaveCount(3);
    }

    [Fact]
    public async Task A08_SecondBatchFails_FirstBatchDeletionIsStillAudited()
    {
        var batches = new List<int>();
        using var app = UpkeepApp(batches, failOnCall: 2);
        var clock = new ManualClock(Start);
        var now = Start.UtcDateTime;
        await SeedAudit(app, 7, i => Fake(now.AddDays(-366).AddMinutes(-i), i));

        var runner = await UpkeepRunner(app, clock);
        await RunUpkeep(clock, runner);

        (await Row(app, "ops.upkeep")).LastStatus.Should().NotBe(ScheduledJobStatus.Succeeded);
        batches.Should().Equal(3);
        var rows = await Rows(app);
        rows.Count(r => r.Action != "ops.retention.purge").Should().Be(4, "第一批已提交");
        rows.Where(r => r.Action == "ops.retention.purge").Select(Deleted).Should().Equal(3);
    }
}
