using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class BlockConfiguration : IEntityTypeConfiguration<Block>
{
    public void Configure(EntityTypeBuilder<Block> b)
    {
        b.ToTable("Dynamic_Block");
        b.HasKey(x => x.Id);
        b.Property(x => x.LabelI18n).HasJsonConversion();
        b.ConfigureAudit();
        b.HasMany(x => x.Fields).WithOne().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Cascade);
    }
}
