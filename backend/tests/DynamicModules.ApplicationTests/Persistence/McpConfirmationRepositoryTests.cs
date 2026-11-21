using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>確認碼表（mcp1a 計畫 Task 1、判斷 18、22）。雙跑。</summary>
[Trait("Category", "Postgres")]
public class McpConfirmationRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 1, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static McpConfirmation Row(string hash, DateTime created, long tokenId = 1, DateTime? expires = null,
        DateTime? used = null, DateTime? voided = null) => new()
    {
        CodeHash = hash, TokenId = tokenId, UserId = "amy", ToolName = "test_tool", ArgumentsHash = new string('0', 64),
        TargetVersion = "v1", PreviewSummary = "{}", CreatedUtc = created, ExpiresUtc = expires ?? created.AddMinutes(5),
        UsedUtc = used, VoidedUtc = voided,
    };

    private static string H(char c) => new(c, 64);

    private static async Task<McpConfirmation[]> SaveAsync(TestDb db, params McpConfirmation[] rows)
    {
        var repo = new McpConfirmationRepository(db.Context);
        foreach (var r in rows) repo.Add(r);
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();
        return rows;
    }

    private static Task<McpConfirmation> ReloadAsync(TestDb db, long id) =>
        db.Context.Set<McpConfirmation>().AsNoTracking().SingleAsync(r => r.Id == id, Ct);

    [Fact]
    public async Task TryConsume_Once()
    {
        await using var db = await TestDb.CreateAsync();
        var row = (await SaveAsync(db, Row(H('a'), T0)))[0];
        var repo = new McpConfirmationRepository(db.Context);

        (await repo.TryConsumeAsync(row.Id, T0.AddMinutes(1), Ct)).Should().Be(1);
        (await repo.TryConsumeAsync(row.Id, T0.AddMinutes(2), Ct)).Should().Be(0);
        (await ReloadAsync(db, row.Id)).UsedUtc.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public async Task TryConsume_Expired_Zero()
    {
        await using var db = await TestDb.CreateAsync();
        var row = (await SaveAsync(db, Row(H('a'), T0)))[0];
        (await new McpConfirmationRepository(db.Context).TryConsumeAsync(row.Id, row.ExpiresUtc, Ct)).Should().Be(0);
        (await ReloadAsync(db, row.Id)).UsedUtc.Should().BeNull();
    }

    [Fact]
    public async Task TryConsume_Voided_Zero()
    {
        await using var db = await TestDb.CreateAsync();
        var row = (await SaveAsync(db, Row(H('a'), T0, voided: T0.AddSeconds(10))))[0];
        (await new McpConfirmationRepository(db.Context).TryConsumeAsync(row.Id, T0.AddMinutes(1), Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ListOpen_ExcludesUsedVoidedExpired_OrderedByCreated()
    {
        await using var db = await TestDb.CreateAsync();
        var now = T0.AddMinutes(3);
        await SaveAsync(db,
            Row(H('a'), T0.AddMinutes(2)),                                   // 開著（較晚）
            Row(H('b'), T0),                                                 // 開著（較早）
            Row(H('c'), T0, used: T0.AddMinutes(1)),
            Row(H('d'), T0, voided: T0.AddMinutes(1)),
            Row(H('e'), T0.AddMinutes(-10)),                                 // 已過期
            Row(H('f'), T0, tokenId: 2));                                    // 別的權杖
        var open = await new McpConfirmationRepository(db.Context).ListOpenTrackedAsync(1, now, Ct);
        open.Select(r => r.CodeHash).Should().Equal(H('b'), H('a'));
        db.Context.Entry(open[0]).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task CodeHash_Unique()
    {
        await using var db = await TestDb.CreateAsync();
        await SaveAsync(db, Row(H('a'), T0));
        new McpConfirmationRepository(db.Context).Add(Row(H('a'), T0, tokenId: 2));
        await db.Context.Invoking(c => c.SaveChangesAsync(Ct)).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task FindByHashTracked_Exact()
    {
        await using var db = await TestDb.CreateAsync();
        await SaveAsync(db, Row(H('a'), T0));
        var repo = new McpConfirmationRepository(db.Context);
        var row = await repo.FindByHashTrackedAsync(H('a'), Ct);
        row.Should().NotBeNull();
        db.Context.Entry(row!).State.Should().Be(EntityState.Unchanged);
        (await repo.FindByHashTrackedAsync(H('A'), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task PurgeBatch_CountsFromEndTime()
    {
        await using var db = await TestDb.CreateAsync();
        var cutoff = T0;
        await SaveAsync(db,
            Row(H('a'), T0.AddDays(-5), used: cutoff.AddMinutes(-1)),                         // 已用、早於 cutoff → 刪
            Row(H('b'), T0.AddDays(-5), used: cutoff.AddMinutes(1)),                          // 已用、晚於 cutoff → 留（即使早就過期）
            Row(H('c'), T0.AddDays(-5), voided: cutoff.AddMinutes(-1)),                       // 作廢早於 cutoff → 刪
            Row(H('d'), T0.AddDays(-5), voided: cutoff.AddMinutes(1)),                        // 作廢晚於 cutoff → 留
            Row(H('e'), T0.AddDays(-5), expires: cutoff.AddMinutes(-1)),                      // 未用、過期早於 cutoff → 刪
            Row(H('f'), T0.AddDays(-5), expires: cutoff.AddMinutes(1)));                      // 未用、過期晚於 cutoff → 留
        var repo = new McpConfirmationRepository(db.Context);

        var batch = await repo.ListPurgeBatchTrackedAsync(cutoff, 10, Ct);
        batch.Select(r => r.CodeHash).Should().Equal(H('a'), H('c'), H('e'));
        (await repo.ListPurgeBatchTrackedAsync(cutoff, 2, Ct)).Should().HaveCount(2);
        repo.RemoveRange(batch);
        await db.Context.SaveChangesAsync(Ct);
        (await db.Context.Set<McpConfirmation>().CountAsync(Ct)).Should().Be(3);
    }
}
