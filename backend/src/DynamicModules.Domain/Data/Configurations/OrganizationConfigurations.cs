using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

// 組織骨架（設計 docs/superpowers/specs/2026-11-10-approval-workflow-design.md §11.2）。
// Department.ParentId 只建索引不建外鍵：環與「有子部門不能刪」由程式擋。

public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("Dynamic_Department");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.NameI18n).HasJsonConversion();
        b.Property(x => x.ManagerUserId).HasMaxLength(50);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.ParentId);
        b.ConfigureAudit();
    }
}

public class UserDepartmentConfiguration : IEntityTypeConfiguration<UserDepartment>
{
    public void Configure(EntityTypeBuilder<UserDepartment> b)
    {
        b.ToTable("Dynamic_UserDepartment");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.HasIndex(x => x.DepartmentId);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}
