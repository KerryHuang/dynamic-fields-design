using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class DashboardConfiguration : IEntityTypeConfiguration<Dashboard>
{
    public void Configure(EntityTypeBuilder<Dashboard> b)
    {
        b.ToTable("Dynamic_Dashboard");
        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerUserId).HasMaxLength(50);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.LayoutJson).IsRequired();
        b.HasIndex(x => x.OwnerUserId);
        b.HasMany(x => x.Assignments).WithOne().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Cascade);
        b.ConfigureAudit();
    }
}

public class DashboardAssignmentConfiguration : IEntityTypeConfiguration<DashboardAssignment>
{
    public void Configure(EntityTypeBuilder<DashboardAssignment> b)
    {
        b.ToTable("Dynamic_DashboardAssignment");
        b.HasKey(x => new { x.DashboardId, x.GroupId });
        b.HasIndex(x => x.GroupId);
    }
}

public class DashboardPersonalVersionConfiguration : IEntityTypeConfiguration<DashboardPersonalVersion>
{
    public void Configure(EntityTypeBuilder<DashboardPersonalVersion> b)
    {
        b.ToTable("Dynamic_DashboardPersonalVersion");
        b.HasKey(x => new { x.DashboardId, x.UserId });
        b.Property(x => x.UserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.LayoutJson).IsRequired();
        b.HasOne<Dashboard>().WithMany().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.UserId);
    }
}
