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
        b.HasMany(x => x.Reports).WithOne().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.IntegrationsJson);
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

public class AppReportConfiguration : IEntityTypeConfiguration<AppReport>
{
    public void Configure(EntityTypeBuilder<AppReport> b)
    {
        b.ToTable("Dynamic_AppReport");
        b.HasKey(x => new { x.AppId, x.ReportId });
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.HasIndex(x => new { x.AppId, x.Code }).IsUnique();
        b.HasOne<Report>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppFieldConfiguration : IEntityTypeConfiguration<AppField>
{
    public void Configure(EntityTypeBuilder<AppField> b)
    {
        b.ToTable("Dynamic_AppField");
        b.HasKey(x => new { x.AppId, x.Name });
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.FieldId);
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Field>().WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppOptionConfiguration : IEntityTypeConfiguration<AppOption>
{
    public void Configure(EntityTypeBuilder<AppOption> b)
    {
        b.ToTable("Dynamic_AppOption");
        b.HasKey(x => new { x.AppId, x.Name });
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.OptionId);
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<FieldOption>().WithMany().HasForeignKey(x => x.OptionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppBlockConfiguration : IEntityTypeConfiguration<AppBlock>
{
    public void Configure(EntityTypeBuilder<AppBlock> b)
    {
        b.ToTable("Dynamic_AppBlock");
        b.HasKey(x => new { x.AppId, x.Name });
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.BlockId);
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Block>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class AppIntegrationConfiguration : IEntityTypeConfiguration<AppIntegration>
{
    public void Configure(EntityTypeBuilder<AppIntegration> b)
    {
        b.ToTable("Dynamic_AppIntegration");
        b.HasKey(x => new { x.AppId, x.Code });
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(2000);
        b.Property(x => x.ItemsJson).IsRequired();
        b.HasOne<App>().WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
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
