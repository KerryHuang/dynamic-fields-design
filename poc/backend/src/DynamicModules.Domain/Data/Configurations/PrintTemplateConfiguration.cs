using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class PrintTemplateConfiguration : IEntityTypeConfiguration<PrintTemplate>
{
    public void Configure(EntityTypeBuilder<PrintTemplate> b)
    {
        b.ToTable("Dynamic_PrintTemplate");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Body).IsRequired();
        b.HasIndex(x => new { x.ModuleId, x.Key }).IsUnique();
        b.ConfigureAudit();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
    }
}
