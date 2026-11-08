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
        b.Property(x => x.TextTruncated).HasDefaultValue(false);
        // 報表的資料狀態探測只走這兩種罕見的壞列（截斷的文字、沒有精確整數值的數字），部分索引讓探測成本跟壞列數有關、跟資料量無關
        b.HasIndex(x => new { x.FieldId, x.TextTruncated }).HasFilter("TextTruncated = 1").HasDatabaseName("IX_Dynamic_RecordIndex_Truncated");
        b.HasIndex(x => x.FieldId).HasFilter("ScaledValue IS NULL AND NumberValue IS NOT NULL").HasDatabaseName("IX_Dynamic_RecordIndex_NoScaled");
        b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Restrict);
    }
}
