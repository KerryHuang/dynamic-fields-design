using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> b)
    {
        b.ToTable("Dynamic_Report");
        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.DefinitionJson).IsRequired();
        b.Property(x => x.RowVersion).HasMaxLength(36).IsRequired().IsConcurrencyToken();
        b.HasIndex(x => x.OwnerUserId);
        b.HasMany(x => x.Shares).WithOne().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        b.ConfigureAudit();
    }
}

public class ReportShareConfiguration : IEntityTypeConfiguration<ReportShare>
{
    public void Configure(EntityTypeBuilder<ReportShare> b)
    {
        b.ToTable("Dynamic_ReportShare");
        b.HasKey(x => new { x.ReportId, x.GroupId });
        b.HasIndex(x => x.GroupId);
    }
}
