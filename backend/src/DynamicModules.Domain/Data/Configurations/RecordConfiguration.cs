using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class RecordConfiguration : IEntityTypeConfiguration<Record>
{
    public void Configure(EntityTypeBuilder<Record> b)
    {
        b.ToTable("Dynamic_Record");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.OwnerUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.ValuesJson).IsRequired();
        b.Property(x => x.RowVersion).HasMaxLength(36).IsRequired().IsConcurrencyToken();
        b.Property(x => x.HostKey).HasMaxLength(100);
        b.HasIndex(x => new { x.ModuleId, x.DeleteFlag });
        b.HasIndex(x => new { x.ModuleId, x.HostKey }).IsUnique()
            .HasFilter("\"HostKey\" IS NOT NULL AND \"DeleteFlag\" = 0");
        b.ConfigureAudit();
        b.HasMany(x => x.Index).WithOne().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
    }
}
