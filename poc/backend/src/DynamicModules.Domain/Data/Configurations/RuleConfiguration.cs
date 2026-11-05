using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> b)
    {
        b.ToTable("Dynamic_Rule");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Condition).HasConversion(
            v => v.GetRawText(),
            v => JsonDocument.Parse(v, default).RootElement.Clone(),
            new ValueComparer<JsonElement>(
                (x, y) => x.GetRawText() == y.GetRawText(),
                v => v.GetRawText().GetHashCode(),
                v => v));
        b.Property(x => x.Targets).HasJsonConversion();
        b.Property(x => x.OptionIds).HasJsonConversion();
        b.Property(x => x.MessageI18n).HasJsonNullableConversion();
        b.HasIndex(x => new { x.ModuleId, x.Sequence });
        b.ConfigureAudit();
    }
}
