using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class ListViewConfiguration : IEntityTypeConfiguration<ListView>
{
    public void Configure(EntityTypeBuilder<ListView> b)
    {
        b.ToTable("Dynamic_ListView");
        b.HasKey(x => x.Id);
        b.Property(x => x.OwnerUserId).HasMaxLength(50);
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Columns).HasJsonConversion();
        b.Property(x => x.Sort).HasJsonConversion();
        b.Property(x => x.Filter).HasConversion(
            v => v.HasValue ? v.Value.GetRawText() : null,
            v => v == null ? null : JsonDocument.Parse(v, default).RootElement.Clone(),
            new ValueComparer<JsonElement?>(
                (x, y) => x.HasValue == y.HasValue && (!x.HasValue || x.Value.GetRawText() == y!.Value.GetRawText()),
                v => v.HasValue ? v.Value.GetRawText().GetHashCode() : 0,
                v => v));
        b.HasIndex(x => new { x.ModuleId, x.OwnerUserId });
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
        b.ConfigureAudit();
    }
}
