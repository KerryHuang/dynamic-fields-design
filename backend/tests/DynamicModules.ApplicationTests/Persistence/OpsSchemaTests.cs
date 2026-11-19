using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// A01（維運設計 §17.8）：O2 的九張表與核心寄信設定套 migration 後存在，欄位（可空與長度）、唯一索引與 §17.4 逐欄一致。
/// 這裡把 §17.4 寫成表、模型一變就失敗（o2e 依使用者 2026-11-19 裁決把告警通道的 SMTP 連線抽到 <c>Dynamic_MailSettings</c>）。兩種資料庫都跑（PostgreSQL 的建表由樣板資料庫套 migration）。
/// </summary>
[Trait("Category", "Postgres")]
public class OpsSchemaTests
{
    // 欄位：名稱、可空、長度（null＝沒宣告）
    private static readonly Dictionary<string, (string Name, bool Nullable, int? Max)[]> Expected = new()
    {
        ["Dynamic_AuditLog"] =
        [
            ("Id", false, null), ("OccurredUtc", false, null), ("ActorUserId", true, 50), ("ActorKind", false, 20), ("Channel", false, 20),
            ("TokenId", true, null), ("Action", false, 100), ("RiskLevel", false, 20), ("TargetKind", true, 30), ("TargetId", true, 100),
            ("TargetLabel", true, 200), ("ArgumentsHash", true, 64), ("ResultCount", true, null), ("SummaryJson", true, 16000),
            ("ConfirmationId", true, null), ("ConfirmationOutcome", true, 20), ("Result", false, 20), ("ReasonCode", true, 50),
            ("RequestId", true, 32), ("ClientIp", true, 45),
        ],
        ["Dynamic_OpsIncident"] =
        [
            ("Id", false, null), ("Fingerprint", false, 16), ("FingerprintVersion", false, null), ("Source", false, 20), ("Category", false, 20),
            ("Severity", false, 20), ("Status", false, 20), ("Code", false, 100), ("TitleKey", false, 100), ("TitleParamsJson", true, 2000),
            ("NormalizedMessage", true, 200), ("ModuleKey", true, 100), ("RuleKind", true, 20), ("RuleId", true, null), ("Route", true, 200),
            ("OccurrenceCount", false, null), ("FirstSeenUtc", false, null), ("LastSeenUtc", false, null), ("FirstVersion", false, 50),
            ("LastVersion", false, 50), ("LastRequestId", true, 32), ("Regression", false, null), ("ReopenCount", false, null),
            ("AcknowledgedBy", true, 50), ("AcknowledgedUtc", true, null), ("ResolvedBy", true, 50), ("ResolvedUtc", true, null),
            ("ResolvedReason", true, 20), ("LastNotifiedUtc", true, null),
        ],
        ["Dynamic_OpsIncidentSample"] =
        [
            ("Id", false, null), ("IncidentId", false, null), ("OccurredUtc", false, null), ("RequestId", true, 32), ("UserId", true, 50),
            ("Version", true, 50), ("Route", true, 200), ("AuditLogId", true, null), ("RawMessage", true, 500), ("RawStack", true, 4000),
            ("Browser", true, 200), ("Locale", true, 20),
        ],
        ["Dynamic_HealthCheck"] =
        [
            ("Code", false, 50), ("Enabled", true, null), ("IntervalMinutes", true, null), ("WarnThreshold", true, null),
            ("CriticalThreshold", true, null), ("ModifiedUserId", true, 50), ("ModifiedUtc", true, null), ("LastProbeUtc", true, null),
            ("LastAggregatedUtc", true, null), ("LastCheckedUtc", true, null), ("LastStatus", true, 20), ("LastValue", true, null),
            ("LastMessageCode", true, 50), ("NextDueUtc", false, null),
        ],
        ["Dynamic_HealthCheckResult"] =
        [
            ("Id", false, null), ("Code", false, 50), ("CheckedUtc", false, null), ("Status", false, 20), ("Value", true, null),
            ("MessageCode", true, 50), ("DurationMs", false, null), ("InstanceId", false, 100), ("TransitionId", false, 32),
        ],
        ["Dynamic_RuleHealth"] =
        [
            ("RuleKind", false, 20), ("RuleId", false, null), ("ConsecutiveFailures", false, null), ("StreakResetUtc", true, null),
            ("LastFailureUtc", true, null), ("LastErrorCode", true, 100), ("LastRequestId", true, 32), ("PausedUtc", true, null),
            ("LastPausedUtc", true, null), ("PauseCount", false, null), ("SkippedWhilePaused", false, null), ("ResumedUtc", true, null),
            ("ResumedBy", true, 50),
        ],
        ["Dynamic_RuleFailure"] =
        [
            ("Id", false, null), ("RuleKind", false, 20), ("RuleId", false, null), ("FailedUtc", false, null), ("RequestId", true, 32),
        ],
        ["Dynamic_AlertChannel"] =
        [
            ("Kind", false, 20), ("Enabled", false, null),
            ("ToAddresses", true, 2000), ("MailLocale", true, 10), ("LastClaimUtc", true, null), ("LastSuccessUtc", true, null),
            ("LastFailureUtc", true, null), ("LastError", true, 500), ("ConsecutiveFailures", false, null), ("ModifiedUserId", true, 50),
            ("ModifiedUtc", true, null),
        ],
        // 2026-11-19 裁決：SMTP 連線從告警通道抽到核心寄信設定（帳號信共用），長度沿用原欄位
        ["Dynamic_MailSettings"] =
        [
            ("Kind", false, 20), ("Host", true, 255), ("Port", false, null), ("Security", false, 20), ("UserName", true, 255),
            ("PasswordProtected", true, null), ("FromAddress", true, 255), ("FromName", true, 100), ("ModifiedUserId", true, 50),
            ("ModifiedUtc", true, null),
        ],
        ["Dynamic_AlertOutbox"] =
        [
            ("Id", false, null), ("Channel", false, 20), ("IncidentId", true, null), ("Transition", false, 20), ("TransitionId", true, 32),
            ("Subject", false, 200), ("Body", false, 4000), ("CreatedUtc", false, null), ("NextAttemptUtc", false, null),
            ("Attempts", false, null), ("Status", false, 20), ("ClaimedBy", true, 100), ("ClaimedUntilUtc", true, null),
            ("DigestOutboxId", true, null), ("LastError", true, 500), ("SentUtc", true, null),
        ],
    };

    [Fact]
    public async Task OpsTables_ExistAfterMigration_AndColumnsMatchDesign()
    {
        await using var db = await TestDb.CreateAsync();
        foreach (var (table, columns) in Expected)
        {
            // 表真的建出來了（空表）
            var sql = "SELECT COUNT(*) AS \"Value\" FROM \"" + table + "\"";   // 表名來自上面的常數表
            (await db.Context.Database.SqlQueryRaw<int>(sql).SingleAsync()).Should().Be(0, table);

            var entity = db.Context.Model.GetEntityTypes().Single(e => e.GetTableName() == table);
            var actual = entity.GetProperties().Select(p => (p.GetColumnName(), p.IsNullable, p.GetMaxLength())).OrderBy(c => c.Item1).ToList();
            actual.Should().BeEquivalentTo(columns.OrderBy(c => c.Name).ToList(), o => o.WithStrictOrdering(), table);
            // 時間欄位一律以 Utc 結尾
            entity.GetProperties().Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(DateTime))
                .Should().OnlyContain(p => p.Name.EndsWith("Utc"), table);
        }
    }

    [Fact]
    public void UniqueIndexes_AreFullTable_WithoutFilters()
    {
        using var context = new Domain.Data.DynamicDbContext(new DbContextOptionsBuilder<Domain.Data.DynamicDbContext>().UseSqlite("Data Source=:memory:").Options);
        string[] Unique<T>() => [.. context.Model.FindEntityType(typeof(T))!.GetIndexes().Where(i => i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)))];
        Unique<OpsIncident>().Should().Equal("Fingerprint");
        Unique<HealthCheckResult>().Should().Equal("TransitionId");
        Unique<AuditLogEntry>().Should().BeEmpty();
        Unique<AlertOutbox>().Should().BeEmpty("寄送佇列的 TransitionId 不設唯一（§17.4）");
        foreach (var table in Expected.Keys)
            context.Model.GetEntityTypes().Single(e => e.GetTableName() == table).GetIndexes().Where(i => i.GetFilter() != null).Should().BeEmpty(table);
        // 樣本對事件刪除串聯；其他表不設外鍵
        context.Model.FindEntityType(typeof(OpsIncidentSample))!.GetForeignKeys().Should().ContainSingle()
            .Which.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        foreach (var t in new[] { typeof(AuditLogEntry), typeof(RuleHealth), typeof(RuleFailure), typeof(AlertOutbox), typeof(HealthCheckResult), typeof(MailSettings) })
            context.Model.FindEntityType(t)!.GetForeignKeys().Should().BeEmpty(t.Name);
    }

    [Fact]
    public async Task Enums_AreStoredAsCamelCase_AndReadBackAsUtc()
    {
        await using var db = await TestDb.CreateAsync();
        var at = new DateTime(2026, 11, 19, 1, 2, 3, DateTimeKind.Utc);
        db.Context.Add(new AuditLogEntry
        {
            OccurredUtc = at, ActorKind = AuditActorKind.OpsService, Channel = AuditChannel.OpsCenter, Action = "x.y",
            RiskLevel = AuditRiskLevel.HighRisk, Result = AuditResult.Success, ConfirmationOutcome = AuditConfirmationOutcome.Mismatch,
        });
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        var raw = await db.Context.Database.SqlQueryRaw<string>(
            "SELECT \"ActorKind\" || '|' || \"Channel\" || '|' || \"RiskLevel\" || '|' || \"ConfirmationOutcome\" AS \"Value\" FROM \"Dynamic_AuditLog\"").SingleAsync();
        raw.Should().Be("opsService|opsCenter|highRisk|mismatch");
        var back = await db.Context.Set<AuditLogEntry>().SingleAsync(a => a.Channel == AuditChannel.OpsCenter);
        back.OccurredUtc.Should().Be(at);
        back.OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
        back.ActorKind.Should().Be(AuditActorKind.OpsService);
    }
}
