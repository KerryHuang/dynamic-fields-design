using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>寫入迴圈的計數（mcp2a 計畫判斷 8）：只算指定權杖、目標、動作、時間之後、成功且沒有原因碼的列。雙跑。</summary>
[Trait("Category", "Postgres")]
public class AuditLogWriteCountTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 1, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string[] Writes = ["mcp.updateRecord", "mcp.submitApproval"];

    private static AuditLogEntry Row(long? tokenId, string action, DateTime at, string? kind = "record", string? id = "42",
        AuditResult result = AuditResult.Success, string? reason = null) => new()
    {
        OccurredUtc = at, ActorUserId = "amy", Channel = AuditChannel.Mcp, TokenId = tokenId, Action = action, Result = result,
        RiskLevel = AuditRiskLevel.Write, TargetKind = kind, TargetId = id, ReasonCode = reason,
    };

    [Fact]
    public async Task CountWritesForTargetSince_OnlyTokenTargetActionsTimeSuccessWithoutReason()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new AuditLogRepository(db.Context);
        repo.Add(Row(7, "mcp.updateRecord", T0));                                         // 算（since 含）
        repo.Add(Row(7, "mcp.submitApproval", T0.AddMinutes(3)));                          // 算
        repo.Add(Row(7, "mcp.updateRecord", T0.AddSeconds(-1)));                          // 時間窗之前
        repo.Add(Row(8, "mcp.updateRecord", T0.AddMinutes(1)));                           // 別的權杖
        repo.Add(Row(null, "mcp.updateRecord", T0.AddMinutes(1)));                        // 畫面通道
        repo.Add(Row(7, "mcp.updateRecord", T0.AddMinutes(1), id: "43"));                 // 別筆
        repo.Add(Row(7, "mcp.updateRecord", T0.AddMinutes(1), kind: "task"));             // 別的目標種類
        repo.Add(Row(7, "mcp.createTask", T0.AddMinutes(1)));                             // 不在清單
        repo.Add(Row(7, "mcp.updateRecord", T0.AddMinutes(1), result: AuditResult.Invalid, reason: "stale"));   // 失敗
        repo.Add(Row(7, "mcp.updateRecord", T0.AddMinutes(1), result: AuditResult.Denied, reason: "write_loop")); // 被擋
        repo.Add(Row(7, "mcp.updateRecord", T0.AddMinutes(1), reason: "replayed"));       // 回放
        repo.Add(Row(7, "mcp.submitApproval", T0.AddMinutes(1), reason: "preview"));      // 預覽
        await db.Context.SaveChangesAsync(Ct);

        (await repo.CountWritesForTargetSinceAsync(7, "record", "42", T0, Writes, Ct)).Should().Be(2);
        (await repo.CountWritesForTargetSinceAsync(7, "record", "42", T0, [], Ct)).Should().Be(0);
    }
}
