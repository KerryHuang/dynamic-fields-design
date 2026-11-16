using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class NumberSequenceConfiguration : IEntityTypeConfiguration<NumberSequence>
{
    public void Configure(EntityTypeBuilder<NumberSequence> b)
    {
        b.ToTable("Dynamic_NumberSequence");
        b.HasKey(x => new { x.FieldId, x.Period });
        b.Property(x => x.Period).HasMaxLength(20).IsRequired();
    }
}
