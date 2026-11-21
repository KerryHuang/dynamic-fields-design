using System.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 兩條連線直接呼叫儲存庫（無鎖模式、READ COMMITTED，票 #24）：條件更新遇到另一個還沒提交的交易改了同一列時，
/// 會卡在列鎖上，等對方提交後以新值重新比對條件、回 0 列（簽核處理權的重試依賴這個行為）；對方回滾則照常成功。
/// </summary>
[Trait("Category", "Postgres")]
public class ApprovalRepositoryInterleavingTests : PgInterleavingTestBase
{
    protected override bool Unserialized => true;

    private async Task<(PurchaseRig R, ApprovalFlow Flow, ApprovalInstance Inst)> Seed()
    {
        var r = await BuildPurchase();
        var flow = await SeedFlow(r, "po-s", r.Confirmed, 1, Linear("fin1"));
        return (r, flow, await SeedInstance(r, flow));
    }

    [PostgresFact]
    public async Task TryBumpRowVersion_SecondWaitsForFirstCommit_ThenReturnsFalse_AndRereadRetrySucceeds()
    {
        var (_, _, inst) = await Seed();
        await using var c1 = NewContext();
        await using var c2 = NewContext();
        await using var t1 = await c1.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var t2 = await c2.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var repo1 = new ApprovalInstanceRepository(c1);
        var repo2 = new ApprovalInstanceRepository(c2);
        var seen = inst.RowVersion;

        (await repo1.TryBumpRowVersionAsync(inst.Id, seen, "first", default)).Should().BeTrue();
        var second = repo2.TryBumpRowVersionAsync(inst.Id, seen, "second", default);
        await WaitUntilSomeoneBlocksOnALockAsync();

        second.IsCompleted.Should().BeFalse("第一個交易還沒提交，第二個條件更新要卡在列鎖上，而不是直接回 0 列或覆蓋");
        await t1.CommitAsync();
        (await Within(second)).Should().BeFalse("第一個提交之後，第二個以新值重新比對條件：舊版本已不在，0 列");
        Inst(inst.Id).RowVersion.Should().Be("first");

        // 重試的做法（ClaimAsync）：同一個交易內重新讀，READ COMMITTED 看得到對方已提交的新值，條件更新成功
        (await repo2.GetRowVersionAsync(inst.Id, default)).Should().Be("first");
        (await repo2.TryBumpRowVersionAsync(inst.Id, "first", "second", default)).Should().BeTrue();
        await t2.CommitAsync();
        Inst(inst.Id).RowVersion.Should().Be("second");
    }

    [PostgresFact]
    public async Task TryBumpRowVersion_FirstRollsBack_SecondProceedsAndSucceeds()
    {
        var (_, _, inst) = await Seed();
        await using var c1 = NewContext();
        await using var c2 = NewContext();
        await using var t1 = await c1.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var t2 = await c2.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var seen = inst.RowVersion;

        (await new ApprovalInstanceRepository(c1).TryBumpRowVersionAsync(inst.Id, seen, "first", default)).Should().BeTrue();
        var second = new ApprovalInstanceRepository(c2).TryBumpRowVersionAsync(inst.Id, seen, "second", default);
        await WaitUntilSomeoneBlocksOnALockAsync();
        await t1.RollbackAsync();

        (await Within(second)).Should().BeTrue("第一個回滾，舊版本還在，第二個照常取得");
        await t2.CommitAsync();
        Inst(inst.Id).RowVersion.Should().Be("second");
    }
}
