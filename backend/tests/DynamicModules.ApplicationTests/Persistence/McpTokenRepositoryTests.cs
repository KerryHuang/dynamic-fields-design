using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>權杖表與稽核表的權杖計數（mcp1a 計畫 Task 1）。雙跑。</summary>
[Trait("Category", "Postgres")]
public class McpTokenRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 1, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static McpToken Token(string user, string hash, DateTime? expires = null, DateTime? revoked = null) => new()
    {
        UserId = user, Name = "laptop", TokenHash = hash, Last4 = "abcd", Scopes = "business.read",
        CreatedUtc = T0.AddDays(-10), ExpiresUtc = expires ?? T0.AddDays(30), RevokedUtc = revoked,
        RevokeReason = revoked is null ? null : McpRevokeReason.Owner,
    };

    private static string H(char c) => new(c, 64);

    private static async Task SaveAsync(TestDb db, params McpToken[] tokens)
    {
        var repo = new McpTokenRepository(db.Context);
        foreach (var t in tokens) repo.Add(t);
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task FindByHash_ExactMatch()
    {
        await using var db = await TestDb.CreateAsync();
        await SaveAsync(db, Token("amy", H('a')));
        var repo = new McpTokenRepository(db.Context);

        var found = await repo.FindByHashAsync(H('a'), Ct);
        found.Should().NotBeNull();
        found!.ExpiresUtc.Kind.Should().Be(DateTimeKind.Utc);
        (await repo.FindByHashAsync(H('A'), Ct)).Should().BeNull();
        db.Context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task CountActive_ExcludesRevokedAndExpired()
    {
        await using var db = await TestDb.CreateAsync();
        await SaveAsync(db,
            Token("amy", H('a')),
            Token("amy", H('b')),
            Token("amy", H('c'), revoked: T0.AddDays(-1)),
            Token("amy", H('d'), expires: T0),            // 剛好到期＝失效
            Token("bob", H('e')));
        var repo = new McpTokenRepository(db.Context);

        (await repo.CountActiveAsync("amy", T0, Ct)).Should().Be(2);
        (await repo.ListActiveTrackedAsync("amy", T0, Ct)).Select(t => t.TokenHash).Should().BeEquivalentTo([H('a'), H('b')]);
    }

    [Fact]
    public async Task TokenHash_Unique()
    {
        await using var db = await TestDb.CreateAsync();
        await SaveAsync(db, Token("amy", H('a')));
        new McpTokenRepository(db.Context).Add(Token("bob", H('a')));
        await db.Context.Invoking(c => c.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ListByUser_OrderedByIdDesc()
    {
        await using var db = await TestDb.CreateAsync();
        await SaveAsync(db, Token("amy", H('a')));
        await SaveAsync(db, Token("bob", H('b')));
        await SaveAsync(db, Token("amy", H('c')));
        var repo = new McpTokenRepository(db.Context);

        var mine = await repo.ListByUserAsync("amy", Ct);
        mine.Select(t => t.TokenHash).Should().Equal(H('c'), H('a'));
        (await repo.ListAsync(null, Ct)).Select(t => t.TokenHash).Should().Equal(H('c'), H('b'), H('a'));
        (await repo.ListAsync("bob", Ct)).Select(t => t.TokenHash).Should().Equal(H('b'));
        db.Context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task PurgeBatch_OnlyOldExpiredOrRevoked()
    {
        await using var db = await TestDb.CreateAsync();
        var cutoff = T0.AddDays(-90);
        await SaveAsync(db,
            Token("amy", H('a'), expires: cutoff.AddDays(-1)),                          // 過期早於 cutoff → 刪
            Token("amy", H('b'), expires: cutoff.AddDays(1)),                           // 過期但晚於 cutoff → 留
            Token("amy", H('c'), revoked: cutoff.AddDays(-2)),                          // 撤銷早於 cutoff → 刪
            Token("amy", H('d'), revoked: cutoff.AddDays(2)),                           // 撤銷晚於 cutoff → 留
            Token("amy", H('e')),                                                       // 有效 → 留
            Token("amy", H('f'), expires: cutoff.AddDays(-3)));                         // 過期早於 cutoff → 刪（超出 take）
        var repo = new McpTokenRepository(db.Context);

        var batch = await repo.ListPurgeBatchTrackedAsync(cutoff, 2, Ct);
        batch.Select(t => t.TokenHash).Should().Equal(H('a'), H('c'));
        repo.RemoveRange(batch);
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();

        (await repo.ListPurgeBatchTrackedAsync(cutoff, 10, Ct)).Select(t => t.TokenHash).Should().Equal(H('f'));
        (await db.Context.Set<McpToken>().CountAsync(Ct)).Should().Be(4);
    }

    private static AuditLogEntry Audit(long? tokenId, string action, DateTime at) => new()
    {
        OccurredUtc = at, ActorUserId = "amy", Channel = AuditChannel.Mcp, TokenId = tokenId, Action = action, Result = AuditResult.Success,
    };

    [Fact]
    public async Task AuditLogRepository_CountForTokenSince_OnlyTokenAndActionsAndTime()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new AuditLogRepository(db.Context);
        repo.Add(Audit(7, "mcp.queryRecords", T0));                 // 算
        repo.Add(Audit(7, "mcp.getRecord", T0.AddMinutes(5)));      // 算
        repo.Add(Audit(7, "mcp.queryRecords", T0.AddSeconds(-1)));  // since 之前
        repo.Add(Audit(8, "mcp.queryRecords", T0.AddMinutes(1)));   // 別的權杖
        repo.Add(Audit(7, "mcp.token.create", T0.AddMinutes(1)));   // 不在清單
        repo.Add(Audit(null, "mcp.queryRecords", T0.AddMinutes(1)));
        await db.Context.SaveChangesAsync(Ct);

        (await repo.CountForTokenSinceAsync(7, T0, ["mcp.queryRecords", "mcp.getRecord"], Ct)).Should().Be(2);
        (await repo.CountForTokenSinceAsync(7, T0, [], Ct)).Should().Be(0);
    }

    [Fact]
    public async Task AuditLogRepository_CountForTokensSince_GroupedByToken()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new AuditLogRepository(db.Context);
        repo.Add(Audit(7, "mcp.queryRecords", T0));                 // 7 算
        repo.Add(Audit(7, "mcp.getRecord", T0.AddMinutes(5)));      // 7 算
        repo.Add(Audit(7, "mcp.queryRecords", T0.AddSeconds(-1)));  // since 之前
        repo.Add(Audit(7, "mcp.token.create", T0.AddMinutes(1)));   // 不在清單
        repo.Add(Audit(8, "mcp.queryRecords", T0.AddMinutes(1)));   // 8 算
        repo.Add(Audit(9, "mcp.queryRecords", T0.AddMinutes(1)));   // 沒查 9
        repo.Add(Audit(null, "mcp.queryRecords", T0.AddMinutes(1)));
        await db.Context.SaveChangesAsync(Ct);
        string[] actions = ["mcp.queryRecords", "mcp.getRecord"];

        (await repo.CountForTokensSinceAsync([7, 8, 10], T0, actions, Ct)).Should().BeEquivalentTo(new Dictionary<long, int> { [7] = 2, [8] = 1 });
        (await repo.CountForTokensSinceAsync([7], T0, [], Ct)).Should().BeEmpty();
        (await repo.CountForTokensSinceAsync([], T0, actions, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task AuditLogRepository_Page_FilterByTokenId()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new AuditLogRepository(db.Context);
        repo.Add(Audit(7, "mcp.queryRecords", T0));
        repo.Add(Audit(8, "mcp.queryRecords", T0));
        repo.Add(Audit(7, "mcp.getRecord", T0));
        await db.Context.SaveChangesAsync(Ct);

        var rows = await repo.PageAsync(new AuditLogQuery(T0.AddDays(-1), T0.AddDays(1), TokenId: 7), 10, Ct);
        rows.Select(r => r.Action).Should().Equal("mcp.getRecord", "mcp.queryRecords");
        rows.Should().OnlyContain(r => r.TokenId == 7);
    }
}
