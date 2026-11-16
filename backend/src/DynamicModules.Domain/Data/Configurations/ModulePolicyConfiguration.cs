using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

/// <summary>模組政策覆寫：主鍵為模組＋政策代碼，模組刪除時一起刪。</summary>
public class ModulePolicyConfiguration : IEntityTypeConfiguration<ModulePolicy>
{
    public void Configure(EntityTypeBuilder<ModulePolicy> b)
    {
        b.ToTable("Dynamic_ModulePolicy");
        b.HasKey(x => new { x.ModuleId, x.Code });
        b.Property(x => x.Code).HasMaxLength(100);
        b.Property(x => x.Value).HasMaxLength(500).IsRequired();
        b.Property(x => x.ModifiedUserId).HasMaxLength(50).IsRequired();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
    }
}
