using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

/// <summary>已安裝語系（語系包設計 §4.1）。<c>FallbackCode</c> 不設指向自己的外鍵（完整性由 i4d1 的安裝驗證與移除守衛負責，§4.5）。</summary>
public class LocaleConfiguration : IEntityTypeConfiguration<InstalledLocale>
{
    public void Configure(EntityTypeBuilder<InstalledLocale> b)
    {
        b.ToTable("Dynamic_Locale");
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(35);
        b.Property(x => x.NativeName).HasMaxLength(50).IsRequired();
        b.Property(x => x.QuasarLang).HasMaxLength(20);
        b.Property(x => x.FallbackCode).HasMaxLength(35);
        b.Property(x => x.Origin).AsCamelText();
        b.Property(x => x.CreatedUserId).HasMaxLength(50);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.ModifiedUserId).HasMaxLength(50);
        b.Property(x => x.ModifiedUtc).AsUtc();
    }
}
