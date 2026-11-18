using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DynamicModules.Domain.Data.Configurations;

/// <summary>
/// O2 的九張維運表（維運設計 §17.4）。時間一律 UTC、讀回標 Kind=Utc（與 <see cref="ScheduledJobConfiguration"/> 同一組轉換）；
/// 列舉存成小寫開頭的 camelCase 字串（例：<c>opsService</c>）；字串長度用 HasMaxLength 宣告（PostgreSQL 仍是 text）。
/// 沒有部分索引、沒有 provider 專屬的 filter 或預設值 SQL。
/// </summary>
internal static class OpsColumns
{
    public static PropertyBuilder<DateTime> AsUtc(this PropertyBuilder<DateTime> b) => b.HasConversion(ScheduledJobConfiguration.Utc);

    public static PropertyBuilder<DateTime?> AsUtc(this PropertyBuilder<DateTime?> b) => b.HasConversion(ScheduledJobConfiguration.UtcNullable);

    public static PropertyBuilder<T> AsCamelText<T>(this PropertyBuilder<T> b, int maxLength = 20) where T : struct, Enum =>
        b.HasConversion(new ValueConverter<T, string>(v => CamelEnum.Text(v), v => CamelEnum.Parse<T>(v))).HasMaxLength(maxLength);

    public static PropertyBuilder<T?> AsCamelText<T>(this PropertyBuilder<T?> b, int maxLength = 20) where T : struct, Enum =>
        b.HasConversion(new ValueConverter<T?, string?>(
            v => v.HasValue ? CamelEnum.Text(v.Value) : null,
            v => v == null ? null : CamelEnum.Parse<T>(v))).HasMaxLength(maxLength);
}

/// <summary>列舉與 camelCase 字串互轉（<c>OpsService</c> ↔ <c>opsService</c>）。API 與資料表用同一種寫法。</summary>
public static class CamelEnum
{
    public static string Text<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public static T Parse<T>(string text) where T : struct, Enum => Enum.Parse<T>(text, ignoreCase: true);

    /// <summary>只認 camelCase 寫法（區分大小寫），給 API 查詢參數用。</summary>
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        foreach (var v in Enum.GetValues<T>())
            if (string.Equals(Text(v), text, StringComparison.Ordinal)) { value = v; return true; }
        value = default;
        return false;
    }
}

public class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> b)
    {
        b.ToTable("Dynamic_AuditLog");
        b.HasKey(x => x.Id);
        b.Property(x => x.OccurredUtc).AsUtc();
        b.Property(x => x.ActorUserId).HasMaxLength(50);
        b.Property(x => x.ActorKind).AsCamelText();
        b.Property(x => x.Channel).AsCamelText();
        b.Property(x => x.Action).HasMaxLength(AuditLogEntry.ActionMax);
        b.Property(x => x.RiskLevel).AsCamelText();
        b.Property(x => x.TargetKind).HasMaxLength(AuditLogEntry.TargetKindMax);
        b.Property(x => x.TargetId).HasMaxLength(AuditLogEntry.TargetIdMax);
        b.Property(x => x.TargetLabel).HasMaxLength(AuditLogEntry.TargetLabelMax);
        b.Property(x => x.ArgumentsHash).HasMaxLength(64);
        b.Property(x => x.SummaryJson).HasMaxLength(AuditLogEntry.SummaryMax);
        b.Property(x => x.ConfirmationOutcome).AsCamelText();
        b.Property(x => x.Result).AsCamelText();
        b.Property(x => x.ReasonCode).HasMaxLength(AuditLogEntry.ReasonCodeMax);
        b.Property(x => x.RequestId).HasMaxLength(32);
        b.Property(x => x.ClientIp).HasMaxLength(45);
        b.HasIndex(x => x.OccurredUtc);
        b.HasIndex(x => new { x.ActorUserId, x.OccurredUtc });
        b.HasIndex(x => new { x.Action, x.OccurredUtc });
        b.HasIndex(x => new { x.TokenId, x.OccurredUtc });
        b.HasIndex(x => new { x.TargetKind, x.TargetId, x.OccurredUtc });
    }
}

public class OpsIncidentConfiguration : IEntityTypeConfiguration<OpsIncident>
{
    public void Configure(EntityTypeBuilder<OpsIncident> b)
    {
        b.ToTable("Dynamic_OpsIncident");
        b.HasKey(x => x.Id);
        b.Property(x => x.Fingerprint).HasMaxLength(16);
        b.Property(x => x.Source).AsCamelText();
        b.Property(x => x.Category).AsCamelText();
        b.Property(x => x.Severity).AsCamelText();
        b.Property(x => x.Status).AsCamelText();
        b.Property(x => x.Code).HasMaxLength(100);
        b.Property(x => x.TitleKey).HasMaxLength(100);
        b.Property(x => x.TitleParamsJson).HasMaxLength(OpsIncident.TitleParamsMax);
        b.Property(x => x.NormalizedMessage).HasMaxLength(OpsIncident.NormalizedMessageMax);
        b.Property(x => x.ModuleKey).HasMaxLength(100);
        b.Property(x => x.RuleKind).HasMaxLength(20);
        b.Property(x => x.Route).HasMaxLength(200);
        b.Property(x => x.FirstSeenUtc).AsUtc();
        b.Property(x => x.LastSeenUtc).AsUtc();
        b.Property(x => x.FirstVersion).HasMaxLength(50);
        b.Property(x => x.LastVersion).HasMaxLength(50);
        b.Property(x => x.LastRequestId).HasMaxLength(32);
        b.Property(x => x.AcknowledgedBy).HasMaxLength(50);
        b.Property(x => x.AcknowledgedUtc).AsUtc();
        b.Property(x => x.ResolvedBy).HasMaxLength(50);
        b.Property(x => x.ResolvedUtc).AsUtc();
        b.Property(x => x.ResolvedReason).AsCamelText();
        b.Property(x => x.LastNotifiedUtc).AsUtc();
        b.HasIndex(x => x.Fingerprint).IsUnique();
        b.HasIndex(x => new { x.Status, x.Severity, x.LastSeenUtc });
        b.HasIndex(x => new { x.Status, x.LastSeenUtc });
        b.HasIndex(x => x.ResolvedUtc);
    }
}

public class OpsIncidentSampleConfiguration : IEntityTypeConfiguration<OpsIncidentSample>
{
    public void Configure(EntityTypeBuilder<OpsIncidentSample> b)
    {
        b.ToTable("Dynamic_OpsIncidentSample");
        b.HasKey(x => x.Id);
        b.HasOne<OpsIncident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.OccurredUtc).AsUtc();
        b.Property(x => x.RequestId).HasMaxLength(32);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.Version).HasMaxLength(50);
        b.Property(x => x.Route).HasMaxLength(200);
        b.Property(x => x.RawMessage).HasMaxLength(OpsIncidentSample.RawMessageMax);
        b.Property(x => x.RawStack).HasMaxLength(OpsIncidentSample.RawStackMax);
        b.Property(x => x.Browser).HasMaxLength(200);
        b.Property(x => x.Locale).HasMaxLength(20);
        b.HasIndex(x => new { x.IncidentId, x.OccurredUtc });
        b.HasIndex(x => x.OccurredUtc);
    }
}

public class HealthCheckConfiguration : IEntityTypeConfiguration<HealthCheck>
{
    public void Configure(EntityTypeBuilder<HealthCheck> b)
    {
        b.ToTable("Dynamic_HealthCheck");
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.ModifiedUserId).HasMaxLength(50);
        b.Property(x => x.ModifiedUtc).AsUtc();
        b.Property(x => x.LastProbeUtc).AsUtc();
        b.Property(x => x.LastAggregatedUtc).AsUtc();
        b.Property(x => x.LastCheckedUtc).AsUtc();
        b.Property(x => x.LastStatus).AsCamelText();
        b.Property(x => x.LastMessageCode).HasMaxLength(50);
        b.Property(x => x.NextDueUtc).AsUtc();
    }
}

public class HealthCheckResultConfiguration : IEntityTypeConfiguration<HealthCheckResult>
{
    public void Configure(EntityTypeBuilder<HealthCheckResult> b)
    {
        b.ToTable("Dynamic_HealthCheckResult");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(50);
        b.Property(x => x.CheckedUtc).AsUtc();
        b.Property(x => x.Status).AsCamelText();
        b.Property(x => x.MessageCode).HasMaxLength(50);
        b.Property(x => x.InstanceId).HasMaxLength(100);
        b.Property(x => x.TransitionId).HasMaxLength(32);
        b.HasIndex(x => x.TransitionId).IsUnique();
        b.HasIndex(x => new { x.Code, x.CheckedUtc });
        b.HasIndex(x => new { x.Code, x.InstanceId, x.CheckedUtc });
        b.HasIndex(x => x.CheckedUtc);
    }
}

public class RuleHealthConfiguration : IEntityTypeConfiguration<RuleHealth>
{
    public void Configure(EntityTypeBuilder<RuleHealth> b)
    {
        b.ToTable("Dynamic_RuleHealth");
        b.HasKey(x => new { x.RuleKind, x.RuleId });
        b.Property(x => x.RuleKind).HasMaxLength(20);
        b.Property(x => x.StreakResetUtc).AsUtc();
        b.Property(x => x.LastFailureUtc).AsUtc();
        b.Property(x => x.LastErrorCode).HasMaxLength(100);
        b.Property(x => x.LastRequestId).HasMaxLength(32);
        b.Property(x => x.PausedUtc).AsUtc();
        b.Property(x => x.LastPausedUtc).AsUtc();
        b.Property(x => x.ResumedUtc).AsUtc();
        b.Property(x => x.ResumedBy).HasMaxLength(50);
    }
}

public class RuleFailureConfiguration : IEntityTypeConfiguration<RuleFailure>
{
    public void Configure(EntityTypeBuilder<RuleFailure> b)
    {
        b.ToTable("Dynamic_RuleFailure");
        b.HasKey(x => x.Id);
        b.Property(x => x.RuleKind).HasMaxLength(20);
        b.Property(x => x.FailedUtc).AsUtc();
        b.Property(x => x.RequestId).HasMaxLength(32);
        b.HasIndex(x => new { x.RuleKind, x.RuleId, x.FailedUtc });
        b.HasIndex(x => x.FailedUtc);
    }
}

public class AlertChannelConfiguration : IEntityTypeConfiguration<AlertChannel>
{
    public void Configure(EntityTypeBuilder<AlertChannel> b)
    {
        b.ToTable("Dynamic_AlertChannel");
        b.HasKey(x => x.Kind);
        b.Property(x => x.Kind).HasMaxLength(20);
        b.Property(x => x.Host).HasMaxLength(255);
        b.Property(x => x.Security).AsCamelText();
        b.Property(x => x.UserName).HasMaxLength(255);
        b.Property(x => x.FromAddress).HasMaxLength(255);
        b.Property(x => x.FromName).HasMaxLength(100);
        b.Property(x => x.ToAddresses).HasMaxLength(2000);
        b.Property(x => x.MailLocale).HasMaxLength(10);
        b.Property(x => x.LastClaimUtc).AsUtc();
        b.Property(x => x.LastSuccessUtc).AsUtc();
        b.Property(x => x.LastFailureUtc).AsUtc();
        b.Property(x => x.LastError).HasMaxLength(500);
        b.Property(x => x.ModifiedUserId).HasMaxLength(50);
        b.Property(x => x.ModifiedUtc).AsUtc();
    }
}

public class AlertOutboxConfiguration : IEntityTypeConfiguration<AlertOutbox>
{
    public void Configure(EntityTypeBuilder<AlertOutbox> b)
    {
        b.ToTable("Dynamic_AlertOutbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Channel).HasMaxLength(20);
        b.Property(x => x.Transition).AsCamelText();
        b.Property(x => x.TransitionId).HasMaxLength(32);
        b.Property(x => x.Subject).HasMaxLength(AlertOutbox.SubjectMax);
        b.Property(x => x.Body).HasMaxLength(AlertOutbox.BodyMax);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.NextAttemptUtc).AsUtc();
        b.Property(x => x.Status).AsCamelText();
        b.Property(x => x.ClaimedBy).HasMaxLength(100);
        b.Property(x => x.ClaimedUntilUtc).AsUtc();
        b.Property(x => x.LastError).HasMaxLength(500);
        b.Property(x => x.SentUtc).AsUtc();
        b.HasIndex(x => new { x.Status, x.NextAttemptUtc });
        b.HasIndex(x => x.SentUtc);
        b.HasIndex(x => x.CreatedUtc);
        b.HasIndex(x => x.DigestOutboxId);
    }
}
