using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class FieldConfiguration : IEntityTypeConfiguration<Field>
{
    public void Configure(EntityTypeBuilder<Field> b)
    {
        b.ToTable("Dynamic_Field");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(50).IsRequired();
        b.Property(x => x.LabelI18n).HasJsonConversion();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Config).HasJsonConversion();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.ModuleId, x.Key }).IsUnique();
        b.ConfigureAudit();
        // 不走 Module 的級聯（避免多重級聯路徑），由 Block 級聯刪除。
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.NoAction);
        b.HasMany(x => x.Options).WithOne().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
    }
}
