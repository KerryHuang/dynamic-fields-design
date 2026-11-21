using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.McpService.Upkeep;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>確認碼與權杖的清理（mcp1a 計畫判斷 22、Task 9）：保留期從結束的時間算；沒用過的碼刪前寫 Expired 稽核。</summary>
[Trait("Category", "Postgres")]
public class McpUpkeepTests : McpTokenTestBase
{
    private const string JobKey = "ops.upkeep", Owner = "worker-1";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Db.Context.Add(new ScheduledJob { JobKey = JobKey, LeaseOwner = Owner, LeaseUntilUtc = NowUtc.AddHours(1), NextDueUtc = NowUtc });
        await Db.Context.SaveChangesAsync();
        As("");   // 排程工作沒有身分（行為者由清理覆寫成碼的擁有者）
    }

    private JobLeaseFence Fence(string owner = Owner) => new(JobKey, owner, NowUtc);

    private McpConfirmation AddCode(DateTime createdUtc, DateTime expiresUtc, DateTime? usedUtc = null, DateTime? voidedUtc = null)
    {
        var row = new McpConfirmation
        {
            CodeHash = McpSecrets.Hash(McpSecrets.NewConfirmationCode()), TokenId = 42, UserId = "wenhui", ToolName = "get_record",
            ArgumentsHash = new string('a', 64), TargetVersion = "v", CreatedUtc = createdUtc, ExpiresUtc = expiresUtc, UsedUtc = usedUtc,
            VoidedUtc = voidedUtc,
        };
        Db.Context.Add(row);
        Db.Context.SaveChanges();
        return row;
    }

    private Task<ServiceResult> PurgeCodes(string owner = Owner) =>
        Send(new PurgeMcpConfirmationsBatchRequest(Fence(owner), NowUtc.AddDays(-McpLimits.ConfirmationPurgeDays), 100));

    private List<long> RemainingCodes()
    {
        Db.Context.ChangeTracker.Clear();
        return Db.Context.Set<McpConfirmation>().AsNoTracking().Select(c => c.Id).ToList();
    }

    private List<AuditLogEntry> ExpiredRows() => Db.Context.Set<AuditLogEntry>().AsNoTracking()
        .Where(a => a.ConfirmationOutcome == AuditConfirmationOutcome.Expired).ToList();

    [Fact]
    public async Task ExpiredUnused_Deleted_WithExpiredAudit()
    {
        var row = AddCode(NowUtc.AddDays(-3), NowUtc.AddDays(-2));

        (await PurgeCodes()).Data.Should().Be(1);

        RemainingCodes().Should().BeEmpty();
        var audit = ExpiredRows().Should().ContainSingle().Subject;
        (audit.TokenId, audit.ActorUserId, audit.Channel, audit.ConfirmationId, audit.Action)
            .Should().Be((42L, "wenhui", AuditChannel.Mcp, row.Id, "mcp.getRecord"));
    }

    [Fact]
    public async Task Used_Deleted_NoExpired_Voided_DeletedWithExpired()
    {
        AddCode(NowUtc.AddDays(-3), NowUtc.AddDays(1), usedUtc: NowUtc.AddDays(-2));
        AddCode(NowUtc.AddDays(-3), NowUtc.AddDays(1), voidedUtc: NowUtc.AddDays(-2));

        (await PurgeCodes()).Data.Should().Be(2);

        RemainingCodes().Should().BeEmpty();
        ExpiredRows().Should().ContainSingle("只有作廢的那個沒用過");
    }

    [Fact]
    public async Task RetentionFromEnd_NotCreation()
    {
        var expiredRecently = AddCode(NowUtc.AddDays(-2), NowUtc.AddHours(-12));
        var usedRecently = AddCode(NowUtc.AddDays(-2), NowUtc.AddDays(1), usedUtc: NowUtc.AddHours(-12));

        (await PurgeCodes()).Data.Should().Be(0);

        RemainingCodes().Should().BeEquivalentTo([expiredRecently.Id, usedRecently.Id]);
        ExpiredRows().Should().BeEmpty();
    }

    [Fact]
    public async Task Tokens_RevokedOrExpiredBeforeCutoff_Deleted()
    {
        var oldRevoked = AddToken("wenhui", "舊的", NowUtc.AddDays(30), revokedUtc: NowUtc.AddDays(-400));
        var oldExpired = AddToken("wenhui", "過期", NowUtc.AddDays(-400));
        var recentRevoked = AddToken("wenhui", "近期撤銷", NowUtc.AddDays(30), revokedUtc: NowUtc.AddDays(-1));
        var active = AddToken("wenhui", "使用中", NowUtc.AddDays(30));

        (await Send(new PurgeMcpTokensBatchRequest(Fence(), NowUtc.AddDays(-365), 100))).Data.Should().Be(2);

        Db.Context.ChangeTracker.Clear();
        Db.Context.Set<McpToken>().AsNoTracking().Select(t => t.Id).ToList()
            .Should().BeEquivalentTo([recentRevoked.Id, active.Id]).And.NotContain([oldRevoked.Id, oldExpired.Id]);
    }

    [Fact]
    public async Task LeaseLost_NoWrites()
    {
        AddCode(NowUtc.AddDays(-3), NowUtc.AddDays(-2));
        AddToken("wenhui", "舊的", NowUtc.AddDays(-400));

        (await PurgeCodes(owner: "someone-else")).Code.Should().Be(JobLeaseFence.LostCode);
        (await Send(new PurgeMcpTokensBatchRequest(Fence("someone-else"), NowUtc.AddDays(-365), 100))).Code.Should().Be(JobLeaseFence.LostCode);

        RemainingCodes().Should().HaveCount(1);
        ExpiredRows().Should().BeEmpty();
        Db.Context.Set<McpToken>().AsNoTracking().Should().HaveCount(1);
    }
}
