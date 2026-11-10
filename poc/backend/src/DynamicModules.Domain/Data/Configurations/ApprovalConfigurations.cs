using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

// 簽核的資料表（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §4.3）。
// Instance.RecordId、Task.RecordId 只建索引不建外鍵：記錄刪除與模組清除由程式處理。

public class ApprovalFlowConfiguration : IEntityTypeConfiguration<ApprovalFlow>
{
    public void Configure(EntityTypeBuilder<ApprovalFlow> b)
    {
        b.ToTable("Dynamic_ApprovalFlow");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => new { x.ModuleId, x.ToOptionId, x.Priority }).IsUnique();
        b.ConfigureAudit();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ApprovalFlowVersionConfiguration : IEntityTypeConfiguration<ApprovalFlowVersion>
{
    public void Configure(EntityTypeBuilder<ApprovalFlowVersion> b)
    {
        b.ToTable("Dynamic_ApprovalFlowVersion");
        b.HasKey(x => x.Id);
        b.Property(x => x.Definition).HasJsonRecordConversion();
        b.Property(x => x.CreatedBy).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.FlowId, x.Version }).IsUnique();
        b.HasOne<ApprovalFlow>().WithMany().HasForeignKey(x => x.FlowId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ApprovalInstanceConfiguration : IEntityTypeConfiguration<ApprovalInstance>
{
    public void Configure(EntityTypeBuilder<ApprovalInstance> b)
    {
        b.ToTable("Dynamic_ApprovalInstance");
        b.HasKey(x => x.Id);
        b.Property(x => x.FlowSetSnapshot).HasJsonConversion();
        b.Property(x => x.RouteKind).HasMaxLength(20).IsRequired();
        b.Property(x => x.InitiatorUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.TitleSnapshot).HasMaxLength(200).IsRequired();
        b.Property(x => x.Picks).HasJsonConversion();
        b.Property(x => x.RowVersion).HasMaxLength(36).IsRequired().IsConcurrencyToken();
        b.Property(x => x.OperationId).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.RecordId).IsUnique().HasFilter("\"Status\" = 'pending'");
        b.HasIndex(x => new { x.ModuleId, x.Status });
        b.HasIndex(x => x.InitiatorUserId);
    }
}

public class ApprovalInstanceFlowConfiguration : IEntityTypeConfiguration<ApprovalInstanceFlow>
{
    public void Configure(EntityTypeBuilder<ApprovalInstanceFlow> b)
    {
        b.ToTable("Dynamic_ApprovalInstanceFlow");
        b.HasKey(x => new { x.InstanceId, x.FlowId });
        b.HasIndex(x => x.FlowVersionId);
        b.HasOne<ApprovalInstance>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ApprovalStageRunConfiguration : IEntityTypeConfiguration<ApprovalStageRun>
{
    public void Configure(EntityTypeBuilder<ApprovalStageRun> b)
    {
        b.ToTable("Dynamic_ApprovalStageRun");
        b.HasKey(x => x.Id);
        b.Property(x => x.StageCode).HasMaxLength(40);
        b.Property(x => x.NameSnapshot).HasJsonNullableConversion();
        b.Property(x => x.Mode).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.SkipReason).HasMaxLength(20);
        b.Property(x => x.AddedByUserId).HasMaxLength(50);
        b.Property(x => x.AddSignUserIds).HasJsonNullableConversion();
        b.Property(x => x.NodeId).HasMaxLength(40);
        b.Property(x => x.BranchKey).HasMaxLength(40);
        b.HasIndex(x => new { x.InstanceId, x.Seq });
        b.HasOne<ApprovalInstance>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ApprovalTaskConfiguration : IEntityTypeConfiguration<ApprovalTask>
{
    public void Configure(EntityTypeBuilder<ApprovalTask> b)
    {
        b.ToTable("Dynamic_ApprovalTask");
        b.HasKey(x => x.Id);
        b.Property(x => x.AssigneeUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.OriginalUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.SeatKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.AssigneeUserId, x.Status });
        b.HasIndex(x => new { x.OriginalUserId, x.Reason, x.Status });
        b.HasIndex(x => new { x.RecordId, x.Status });
        b.HasIndex(x => x.StageRunId);
        b.HasOne<ApprovalInstance>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ApprovalActionConfiguration : IEntityTypeConfiguration<ApprovalAction>
{
    public void Configure(EntityTypeBuilder<ApprovalAction> b)
    {
        b.ToTable("Dynamic_ApprovalAction");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.ActorUserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.OnBehalfOfUserId).HasMaxLength(50);
        b.Property(x => x.TargetUserIds).HasJsonNullableConversion();
        b.Property(x => x.Comment).HasMaxLength(1000);
        b.Property(x => x.DataJson).HasMaxLength(4000);
        b.Property(x => x.OperationId).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.InstanceId);
        b.HasOne<ApprovalInstance>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ApprovalDelegateConfiguration : IEntityTypeConfiguration<ApprovalDelegate>
{
    public void Configure(EntityTypeBuilder<ApprovalDelegate> b)
    {
        b.ToTable("Dynamic_ApprovalDelegate");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.DelegateUserId).HasMaxLength(50).IsRequired();
    }
}
