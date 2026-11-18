using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DynamicModules.Domain.Data.Configurations;

/// <summary>排程工作表（維運設計 §10.4）：主鍵是工作代碼；讀回的時間一律標成 Kind=Utc（SQLite 存文字不帶 Kind）。</summary>
public class ScheduledJobConfiguration : IEntityTypeConfiguration<ScheduledJob>
{
    private static readonly ValueConverter<DateTime, DateTime> Utc =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> UtcNullable =
        new(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    public void Configure(EntityTypeBuilder<ScheduledJob> b)
    {
        b.ToTable("Dynamic_ScheduledJob");
        b.HasKey(x => x.JobKey);
        b.Property(x => x.JobKey).HasMaxLength(100);
        b.Property(x => x.LeaseOwner).HasMaxLength(100);
        b.Property(x => x.LastStatus).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.LastError).HasMaxLength(ScheduledJob.LastErrorMax);
        b.Property(x => x.LeaseUntilUtc).HasConversion(UtcNullable);
        b.Property(x => x.LastStartedUtc).HasConversion(UtcNullable);
        b.Property(x => x.LastFinishedUtc).HasConversion(UtcNullable);
        b.Property(x => x.NextDueUtc).HasConversion(Utc);
    }
}
