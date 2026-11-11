using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class ModuleConfiguration : IEntityTypeConfiguration<Module>
{
    public void Configure(EntityTypeBuilder<Module> b)
    {
        b.ToTable("Dynamic_Module");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.ProgramCode).HasMaxLength(8).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Icon).HasMaxLength(100);
        b.Property(x => x.Section).HasMaxLength(100);
        b.Property(x => x.SectionI18n).HasJsonNullableConversion();
        // 樣板的上限不量欄位寫法（TitleTemplate.MaxLength），存的是欄位 Id：照最長的 Id 算才放得下任何合法樣板
        b.Property(x => x.TitleTemplate).HasMaxLength(Modules.TitleTemplate.MaxStoredLength);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ModuleKind.Standalone);
        b.Property(x => x.HostEntityKey).HasMaxLength(100);
        b.Property(x => x.TasksEnabled).HasDefaultValue(true).HasSentinel(true);
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => x.HostEntityKey).IsUnique().HasFilter("\"HostEntityKey\" IS NOT NULL");
        b.ConfigureAudit();
        b.HasMany(x => x.Blocks).WithOne().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Workflow).WithOne().HasForeignKey<Workflow>(w => w.ModuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Rules).WithOne().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
    }
}
