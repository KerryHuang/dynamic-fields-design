using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

// 任務檢視（看板、甘特圖）與分享（看板設計 docs/superpowers/specs/2026-11-13-task-board-reports-gantt-design.md §6.1）。

public class TaskViewConfiguration : IEntityTypeConfiguration<TaskView>
{
    public void Configure(EntityTypeBuilder<TaskView> b)
    {
        b.ToTable("Dynamic_TaskView");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.OwnerUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.ConfigJson).IsRequired();
        b.Property(x => x.RowVersion).HasMaxLength(36).IsRequired().IsConcurrencyToken();
        b.HasIndex(x => x.OwnerUserId);
        b.HasMany(x => x.Shares).WithOne().HasForeignKey(x => x.ViewId).OnDelete(DeleteBehavior.Cascade);
        b.ConfigureAudit();
    }
}

public class TaskViewShareConfiguration : IEntityTypeConfiguration<TaskViewShare>
{
    public void Configure(EntityTypeBuilder<TaskViewShare> b)
    {
        b.ToTable("Dynamic_TaskViewShare");
        b.HasKey(x => new { x.ViewId, x.TargetKind, x.TargetId });
        b.Property(x => x.TargetKind).HasMaxLength(20);
        b.Property(x => x.TargetId).HasMaxLength(50);
        b.HasIndex(x => new { x.TargetKind, x.TargetId });
    }
}
