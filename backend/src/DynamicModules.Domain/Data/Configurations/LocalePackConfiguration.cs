using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

/// <summary>
/// 覆寫層版本（語系包設計 §6.4、§22；i4d1 計畫判斷 7）。不設 <c>(Code, IsCurrent)</c> 唯一索引：<c>AddVersion</c> 在同一個 <c>SaveChanges</c>
/// 把舊的目前版標成上一版、再加新的目前版，PostgreSQL 逐列檢查唯一索引可能在中途撞；完整性由 repository 的方法保證。JSON 欄不設長度（text）。
/// </summary>
public class LocalePackConfiguration : IEntityTypeConfiguration<LocalePack>
{
    public void Configure(EntityTypeBuilder<LocalePack> b)
    {
        b.ToTable("Dynamic_LocalePack");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(35).IsRequired();
        b.HasIndex(x => x.Code);
        b.Property(x => x.UiJson).IsRequired();
        b.Property(x => x.ServerJson).IsRequired();
        b.Property(x => x.BaseHashesJson).IsRequired();
        b.Property(x => x.FileHash).HasMaxLength(64);
        b.Property(x => x.FileName).HasMaxLength(200);
        b.Property(x => x.PackVersion).HasMaxLength(50);
        b.Property(x => x.BuiltFor).HasMaxLength(20);
        b.Property(x => x.BaseLocale).HasMaxLength(35);
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.ImportedUserId).HasMaxLength(50);
        b.Property(x => x.ImportedUtc).AsUtc();
        b.Property(x => x.ModifiedUtc).AsUtc();
    }
}

/// <summary>語系包預覽暫存（語系包設計 §22；i4d1 計畫判斷 8）。<c>ExpiresUtc</c> 有索引給清除過期列用。</summary>
public class LocalePackPreviewConfiguration : IEntityTypeConfiguration<LocalePackPreview>
{
    public void Configure(EntityTypeBuilder<LocalePackPreview> b)
    {
        b.ToTable("Dynamic_LocalePackPreview");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(32);
        b.Property(x => x.UserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Code).HasMaxLength(35).IsRequired();
        b.Property(x => x.FileHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PlanJson).IsRequired();
        b.Property(x => x.ReportJson).IsRequired();
        b.Property(x => x.LocaleFingerprint).HasMaxLength(64);
        b.Property(x => x.CreatedUtc).AsUtc();
        b.Property(x => x.ExpiresUtc).AsUtc();
        b.HasIndex(x => x.ExpiresUtc);
    }
}
