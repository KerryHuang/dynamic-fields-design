using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using DynamicModules.Application.Dynamic.AccountService.Commands.ResetPassword;
using DynamicModules.Application.Dynamic.AccountService.Commands.SetUserActive;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>停用帳號、管理員重設密碼時連帶撤銷全部權杖（mcp1a 計畫判斷 23，Task 4）。</summary>
[Trait("Category", "Postgres")]
public class McpAccountLinkTests : McpTokenTestBase
{
    /// <summary>wenhui 有 3 把有效、1 把已撤銷（原因 owner）、1 把已過期。</summary>
    private (long[] Active, long Revoked, long Expired) Seed()
    {
        var later = NowUtc.AddDays(10);
        var active = new[] { AddToken("wenhui", "a", later).Id, AddToken("wenhui", "b", later).Id, AddToken("wenhui", "c", later).Id };
        var revoked = AddToken("wenhui", "old", later, revokedUtc: NowUtc.AddDays(-1)).Id;
        var expired = AddToken("wenhui", "expired", NowUtc.AddMinutes(-1)).Id;
        return (active, revoked, expired);
    }

    private System.Text.Json.JsonElement Summary(string action)
    {
        var row = Db.Context.Set<AuditLogEntry>().AsNoTracking().Single(a => a.Action == action);
        return System.Text.Json.JsonDocument.Parse(row.SummaryJson!).RootElement;
    }

    [Fact]
    public async Task Deactivate_RevokesAll()
    {
        var (active, revoked, expired) = Seed();
        var other = AddToken("jiahao", "別人的", NowUtc.AddDays(10)).Id;

        (await Send(new SetUserActiveRequest("wenhui", false))).IsSuccess.Should().BeTrue();

        foreach (var id in active)
        {
            var t = Row(id);
            (t.RevokeReason, t.RevokedBy, t.RevokedUtc).Should().Be((McpRevokeReason.UserDeactivated, "admin", NowUtc));
        }
        var old = Row(revoked);
        (old.RevokeReason, old.RevokedUtc).Should().Be((McpRevokeReason.Owner, NowUtc.AddDays(-1)));
        Row(expired).RevokedUtc.Should().BeNull("過期的本來就不能用，不必改");
        Row(other).RevokedUtc.Should().BeNull();
        Summary("user.setActive").GetProperty("revokedMcpTokens").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Reactivate_DoesNotRestore()
    {
        var (active, _, _) = Seed();
        await Send(new SetUserActiveRequest("wenhui", false));
        (await Send(new SetUserActiveRequest("wenhui", true))).IsSuccess.Should().BeTrue();
        foreach (var id in active) Row(id).RevokeReason.Should().Be(McpRevokeReason.UserDeactivated);
    }

    [Fact]
    public async Task AdminResetPassword_RevokesAll()
    {
        var (active, revoked, _) = Seed();
        (await Send(new ResetPasswordRequest("wenhui", "new-pass-123"))).IsSuccess.Should().BeTrue();
        foreach (var id in active)
        {
            var t = Row(id);
            (t.RevokeReason, t.RevokedBy).Should().Be((McpRevokeReason.PasswordReset, "admin"));
        }
        Row(revoked).RevokeReason.Should().Be(McpRevokeReason.Owner);
        Summary("user.resetPassword").GetProperty("revokedMcpTokens").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task ChangeOwnPassword_Keeps()
    {
        var (active, _, _) = Seed();
        As("wenhui");
        (await Send(new ChangePasswordRequest(Password, "new-pass-123"))).IsSuccess.Should().BeTrue();
        foreach (var id in active) Row(id).RevokedUtc.Should().BeNull();
    }

    /// <summary>稽核寫不進去時整筆回滾：撤銷與停用都不留下（撤銷和停用同一交易）。</summary>
    [Trait("Category", "Postgres")]
    public class AuditFails : McpTokenTestBase
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddScoped<IAuditLog>(sp => new ThrowingAudit(ActivatorUtilities.CreateInstance<AuditLog>(sp)));
        }

        private sealed class ThrowingAudit(IAuditLog inner) : IAuditLog
        {
            public AuditLogEntry Add(AuditEntry entry) => throw new InvalidOperationException("測試：稽核寫不進去");
            public Task WriteNowAsync(AuditEntry entry, CancellationToken ct) => inner.WriteNowAsync(entry, ct);
            public Task<bool> TryWriteNowAsync(AuditEntry entry, CancellationToken ct) => inner.TryWriteNowAsync(entry, ct);
        }

        [Fact]
        public async Task Deactivate_FailsAfterRevoke_RollsBack()
        {
            var id = AddToken("wenhui", "a", NowUtc.AddDays(10)).Id;

            var act = () => Send(new SetUserActiveRequest("wenhui", false));

            await act.Should().ThrowAsync<InvalidOperationException>();
            Row(id).RevokedUtc.Should().BeNull();
            Db.Context.Set<AppUser>().AsNoTracking().Single(u => u.UserId == "wenhui").IsActive.Should().BeTrue();
        }

        [Fact]
        public async Task ResetPassword_FailsAfterRevoke_RollsBack()
        {
            var id = AddToken("wenhui", "a", NowUtc.AddDays(10)).Id;

            var act = () => Send(new ResetPasswordRequest("wenhui", "new-pass-123"));

            await act.Should().ThrowAsync<InvalidOperationException>();
            Row(id).RevokedUtc.Should().BeNull();
        }
    }
}
