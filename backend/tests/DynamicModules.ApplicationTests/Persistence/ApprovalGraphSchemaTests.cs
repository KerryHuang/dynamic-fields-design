using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>migration ApprovalGraph（流程圖設計 §7.6）：新欄位全部可為空、既有資料升級後原樣保留。</summary>
public class ApprovalGraphSchemaTests
{
    [Fact]
    public async Task ApprovalGraphMigration_AddsColumns()
    {
        await using var db = await TestDb.CreateAsync();
        var expected = new Dictionary<string, string[]>
        {
            ["Dynamic_ApprovalFlowVersion"] = ["LayoutJson"],
            ["Dynamic_ApprovalFlow"] = ["DraftJson", "DraftUpdatedBy", "DraftUpdatedTime"],
            ["Dynamic_ApprovalStageRun"] = ["NodeKind", "InEdgeId", "CloseReason"],
            ["Dynamic_Notification"] = ["DataJson"],
        };
        foreach (var (table, columns) in expected)
        {
            foreach (var column in columns)
            {
                var notNull = await db.Context.Database
                    .SqlQuery<int>($"SELECT \"notnull\" AS Value FROM pragma_table_info({table}) WHERE name = {column}")
                    .ToListAsync();
                notNull.Should().Equal([0], $"{table}.{column} 要存在而且可為空");
            }
        }
    }

    [Fact]
    public async Task ApprovalGraphMigration_KeepsExistingStageRunsAndFlows()
    {
        await using var db = await TestDb.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync("RemoveMockCustomer");
        var sql = db.Context.Database;
        await sql.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await sql.ExecuteSqlRawAsync(
            "INSERT INTO Dynamic_ApprovalFlow (Id, Key, NameI18n, ModuleId, ToOptionId, Priority, Active, CurrentVersionId, CreatedUserId, CreatedTime) " +
            "VALUES (7, 'old-flow', '{{\"zh-TW\":\"舊流程\"}}', 1, 2, 3, 1, 70, 'u', '2026-01-01')");
        await sql.ExecuteSqlRawAsync(
            "INSERT INTO Dynamic_ApprovalFlowVersion (Id, FlowId, Version, Definition, CreatedBy, CreatedTime) " +
            "VALUES (70, 7, 1, '{{\"shape\":\"sequence\"}}', 'u', '2026-01-01')");
        await sql.ExecuteSqlRawAsync(
            "INSERT INTO Dynamic_ApprovalInstance (Id, RecordId, ModuleId, FlowSetSnapshot, RouteKind, FromOptionId, ToOptionId, InitiatorUserId, Status, TitleSnapshot, Picks, SubmittedTime, RowVersion, OperationId) " +
            "VALUES (9, 1, 1, '[]', 'flow', 1, 2, 'u', 'pending', 't', '{{}}', '2026-01-01', 'x', 'op')");
        await sql.ExecuteSqlRawAsync(
            "INSERT INTO Dynamic_ApprovalStageRun (Id, InstanceId, Seq, StageCode, NodeId, BranchKey, Mode, Status) " +
            "VALUES (11, 9, 1, 's1', 'n1', 'b1', 'any', 'active'), (12, 9, 2, NULL, NULL, NULL, 'all', 'queued')");
        await sql.ExecuteSqlRawAsync(
            "INSERT INTO Dynamic_Notification (Id, UserId, Kind, TitleSnapshot, Link, CreatedTime) VALUES (5, 'u', 'approvalPending', 't', '/x', '2026-01-01')");
        await sql.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");

        await migrator.MigrateAsync();

        var runs = await db.Context.Set<ApprovalStageRun>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        runs.Should().HaveCount(2);
        runs[0].Should().BeEquivalentTo(new { Id = 11L, InstanceId = 9L, Seq = 1, StageCode = "s1", NodeId = "n1", BranchKey = "b1", Mode = "any", Status = "active", NodeKind = (string?)null, InEdgeId = (string?)null, CloseReason = (string?)null });
        runs[1].Should().BeEquivalentTo(new { Id = 12L, StageCode = (string?)null, NodeId = (string?)null, BranchKey = (string?)null, Mode = "all", Status = "queued" });
        var flow = await db.Context.Set<ApprovalFlow>().AsNoTracking().SingleAsync();
        flow.Should().BeEquivalentTo(new { Id = 7L, Key = "old-flow", Priority = 3, CurrentVersionId = (long?)70, DraftJson = (string?)null, DraftUpdatedBy = (string?)null, DraftUpdatedTime = (DateTime?)null });
        (await db.Context.Set<ApprovalFlowVersion>().AsNoTracking().SingleAsync()).LayoutJson.Should().BeNull();
        (await db.Context.Set<Notification>().AsNoTracking().SingleAsync()).DataJson.Should().BeNull();
        (await sql.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_foreign_key_list('Dynamic_ApprovalStageRun')").SingleAsync()).Should().Be(1);
        (await sql.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_index_list('Dynamic_ApprovalStageRun') WHERE name LIKE 'IX_%'").SingleAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StageRun_BranchKey256_RoundTrips()
    {
        await using var db = await TestDb.CreateAsync();
        var branchKey = string.Concat(Enumerable.Repeat("b1/", 86))[..256];
        db.Context.Set<ApprovalInstance>().Add(new ApprovalInstance
        {
            RecordId = 1, ModuleId = 1, FlowSetSnapshot = [], FromOptionId = 1, ToOptionId = 2, InitiatorUserId = "u",
            TitleSnapshot = "t", RowVersion = "x", OperationId = "op", SubmittedTime = DateTime.UtcNow
        });
        await db.Context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await db.Context.SaveChangesAsync();
        var instanceId = db.Context.Set<ApprovalInstance>().Local.Single().Id;
        db.Context.Set<ApprovalStageRun>().Add(new ApprovalStageRun
        {
            InstanceId = instanceId, Seq = 1, BranchKey = branchKey, NodeKind = "approval", InEdgeId = "e1", CloseReason = "x"
        });
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        var run = await db.Context.Set<ApprovalStageRun>().AsNoTracking().SingleAsync();
        run.BranchKey.Should().Be(branchKey).And.HaveLength(256);
        (run.NodeKind, run.InEdgeId, run.CloseReason).Should().Be(("approval", "e1", "x"));
        db.Context.Model.FindEntityType(typeof(ApprovalStageRun))!.FindProperty(nameof(ApprovalStageRun.BranchKey))!.GetMaxLength().Should().Be(256);
    }
}
