using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class DynamicProgramConfiguration : IEntityTypeConfiguration<DynamicProgram>
{
    public void Configure(EntityTypeBuilder<DynamicProgram> b)
    {
        b.ToTable("Dynamic_Program");
        b.HasKey(x => x.ProgramCode);
        b.Property(x => x.ProgramCode).HasMaxLength(8);
        b.Property(x => x.NameI18n).HasJsonConversion();
    }
}
