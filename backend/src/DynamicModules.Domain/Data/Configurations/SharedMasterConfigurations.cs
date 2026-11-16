using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

// 共用主檔的四張新表與使用關係（設計 §8）。列舉照專案慣例存成名稱字串（App、Master、Manual），
// 所以檢查約束比對的是 'Manual'。檢查約束的布林運算是 SQLite 寫法，換資料庫時依 provider 改寫。

public class AppMasterUseConfiguration : IEntityTypeConfiguration<AppMasterUse>
{
    public void Configure(EntityTypeBuilder<AppMasterUse> b)
    {
        b.ToTable("Dynamic_AppMasterUse");
        b.HasKey(x => new { x.AppId, x.MasterAppId });
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<App>().WithMany().HasForeignKey(x => x.MasterAppId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AppGrantConfiguration : IEntityTypeConfiguration<AppGrant>
{
    public void Configure(EntityTypeBuilder<AppGrant> b)
    {
        b.ToTable("Dynamic_AppGrant", t => t.HasCheckConstraint("CK_AppGrant_Source", "(\"SourceKind\" = 'Manual') = (\"AppId\" IS NULL)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.AppId, x.ModuleId, x.GroupId, x.Action }).IsUnique().HasFilter("\"AppId\" IS NOT NULL");
        b.HasIndex(x => new { x.ModuleId, x.GroupId, x.Action }).IsUnique().HasFilter("\"AppId\" IS NULL");
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PermissionGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ModuleActionScopeConfiguration : IEntityTypeConfiguration<ModuleActionScope>
{
    public void Configure(EntityTypeBuilder<ModuleActionScope> b)
    {
        b.ToTable("Dynamic_ModuleActionScope");
        b.HasKey(x => new { x.ModuleId, x.GroupId, x.Action });
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PermissionGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class FieldGrantConfiguration : IEntityTypeConfiguration<FieldGrant>
{
    public void Configure(EntityTypeBuilder<FieldGrant> b)
    {
        b.ToTable("Dynamic_FieldGrant", t => t.HasCheckConstraint("CK_FieldGrant_Source", "(\"SourceKind\" = 'Manual') = (\"AppId\" IS NULL)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceKind).HasConversion<string>().HasMaxLength(10);
        b.HasIndex(x => new { x.FieldId, x.GroupId, x.AppId }).IsUnique().HasFilter("\"AppId\" IS NOT NULL");
        b.HasIndex(x => new { x.FieldId, x.GroupId }).IsUnique().HasFilter("\"AppId\" IS NULL");
        b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PermissionGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppOrphanConfiguration : IEntityTypeConfiguration<AppOrphan>
{
    public void Configure(EntityTypeBuilder<AppOrphan> b)
    {
        b.ToTable("Dynamic_AppOrphan", t => t.HasCheckConstraint("CK_AppOrphan_One",
            "(\"FieldId\" IS NOT NULL) + (\"OptionId\" IS NOT NULL) + (\"BlockId\" IS NOT NULL) = 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.FormerAppKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.FormerAppKey, x.Kind, x.Name }).IsUnique();
        b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<FieldOption>().WithMany().HasForeignKey(x => x.OptionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Block>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Cascade);
    }
}
