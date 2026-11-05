using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class RecordIndexConfiguration : IEntityTypeConfiguration<RecordIndex>
{
    public void Configure(EntityTypeBuilder<RecordIndex> b)
    {
        b.ToTable("Dynamic_RecordIndex");
        b.HasKey(x => new { x.RecordId, x.FieldId, x.Seq });
        b.Property(x => x.TextValue).HasMaxLength(440);
        b.Property(x => x.RefKey).HasMaxLength(100);
        b.HasIndex(x => new { x.FieldId, x.TextValue });
        b.HasIndex(x => new { x.FieldId, x.NumberValue });
        b.HasIndex(x => new { x.FieldId, x.DateValue });
        b.HasIndex(x => new { x.FieldId, x.RefKey });
        b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Restrict);
    }
}
