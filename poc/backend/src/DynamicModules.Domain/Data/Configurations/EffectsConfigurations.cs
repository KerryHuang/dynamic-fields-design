using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class ModuleEventConfiguration : IEntityTypeConfiguration<ModuleEvent>
{
    public void Configure(EntityTypeBuilder<ModuleEvent> b)
    {
        b.ToTable("Dynamic_Event");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Definition).HasJsonConversion();
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => x.SourceModuleId);
        b.ConfigureAudit();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.SourceModuleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class EventFactConfiguration : IEntityTypeConfiguration<EventFact>
{
    public void Configure(EntityTypeBuilder<EventFact> b)
    {
        b.ToTable("Dynamic_EventFact");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.EffectCode).HasMaxLength(40).IsRequired();
        b.Property(x => x.OperationId).HasMaxLength(64).IsRequired();
        // 同一事件的同一效果對同一來源（與明細列）最多生效一次：重複記帳的最後防線
        b.HasIndex(x => new { x.EventKey, x.EffectCode, x.SourceRecordId, x.ItemKey }).IsUnique();
        b.HasIndex(x => x.SourceRecordId);
        b.HasIndex(x => x.TargetRecordId);
    }
}

public class ModuleCheckConfiguration : IEntityTypeConfiguration<ModuleCheck>
{
    public void Configure(EntityTypeBuilder<ModuleCheck> b)
    {
        b.ToTable("Dynamic_Check");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Definition).HasJsonConversion();
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => x.ModuleId);
        b.ConfigureAudit();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ModuleLedgerConfiguration : IEntityTypeConfiguration<ModuleLedger>
{
    public void Configure(EntityTypeBuilder<ModuleLedger> b)
    {
        b.ToTable("Dynamic_Ledger");
        b.HasKey(x => x.Id);
        b.Property(x => x.Definition).HasJsonConversion();
        b.HasIndex(x => x.ModuleId).IsUnique();
        b.ConfigureAudit();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LedgerReversalConfiguration : IEntityTypeConfiguration<LedgerReversal>
{
    public void Configure(EntityTypeBuilder<LedgerReversal> b)
    {
        b.ToTable("Dynamic_LedgerReversal");
        b.HasKey(x => x.OriginalRecordId);
        b.Property(x => x.OriginalRecordId).ValueGeneratedNever();
        b.HasIndex(x => x.ReversalRecordId).IsUnique();
    }
}

public class OperationRecordConfiguration : IEntityTypeConfiguration<OperationRecord>
{
    public void Configure(EntityTypeBuilder<OperationRecord> b)
    {
        b.ToTable("Dynamic_Operation");
        b.HasKey(x => new { x.UserId, x.Key });
        b.Property(x => x.UserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Key).HasMaxLength(64).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.CreatedTime);
    }
}
