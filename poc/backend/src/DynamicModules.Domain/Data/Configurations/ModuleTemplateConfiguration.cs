using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class ModuleTemplateConfiguration : IEntityTypeConfiguration<ModuleTemplate>
{
    public void Configure(EntityTypeBuilder<ModuleTemplate> b)
    {
        b.ToTable("Dynamic_ModuleTemplate");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.Json).IsRequired();
        b.Property(x => x.CreatedUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.ModifiedUserId).HasMaxLength(50);
    }
}
