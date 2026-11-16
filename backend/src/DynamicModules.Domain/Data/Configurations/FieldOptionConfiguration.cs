using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class FieldOptionConfiguration : IEntityTypeConfiguration<FieldOption>
{
    public void Configure(EntityTypeBuilder<FieldOption> b)
    {
        b.ToTable("Dynamic_FieldOption");
        b.HasKey(x => x.Id);
        b.Property(x => x.LabelI18n).HasJsonConversion();
        b.Property(x => x.Color).HasMaxLength(20);
        b.ConfigureAudit();
    }
}
