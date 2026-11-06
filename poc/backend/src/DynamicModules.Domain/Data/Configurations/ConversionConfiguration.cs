using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class ConversionConfiguration : IEntityTypeConfiguration<Conversion>
{
    public void Configure(EntityTypeBuilder<Conversion> b)
    {
        b.ToTable("Dynamic_Conversion");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Definition).HasJsonConversion();
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => x.SourceModuleId);
        b.ConfigureAudit();
        // 兩條外鍵都不級聯（避免多重級聯路徑）；刪除模組前由安全刪除盤點擋下
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.SourceModuleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.TargetModuleId).OnDelete(DeleteBehavior.Restrict);
    }
}
