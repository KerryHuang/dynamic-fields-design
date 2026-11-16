using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class RecordHistoryConfiguration : IEntityTypeConfiguration<RecordHistory>
{
    public void Configure(EntityTypeBuilder<RecordHistory> b)
    {
        b.ToTable("Dynamic_RecordHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.Operation).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.ChangedUserId).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.RecordId, x.ChangedTime });
        b.HasOne(x => x.Record).WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Restrict);
    }
}
