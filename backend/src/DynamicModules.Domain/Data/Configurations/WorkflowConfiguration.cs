using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class WorkflowConfiguration : IEntityTypeConfiguration<Workflow>
{
    public void Configure(EntityTypeBuilder<Workflow> b)
    {
        b.ToTable("Dynamic_Workflow");
        b.HasKey(x => x.Id);
        b.Property(x => x.States).HasJsonConversion();
        b.Property(x => x.Transitions).HasJsonConversion();
        b.Ignore(x => x.StatusFieldKey);
        b.HasIndex(x => x.ModuleId).IsUnique();
        b.ConfigureAudit();
    }
}
