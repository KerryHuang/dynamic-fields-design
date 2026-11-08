using DynamicModules.Domain.Entities.Mock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class MockCustomerConfiguration : IEntityTypeConfiguration<MockCustomer>
{
    public void Configure(EntityTypeBuilder<MockCustomer> b)
    {
        b.ToTable("Mock_Customer");
        b.HasKey(x => x.CustomerNo);
        b.Property(x => x.CustomerNo).HasMaxLength(20);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
    }
}
