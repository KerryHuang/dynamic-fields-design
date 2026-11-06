using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class AppConfiguration : IEntityTypeConfiguration<App>
{
    public void Configure(EntityTypeBuilder<App> b)
    {
        b.ToTable("Dynamic_App");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Icon).HasMaxLength(100);
        b.Property(x => x.PackageVersion).HasMaxLength(20).IsRequired();
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Requires).HasJsonConversion();
        b.Property(x => x.RowVersion).HasMaxLength(36).IsRequired().IsConcurrencyToken();
        b.HasMany(x => x.Modules).WithOne().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        b.ConfigureAudit();
    }
}

public class AppModuleConfiguration : IEntityTypeConfiguration<AppModule>
{
    public void Configure(EntityTypeBuilder<AppModule> b)
    {
        b.ToTable("Dynamic_AppModule");
        b.HasKey(x => new { x.AppId, x.Code });
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        // 一個模組最多屬於一個應用程式
        b.HasIndex(x => x.ModuleId).IsUnique();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppPackageConfiguration : IEntityTypeConfiguration<AppPackage>
{
    public void Configure(EntityTypeBuilder<AppPackage> b)
    {
        b.ToTable("Dynamic_AppPackage");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.Version).HasMaxLength(20).IsRequired();
        b.Property(x => x.Json).IsRequired();
        b.ConfigureAudit();
    }
}
