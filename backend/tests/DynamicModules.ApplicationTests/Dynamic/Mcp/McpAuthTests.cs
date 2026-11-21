using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Auth;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>權杖認證的判斷順序（mcp1a 計畫判斷 8，Task 6）：無效一律 Unauthorized、政策擋下是帶身分的 Forbidden。</summary>
/// <summary>權杖認證測試共用：直接寫權杖列、送認證請求。</summary>
public abstract class McpAuthTestBase : McpTokenTestBase
{
    /// <summary>直接寫一列權杖，回明文（不經建立 API 的密碼重驗）。</summary>
    protected (string Raw, McpToken Row) Issue(string userId = "wenhui", DateTime? expiresUtc = null, DateTime? revokedUtc = null,
        string scopes = McpScopes.BusinessRead)
    {
        var raw = McpSecrets.NewToken();
        var row = new McpToken
        {
            UserId = userId, Name = "測試", TokenHash = McpSecrets.Hash(raw), Last4 = McpSecrets.Last4(raw), Scopes = scopes,
            CreatedUtc = NowUtc.AddDays(-1), ExpiresUtc = expiresUtc ?? NowUtc.AddDays(30), RevokedUtc = revokedUtc,
            RevokeReason = revokedUtc is null ? null : McpRevokeReason.Owner,
        };
        Db.Context.Add(row);
        Db.Context.SaveChanges();
        return (raw, row);
    }

    protected Task<McpAuthResult> Auth(string raw) => Send(new AuthenticateMcpTokenRequest(raw, Ip));
}

[Trait("Category", "Postgres")]
public class McpAuthTests : McpAuthTestBase
{
    private async Task SetUser(string userId, Action<AppUser> change)
    {
        var user = await Db.Context.Set<AppUser>().SingleAsync(u => u.UserId == userId);
        change(user);
        await Db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Valid_Ok_WithIdentityAndScopes()
    {
        await Enable();
        var (raw, row) = Issue();

        var r = await Auth(raw);

        r.Status.Should().Be(McpAuthStatus.Ok);
        (r.UserId, r.TokenId, r.ExpiresUtc).Should().Be(("wenhui", row.Id, row.ExpiresUtc));
        r.Scopes.Should().Equal(McpScopes.BusinessRead);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fwp_short")]
    [InlineData("Bearer fwp_xxx")]
    public async Task Malformed_Unauthorized(string raw)
    {
        await Enable();
        (await Auth(raw)).Should().Be(McpAuthResult.Unauthorized);
    }

    [Fact]
    public async Task NotFound_Unauthorized()
    {
        await Enable();
        (await Auth(McpSecrets.NewToken())).Status.Should().Be(McpAuthStatus.Unauthorized);
    }

    [Fact]
    public async Task Revoked_Unauthorized()
    {
        await Enable();
        var (raw, _) = Issue(revokedUtc: NowUtc.AddMinutes(-1));
        (await Auth(raw)).Should().Be(McpAuthResult.Unauthorized);
    }

    [Fact]
    public async Task ExpiresNow_Unauthorized()
    {
        await Enable();
        var (raw, _) = Issue(expiresUtc: NowUtc);
        (await Auth(raw)).Should().Be(McpAuthResult.Unauthorized);
    }

    [Theory]
    [InlineData("business.read,bogus")]
    [InlineData("")]
    public async Task UnparsableScopes_Unauthorized(string scopes)
    {
        await Enable();
        var (raw, _) = Issue(scopes: scopes);
        (await Auth(raw)).Should().Be(McpAuthResult.Unauthorized);
    }

    [Fact]
    public async Task Deactivated_Unauthorized()
    {
        await Enable();
        var (raw, _) = Issue();
        await SetUser("wenhui", u => u.IsActive = false);
        (await Auth(raw)).Should().Be(McpAuthResult.Unauthorized);
    }

    [Fact]
    public async Task MustChangePassword_Unauthorized()
    {
        await Enable();
        var (raw, _) = Issue();
        await SetUser("wenhui", u => u.MustChangePassword = true);
        (await Auth(raw)).Should().Be(McpAuthResult.Unauthorized);
    }

    [Fact]
    public async Task DemoAccount_Unauthorized()
    {
        await Enable();
        var (raw, _) = Issue();
        await SetUser("wenhui", u => u.IsDemoAccount = true);
        (await Auth(raw)).Should().Be(McpAuthResult.Unauthorized);
    }

    [Fact]
    public async Task Disabled_ForbiddenWithIdentity()
    {
        var (raw, row) = Issue();
        var r = await Auth(raw);
        (r.Status, r.Code, r.UserId, r.TokenId).Should().Be((McpAuthStatus.Forbidden, McpMessages.CodeDisabled, "wenhui", row.Id));
    }

    [Fact]
    public async Task PolicyInvalid_Forbidden()
    {
        await Enable();
        await SetSystem(PolicyCatalog.McpAllowedGroups, "everyone!");
        var (raw, row) = Issue();
        var r = await Auth(raw);
        (r.Status, r.Code, r.TokenId).Should().Be((McpAuthStatus.Forbidden, McpMessages.CodePolicyInvalid, row.Id));
    }

    /// <summary>被移出允許對象：下一次呼叫就 Forbidden；放回後 Ok，權杖保留（設計 §10 表）。</summary>
    [Fact]
    public async Task RemovedFromAllowed_ForbiddenThenOkWhenRestored()
    {
        await Enable();
        var (raw, row) = Issue();
        await SetSystem(PolicyCatalog.McpAllowedGroups, "user:wenhui");
        (await Auth(raw)).Status.Should().Be(McpAuthStatus.Ok);

        await SetSystem(PolicyCatalog.McpAllowedGroups, "user:jiahao");
        var denied = await Auth(raw);
        (denied.Status, denied.Code, denied.TokenId).Should().Be((McpAuthStatus.Forbidden, McpMessages.CodeNotAllowed, row.Id));

        await SetSystem(PolicyCatalog.McpAllowedGroups, "user:wenhui,user:jiahao");
        (await Auth(raw)).Status.Should().Be(McpAuthStatus.Ok);
        Row(row.Id).RevokedUtc.Should().BeNull();
    }

    /// <summary>最後使用時間最多每 60 秒寫一次；Forbidden 不寫。</summary>
    [Fact]
    public async Task Touch_AtMostOncePer60Seconds()
    {
        var (raw, row) = Issue();
        (await Auth(raw)).Status.Should().Be(McpAuthStatus.Forbidden);
        Row(row.Id).LastUsedUtc.Should().BeNull();

        await Enable();
        var first = NowUtc;
        (await Auth(raw)).Status.Should().Be(McpAuthStatus.Ok);
        Row(row.Id).LastUsedUtc.Should().Be(first);
        Row(row.Id).LastUsedIp.Should().Be(Ip);

        Clock.Advance(TimeSpan.FromSeconds(59));
        await Auth(raw);
        Row(row.Id).LastUsedUtc.Should().Be(first);

        Clock.Advance(TimeSpan.FromSeconds(2));
        await Auth(raw);
        Row(row.Id).LastUsedUtc.Should().Be(NowUtc);
    }

    /// <summary>站台開著 Demo 一鍵登入：有效權杖也是 Forbidden（mcpDemoSite），帶身分。</summary>
    [Trait("Category", "Postgres")]
    public class DemoSite : McpAuthTestBase
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(new AuthSettings(true));
        }

        [Fact]
        public async Task DemoSite_Forbidden()
        {
            await Enable();
            var (raw, row) = Issue();
            var r = await Auth(raw);
            (r.Status, r.Code, r.UserId, r.TokenId).Should().Be((McpAuthStatus.Forbidden, McpMessages.CodeDemoSite, "wenhui", row.Id));
        }
    }
}
