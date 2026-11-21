using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Tokens;
using DynamicModules.ApplicationTests.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>權杖 handler 測試共用：營運時區是出廠 +08:00，業務時間固定 2026-11-04 09:00，UTC 時鐘與它一致。</summary>
public abstract class McpTokenTestBase : DesignerTestBase
{
    protected const string Password = "token-pass-1";
    protected const string Ip = "203.0.113.5";
    protected static readonly DateOnly Today = new(2026, 11, 4);

    /// <summary>與業務時間一致的 UTC 時鐘（09:00 +08:00）。</summary>
    protected readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero));

    protected override bool ScopePerRequest => true;

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IPasswordService, FakePasswords>();
    }

    /// <summary>正式的 PBKDF2 實作在 WebAPI；這裡只要「對的密碼驗得過、錯的驗不過」。</summary>
    private sealed class FakePasswords : IPasswordService
    {
        public string Hash(string password) => "fake:" + password;
        public bool Verify(string hash, string password) => hash == "fake:" + password;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var hash = Svc<IPasswordService>().Hash(Password);
        Db.Context.Set<AppUser>().Single(u => u.UserId == "admin").PasswordHash = hash;
        Db.Context.Add(new AppUser { UserId = "wenhui", DisplayName = "林文慧", PasswordHash = hash });
        Db.Context.Add(new AppUser { UserId = "jiahao", DisplayName = "陳家豪", PasswordHash = hash });
        await Db.Context.SaveChangesAsync();
    }

    protected async Task SetSystem(PolicyDefinition def, string? value)
    {
        var row = await Db.Context.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == def.SettingKey);
        if (row is not null) Db.Context.Remove(row);
        if (value is not null) Db.Context.Add(new SystemSetting { Key = def.SettingKey, Value = value });
        await Db.Context.SaveChangesAsync();
    }

    protected Task Enable() => SetSystem(PolicyCatalog.McpEnabled, "true");

    protected Task<ServiceResult> Create(
        string name = "筆電上的 Claude", string[]? scopes = null, DateOnly? expiresOn = null, string password = Password) =>
        Send(new CreateMcpTokenRequest(name, scopes ?? [McpScopes.BusinessRead], expiresOn ?? Today.AddDays(30), password, Ip));

    protected McpToken AddToken(string userId, string name, DateTime expiresUtc, DateTime? revokedUtc = null)
    {
        var token = new McpToken
        {
            UserId = userId, Name = name, TokenHash = McpSecrets.Hash(McpSecrets.NewToken()), Last4 = "abcd", Scopes = McpScopes.BusinessRead,
            CreatedUtc = Clock.GetUtcNow().UtcDateTime.AddHours(-1), ExpiresUtc = expiresUtc, RevokedUtc = revokedUtc,
            RevokeReason = revokedUtc is null ? null : McpRevokeReason.Owner,
        };
        Db.Context.Add(token);
        Db.Context.SaveChanges();
        return token;
    }

    protected DateTime NowUtc => Clock.GetUtcNow().UtcDateTime;

    protected McpToken Row(long id)
    {
        Db.Context.ChangeTracker.Clear();
        return Db.Context.Set<McpToken>().AsNoTracking().Single(t => t.Id == id);
    }

}

/// <summary>個人存取權杖 API 的 handler（mcp1a 計畫判斷 7、16，Task 3）。</summary>
public class McpTokenHandlerTests : McpTokenTestBase
{
    [Fact]
    public async Task Create_RequiresPassword()
    {
        await Enable();
        var r = await Create(password: "wrong-pass");
        r.IsInvalid.Should().BeTrue();
        Err(r, McpMessages.PasswordField).Should().Be("密碼錯誤");
        Db.Context.Set<McpToken>().Should().BeEmpty();

        // 與登入同一套降速：帳號＋IP 15 分鐘內輸錯 5 次（這裡已經 1 次）後，正確密碼也被擋，回 429
        for (var i = 0; i < 4; i++) (await Create(password: "wrong-pass")).IsInvalid.Should().BeTrue();
        var throttled = await Create();
        throttled.IsTooManyRequests.Should().BeTrue();
        throttled.Code.Should().Be(McpMessages.CodePasswordThrottled);
        Db.Context.Set<McpToken>().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_Success_PlaintextOnce()
    {
        await Enable();
        var r = await Create();
        r.IsCreated.Should().BeTrue();
        var created = (McpTokenCreated)r.Data!;
        created.Token.Should().StartWith("fwp_").And.HaveLength(McpSecrets.TokenLength);

        var row = Db.Context.Set<McpToken>().AsNoTracking().Single();
        row.TokenHash.Should().Be(McpSecrets.Hash(created.Token)).And.NotContain(created.Token);
        row.Last4.Should().Be(created.Token[^4..]);
        row.UserId.Should().Be("admin");
        row.Scopes.Should().Be(McpScopes.BusinessRead);
        created.Item.Status.Should().Be("active");
        created.Item.Name.Should().Be("筆電上的 Claude");

        var list = (IReadOnlyList<McpTokenItem>)(await Send(new ListMyMcpTokensRequest())).Data!;
        list.Should().ContainSingle();
        var json = System.Text.Json.JsonSerializer.Serialize(list);
        json.Should().NotContain(created.Token).And.NotContain(row.TokenHash);
        typeof(McpTokenItem).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Hash"));
    }

    [Fact]
    public async Task Create_ExpiryRange()
    {
        await Enable();
        var r = await Create(expiresOn: Today);
        r.IsInvalid.Should().BeTrue();
        Err(r, McpMessages.ExpiresOnField).Should().Contain("2026-11-05").And.Contain("2027-02-02");
        (await Create(expiresOn: Today.AddDays(1))).IsCreated.Should().BeTrue();
        (await Create(expiresOn: Today.AddDays(90))).IsCreated.Should().BeTrue();
        (await Create(expiresOn: Today.AddDays(91))).IsInvalid.Should().BeTrue();

        await SetSystem(PolicyCatalog.McpTokenMaxLifetimeDays, "7");
        Err(await Create(expiresOn: Today.AddDays(8)), McpMessages.ExpiresOnField).Should().Contain("2026-11-11");
        (await Send(new CreateMcpTokenRequest("x", [McpScopes.BusinessRead], null, Password, Ip))).IsInvalid.Should().BeTrue("沒給到期日");
    }

    [Fact]
    public async Task Create_ExpiresUtc_EndOfBusinessDay()
    {
        await Enable();
        var r = await Create(expiresOn: new DateOnly(2026, 12, 1));
        var item = ((McpTokenCreated)r.Data!).Item;
        var row = Row(item.Id);
        row.ExpiresUtc.Should().Be(new DateTime(2026, 12, 1, 16, 0, 0, DateTimeKind.Utc));
        row.CreatedUtc.Should().Be(NowUtc);
        item.ExpiresOn.Should().Be(new DateOnly(2026, 12, 1));
    }

    [Fact]
    public async Task Create_MaxPerUser()
    {
        await Enable();
        for (var i = 0; i < 9; i++) AddToken("admin", $"t{i}", NowUtc.AddDays(10));
        AddToken("admin", "revoked", NowUtc.AddDays(10), revokedUtc: NowUtc.AddMinutes(-5));
        AddToken("admin", "expired", NowUtc.AddMinutes(-1));
        AddToken("wenhui", "other", NowUtc.AddDays(10));

        (await Create()).IsCreated.Should().BeTrue("已撤銷、已過期、別人的都不算");
        var r = await Create();
        r.IsForbidden.Should().BeTrue();
        r.Code.Should().Be(McpMessages.CodeTokenLimit);
        r.Message.Should().Contain("10");

        await SetSystem(PolicyCatalog.McpTokenMaxPerUser, "11");
        (await Create()).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task Create_ScopeNotIssuable()
    {
        await Enable();
        Err(await Create(scopes: [McpScopes.DesignRead]), McpMessages.ScopesField).Should().NotBeNullOrEmpty();
        Err(await Create(scopes: []), McpMessages.ScopesField).Should().NotBeNullOrEmpty();
        Err(await Create(scopes: ["business.read", "nope"]), McpMessages.ScopesField).Should().NotBeNullOrEmpty();
        Db.Context.Set<McpToken>().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateToken_BusinessWrite_Allowed()
    {
        await Enable();
        var r = await Create(scopes: [McpScopes.BusinessRead, McpScopes.BusinessWrite]);
        r.IsCreated.Should().BeTrue();
        ((McpTokenCreated)r.Data!).Item.Scopes.Should().Equal(McpScopes.BusinessRead, McpScopes.BusinessWrite);
        Err(await Create(scopes: [McpScopes.DesignRead]), McpMessages.ScopesField).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Info_DefaultScopes_ReadOnly()
    {
        await Enable();
        var info = (McpInfoResponse)(await Send(new GetMyMcpInfoRequest("http://h"))).Data!;
        info.Scopes.Should().Equal(McpScopes.BusinessRead, McpScopes.BusinessWrite);
        info.DefaultScopes.Should().Equal(McpScopes.BusinessRead);
    }

    [Fact]
    public async Task Create_NameLength()
    {
        await Enable();
        Err(await Create(name: "   "), McpMessages.NameField).Should().Contain("100");
        Err(await Create(name: new string('名', 101)), McpMessages.NameField).Should().Contain("100");
        var ok = await Create(name: "  " + new string('名', 100) + "  ");
        ((McpTokenCreated)ok.Data!).Item.Name.Should().Be(new string('名', 100), "前後空白去掉");
    }

    [Fact]
    public async Task Create_Disabled_403()
    {
        var r = await Create();
        r.IsForbidden.Should().BeTrue();
        r.Code.Should().Be(McpMessages.CodeDisabled);
        Db.Context.Set<McpToken>().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_NotAllowed_403()
    {
        await Enable();
        var group = Db.Context.Set<PermissionGroup>().AsNoTracking().OrderBy(g => g.Id).First().Id;
        await SetSystem(PolicyCatalog.McpAllowedGroups, $"group:{group}");
        As("wenhui");
        var r = await Create();
        r.IsForbidden.Should().BeTrue();
        r.Code.Should().Be(McpMessages.CodeNotAllowed);
    }

    [Fact]
    public async Task Create_UserListed_Allowed()
    {
        await Enable();
        await SetSystem(PolicyCatalog.McpAllowedGroups, "user:wenhui");
        As("wenhui");
        (await Create()).IsCreated.Should().BeTrue();
        As("jiahao");
        (await Create()).Code.Should().Be(McpMessages.CodeNotAllowed);
    }

    [Fact]
    public async Task Create_AdminsItem()
    {
        await Enable();
        await SetSystem(PolicyCatalog.McpAllowedGroups, "admins");
        (await Create()).IsCreated.Should().BeTrue();
        As("wenhui");
        (await Create()).Code.Should().Be(McpMessages.CodeNotAllowed);
    }

    [Fact]
    public async Task Create_AllowedGroupsResolvesEmpty_403()
    {
        await Enable();
        await SetSystem(PolicyCatalog.McpAllowedGroups, "group:999999");
        var r = await Create();
        r.IsForbidden.Should().BeTrue();
        r.Code.Should().Be(McpMessages.CodeNotAllowed);
    }

    [Fact]
    public async Task Create_PolicyInvalid_403()
    {
        await Enable();
        await SetSystem(PolicyCatalog.McpAllowedGroups, "everyone!");
        var r = await Create();
        r.IsForbidden.Should().BeTrue();
        r.Code.Should().Be(McpMessages.CodePolicyInvalid);
    }

    [Fact]
    public async Task Create_IntegerPolicyInvalid_403()
    {
        await Enable();
        await SetSystem(PolicyCatalog.McpTokenMaxPerUser, "999");
        var r = await Create();
        r.IsForbidden.Should().BeTrue();
        r.Code.Should().Be(McpMessages.CodePolicyInvalid);
        Db.Context.Set<McpToken>().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_DemoAccount_403()
    {
        await Enable();
        As("sales01");
        (await Create()).Code.Should().Be(McpMessages.CodeAccountBlocked);

        Db.Context.Set<AppUser>().Single(u => u.UserId == "wenhui").MustChangePassword = true;
        Db.Context.Set<AppUser>().Single(u => u.UserId == "jiahao").IsActive = false;
        await Db.Context.SaveChangesAsync();
        As("wenhui");
        (await Create()).Code.Should().Be(McpMessages.CodeAccountBlocked);
        As("jiahao");
        (await Create()).Code.Should().Be(McpMessages.CodeAccountBlocked);
        Db.Context.Set<McpToken>().Should().BeEmpty();
    }

    [Fact]
    public async Task Revoke_Own()
    {
        await Enable();
        var id = ((McpTokenCreated)(await Create()).Data!).Item.Id;
        Clock.Advance(TimeSpan.FromMinutes(3));
        (await Send(new RevokeMyMcpTokenRequest(id))).IsSuccess.Should().BeTrue();
        var row = Row(id);
        (row.RevokeReason, row.RevokedBy, row.RevokedUtc).Should().Be((McpRevokeReason.Owner, "admin", NowUtc));

        Clock.Advance(TimeSpan.FromMinutes(3));
        (await Send(new RevokeMyMcpTokenRequest(id))).IsSuccess.Should().BeTrue("冪等");
        Row(id).RevokedUtc.Should().Be(row.RevokedUtc, "不改原因與時間");

        var item = ((IReadOnlyList<McpTokenItem>)(await Send(new ListMyMcpTokensRequest())).Data!).Single();
        (item.Status, item.RevokeReason).Should().Be(("revoked", "owner"));
    }

    [Fact]
    public async Task Revoke_Other_404()
    {
        var other = AddToken("wenhui", "別人的", NowUtc.AddDays(10));
        var r = await Send(new RevokeMyMcpTokenRequest(other.Id));
        r.IsNotFound.Should().BeTrue();
        Row(other.Id).RevokedUtc.Should().BeNull();
        (await Send(new RevokeMyMcpTokenRequest(987654))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_Revoke()
    {
        var token = AddToken("wenhui", "文慧的", NowUtc.AddDays(10));
        As("wenhui");
        (await Send(new AdminRevokeMcpTokenRequest(token.Id))).IsForbidden.Should().BeTrue("非管理員被 behavior 擋");
        As("admin");
        (await Send(new AdminRevokeMcpTokenRequest(token.Id))).IsSuccess.Should().BeTrue();
        var row = Row(token.Id);
        (row.RevokeReason, row.RevokedBy).Should().Be((McpRevokeReason.Admin, "admin"));
        (await Send(new AdminRevokeMcpTokenRequest(token.Id))).IsSuccess.Should().BeTrue();
        Row(token.Id).RevokeReason.Should().Be(McpRevokeReason.Admin);
        (await Send(new AdminRevokeMcpTokenRequest(987654))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_RevokeAll()
    {
        var a = AddToken("wenhui", "a", NowUtc.AddDays(10));
        var b = AddToken("wenhui", "b", NowUtc.AddDays(10));
        var old = AddToken("wenhui", "old", NowUtc.AddDays(10), revokedUtc: NowUtc.AddDays(-1));
        var keep = AddToken("jiahao", "keep", NowUtc.AddDays(10));
        As("jiahao");
        (await Send(new AdminRevokeAllMcpTokensRequest("wenhui"))).IsForbidden.Should().BeTrue();
        As("admin");
        (await Send(new AdminRevokeAllMcpTokensRequest("wenhui"))).IsSuccess.Should().BeTrue();
        Row(a.Id).RevokeReason.Should().Be(McpRevokeReason.Admin);
        Row(b.Id).RevokeReason.Should().Be(McpRevokeReason.Admin);
        Row(old.Id).RevokeReason.Should().Be(McpRevokeReason.Owner, "已撤銷的不改原因");
        Row(keep.Id).RevokedUtc.Should().BeNull();
        (await Send(new AdminRevokeAllMcpTokensRequest("nobody"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_List_FilterByUserAndStatus()
    {
        AddToken("wenhui", "active", NowUtc.AddDays(10));
        AddToken("wenhui", "expired", NowUtc.AddMinutes(-1));
        AddToken("jiahao", "revoked", NowUtc.AddDays(10), revokedUtc: NowUtc.AddMinutes(-1));
        As("wenhui");
        (await Send(new AdminListMcpTokensRequest(null, null))).IsForbidden.Should().BeTrue();
        As("admin");
        IReadOnlyList<McpTokenItem> List(ServiceResult r) => (IReadOnlyList<McpTokenItem>)r.Data!;
        List(await Send(new AdminListMcpTokensRequest(null, null))).Should().HaveCount(3);
        List(await Send(new AdminListMcpTokensRequest("wenhui", null))).Select(i => i.Name).Should().BeEquivalentTo("active", "expired");
        List(await Send(new AdminListMcpTokensRequest(null, "expired"))).Select(i => i.Name).Should().Equal("expired");
        List(await Send(new AdminListMcpTokensRequest(null, "revoked"))).Single().UserId.Should().Be("jiahao");
        (await Send(new AdminListMcpTokensRequest(null, "bogus"))).IsInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Info_CanUse_Reasons()
    {
        McpInfoResponse Info(ServiceResult r) => (McpInfoResponse)r.Data!;
        var off = Info(await Send(new GetMyMcpInfoRequest("http://127.0.0.1:5180")));
        (off.Enabled, off.CanUse, off.Reason).Should().Be((false, false, "disabled"));

        await Enable();
        var on = Info(await Send(new GetMyMcpInfoRequest("http://127.0.0.1:5180/")));
        (on.Enabled, on.CanUse, on.Reason).Should().Be((true, true, (string?)null));
        on.Scopes.Should().Equal(McpScopes.BusinessRead, McpScopes.BusinessWrite);
        (on.DefaultExpiresOn, on.MaxExpiresOn).Should().Be((Today.AddDays(30), Today.AddDays(90)));
        on.ServerUrl.Should().Be("http://127.0.0.1:5180/mcp");
        on.Examples.ClaudeCode.Should().Be(
            "claude mcp add --transport http formwright http://127.0.0.1:5180/mcp --header 'Authorization: Bearer ${FORMWRIGHT_TOKEN}'");
        on.Examples.Codex.Should().Be(
            "[mcp_servers.formwright]\nurl = \"http://127.0.0.1:5180/mcp\"\nbearer_token_env_var = \"FORMWRIGHT_TOKEN\"");

        await SetSystem(PolicyCatalog.McpTokenMaxLifetimeDays, "14");
        var clamped = Info(await Send(new GetMyMcpInfoRequest("http://127.0.0.1:5180")));
        (clamped.DefaultExpiresOn, clamped.MaxExpiresOn).Should().Be((Today.AddDays(14), Today.AddDays(14)));

        await SetSystem(PolicyCatalog.McpAllowedGroups, "user:jiahao");
        As("wenhui");
        Info(await Send(new GetMyMcpInfoRequest("http://h"))).Reason.Should().Be("notAllowed");
        As("sales01");
        var blocked = Info(await Send(new GetMyMcpInfoRequest("http://h")));
        (blocked.Enabled, blocked.CanUse, blocked.Reason).Should().Be((true, false, "accountBlocked"));
    }

    [Fact]
    public async Task Calls_OnlyOwnToken_Recent50()
    {
        var mine = AddToken("admin", "mine", NowUtc.AddDays(10));
        var other = AddToken("wenhui", "other", NowUtc.AddDays(10));
        var start = mine.CreatedUtc;
        for (var i = 0; i < 60; i++)
            Db.Context.Add(new AuditLogEntry
            {
                OccurredUtc = start.AddSeconds(i), ActorUserId = "admin", Channel = AuditChannel.Mcp, TokenId = mine.Id,
                Action = "mcp.queryRecords", TargetLabel = "query_records", Result = i % 2 == 0 ? AuditResult.Success : AuditResult.Denied,
                ReasonCode = i % 2 == 0 ? null : "rate_limited",
            });
        Db.Context.Add(new AuditLogEntry
        {
            OccurredUtc = start.AddSeconds(30), ActorUserId = "wenhui", Channel = AuditChannel.Mcp, TokenId = other.Id, Action = "mcp.whoami",
            TargetLabel = "whoami", Result = AuditResult.Success,
        });
        await Db.Context.SaveChangesAsync();

        var calls = (IReadOnlyList<McpTokenCall>)(await Send(new GetMyMcpTokenCallsRequest(mine.Id))).Data!;
        calls.Should().HaveCount(McpLimits.RecentCalls);
        calls.Select(c => c.OccurredUtc).Should().BeInDescendingOrder();
        calls[0].OccurredUtc.Should().Be(start.AddSeconds(59));
        (calls[0].Tool, calls[0].Result, calls[0].ReasonCode).Should().Be(("query_records", "denied", "rate_limited"));
        calls.Should().OnlyContain(c => c.Action == "mcp.queryRecords");

        (await Send(new GetMyMcpTokenCallsRequest(other.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task List_TodayCalls_FromBusinessMidnight()
    {
        var token = AddToken("admin", "mine", NowUtc.AddDays(10));
        // 營運時區今天 00:00 是 2026-11-03T16:00Z：之前的不算；工具呼叫與不存在的工具算，存取被擋（mcp.access）不算
        foreach (var (at, action) in new[]
                 {
                     (new DateTime(2026, 11, 3, 15, 59, 0, DateTimeKind.Utc), "mcp.whoami"),
                     (new DateTime(2026, 11, 3, 16, 0, 0, DateTimeKind.Utc), "mcp.whoami"),
                     (new DateTime(2026, 11, 4, 0, 30, 0, DateTimeKind.Utc), "mcp.unknownTool"),
                     (new DateTime(2026, 11, 4, 0, 40, 0, DateTimeKind.Utc), "mcp.access"),
                 })
            Db.Context.Add(new AuditLogEntry { OccurredUtc = at, Channel = AuditChannel.Mcp, TokenId = token.Id, Action = action, Result = AuditResult.Success });
        await Db.Context.SaveChangesAsync();

        var item = ((IReadOnlyList<McpTokenItem>)(await Send(new ListMyMcpTokensRequest())).Data!).Single();
        item.TodayCalls.Should().Be(2);
    }

    [Fact]
    public async Task AdminList_TodayCalls_PerToken()
    {
        var a = AddToken("admin", "a", NowUtc.AddDays(10));
        var b = AddToken("wenhui", "b", NowUtc.AddDays(10));
        AddToken("jiahao", "c", NowUtc.AddDays(10));
        var today = new DateTime(2026, 11, 4, 0, 30, 0, DateTimeKind.Utc);
        foreach (var (tokenId, action) in new[] { (a.Id, "mcp.whoami"), (a.Id, "mcp.listModules"), (b.Id, "mcp.unknownTool"), (b.Id, "mcp.access") })
            Db.Context.Add(new AuditLogEntry { OccurredUtc = today, Channel = AuditChannel.Mcp, TokenId = tokenId, Action = action, Result = AuditResult.Success });
        await Db.Context.SaveChangesAsync();

        ((IReadOnlyList<McpTokenItem>)(await Send(new AdminListMcpTokensRequest(null, null))).Data!).ToDictionary(i => i.Name, i => i.TodayCalls)
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["a"] = 2, ["b"] = 1, ["c"] = 0 });
    }

    [Fact]
    public async Task Audit_Create_NoSecret()
    {
        await Enable();
        var created = (McpTokenCreated)(await Create(name: "家裡的 Codex", expiresOn: new DateOnly(2026, 12, 1))).Data!;
        var hash = McpSecrets.Hash(created.Token);
        var row = Db.Context.Set<AuditLogEntry>().AsNoTracking().Single(a => a.Action == "mcp.token.create");
        (row.TargetKind, row.TargetId, row.ActorUserId).Should().Be(("user", "admin", "admin"));
        var all = System.Text.Json.JsonSerializer.Serialize(row);
        all.Should().NotContain(created.Token).And.NotContain(hash).And.NotContain(Password).And.NotContain(created.Token[4..]);
        var summary = System.Text.Json.JsonDocument.Parse(row.SummaryJson!).RootElement;
        summary.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("tokenId", "name", "scopes", "expiresOn");
        (summary.GetProperty("name").GetString(), summary.GetProperty("scopes").GetString(), summary.GetProperty("expiresOn").GetString())
            .Should().Be(("家裡的 Codex", McpScopes.BusinessRead, "2026-12-01"));

        await Send(new RevokeMyMcpTokenRequest(created.Item.Id));
        Db.Context.Set<AuditLogEntry>().AsNoTracking().Single(a => a.Action == "mcp.token.revoke").TargetId.Should().Be("admin");
    }

    /// <summary>站台開著 Demo 一鍵登入時整個 MCP 停用（設計 §5.1）。</summary>
    public class DemoSite : McpTokenTestBase
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(new AuthSettings(true));
        }

        [Fact]
        public async Task Create_DemoSite_403()
        {
            await Enable();
            var r = await Create();
            r.IsForbidden.Should().BeTrue();
            r.Code.Should().Be(McpMessages.CodeDemoSite);
            var info = (McpInfoResponse)(await Send(new GetMyMcpInfoRequest("http://h"))).Data!;
            (info.CanUse, info.Reason).Should().Be((false, "demoSite"));
        }
    }
}
