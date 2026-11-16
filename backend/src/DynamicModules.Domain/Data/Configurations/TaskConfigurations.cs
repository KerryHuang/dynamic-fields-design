using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

// 任務、留言、規則與通知的核心資料表（設計 docs/superpowers/specs/2026-11-10-task-management-design.md §3）。
// Task.RuleId、Task.RecordId、TaskRule.AppId 只建索引不建外鍵：規則刪了之後 RuleId 要留著。

public class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> b)
    {
        b.ToTable("Dynamic_Task");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.AssigneeUserId).HasMaxLength(50);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RecordTitleSnapshot).HasMaxLength(200);
        b.Property(x => x.RuleTaskCode).HasMaxLength(40);
        b.Property(x => x.CreatedBy).HasMaxLength(50);
        b.Property(x => x.ModifiedBy).HasMaxLength(50);
        b.Ignore(x => x.IsOpen);
        b.HasIndex(x => new { x.RuleId, x.RecordId, x.RuleOccurrence, x.RuleTaskCode }).IsUnique();
        b.HasIndex(x => x.RecordId);
        b.HasIndex(x => new { x.AssigneeUserId, x.Status });
        b.HasIndex(x => x.CreatedBy);
        b.HasIndex(x => x.ModuleId);
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Participants).WithOne().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TaskCommentConfiguration : IEntityTypeConfiguration<TaskComment>
{
    public void Configure(EntityTypeBuilder<TaskComment> b)
    {
        b.ToTable("Dynamic_TaskComment");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AuthorUserId).HasMaxLength(50);
        b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        b.Property(x => x.SystemEvent).HasMaxLength(40);
        b.Property(x => x.SystemData).HasMaxLength(2000);
        b.Property(x => x.WithdrawnBy).HasMaxLength(50);
        b.HasIndex(x => x.TaskId);
        b.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TaskCommentAttachmentConfiguration : IEntityTypeConfiguration<TaskCommentAttachment>
{
    public void Configure(EntityTypeBuilder<TaskCommentAttachment> b)
    {
        b.ToTable("Dynamic_TaskCommentAttachment");
        b.HasKey(x => x.Id);
        b.Property(x => x.FileName).HasMaxLength(150).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.StorageKey).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.CommentId);
        b.HasIndex(x => x.StorageKey).IsUnique();
    }
}

public class TaskParticipantConfiguration : IEntityTypeConfiguration<TaskParticipant>
{
    public void Configure(EntityTypeBuilder<TaskParticipant> b)
    {
        b.ToTable("Dynamic_TaskParticipant");
        b.HasKey(x => new { x.TaskId, x.UserId, x.Reason });
        b.Property(x => x.UserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Reason).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.UserId);
    }
}

public class TaskRuleConfiguration : IEntityTypeConfiguration<TaskRule>
{
    public void Configure(EntityTypeBuilder<TaskRule> b)
    {
        b.ToTable("Dynamic_TaskRule");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.Repeat).HasConversion<string>().HasMaxLength(20).HasDefaultValue(TaskRuleRepeat.Once);
        b.Property(x => x.Trigger).HasJsonConversion();
        b.HasIndex(x => x.Key).IsUnique();
        b.HasIndex(x => x.ModuleId);
        b.HasIndex(x => x.AppId);
        b.ConfigureAudit();
        b.HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany<TaskRuleRun>().WithOne().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TaskRuleTaskConfiguration : IEntityTypeConfiguration<TaskRuleTask>
{
    public void Configure(EntityTypeBuilder<TaskRuleTask> b)
    {
        b.ToTable("Dynamic_TaskRuleTask");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.TitleI18n).HasJsonConversion();
        b.Property(x => x.DescriptionI18n).HasJsonNullableConversion();
        b.HasIndex(x => new { x.RuleId, x.Code }).IsUnique();
    }
}

public class TaskRuleRunConfiguration : IEntityTypeConfiguration<TaskRuleRun>
{
    public void Configure(EntityTypeBuilder<TaskRuleRun> b)
    {
        b.ToTable("Dynamic_TaskRuleRun");
        b.HasKey(x => new { x.RuleId, x.RecordId, x.Occurrence });
        b.Property(x => x.TriggeredBy).HasMaxLength(50);
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("Dynamic_Notification");
        b.HasKey(x => x.Id);
        b.Property(x => x.UserId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
        b.Property(x => x.TitleSnapshot).HasMaxLength(200).IsRequired();
        b.Property(x => x.Link).HasMaxLength(400).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(40);
        b.HasIndex(x => new { x.UserId, x.CreatedTime });
        b.HasIndex(x => new { x.UserId, x.ReadTime });
        b.HasIndex(x => new { x.SourceType, x.SourceId });
    }
}
