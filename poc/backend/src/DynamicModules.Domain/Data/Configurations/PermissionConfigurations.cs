using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class PermissionGroupConfiguration : IEntityTypeConfiguration<PermissionGroup>
{
    public void Configure(EntityTypeBuilder<PermissionGroup> b)
    {
        b.ToTable("Dynamic_PermissionGroup");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.NameI18n).HasJsonConversion();
    }
}

public class UserGroupMappingConfiguration : IEntityTypeConfiguration<UserGroupMapping>
{
    public void Configure(EntityTypeBuilder<UserGroupMapping> b)
    {
        b.ToTable("Dynamic_UserGroupMapping");
        b.HasKey(x => new { x.GroupId, x.UserId });
        b.Property(x => x.UserId).HasMaxLength(50);
    }
}

public class GroupProgramPrivilegeConfiguration : IEntityTypeConfiguration<GroupProgramPrivilege>
{
    public void Configure(EntityTypeBuilder<GroupProgramPrivilege> b)
    {
        b.ToTable("Dynamic_GroupProgramPrivilege");
        b.HasKey(x => new { x.GroupId, x.ProgramCode });
        b.Property(x => x.ProgramCode).HasMaxLength(8);
    }
}

public class UserProgramPrivilegeConfiguration : IEntityTypeConfiguration<UserProgramPrivilege>
{
    public void Configure(EntityTypeBuilder<UserProgramPrivilege> b)
    {
        b.ToTable("Dynamic_UserProgramPrivilege");
        b.HasKey(x => new { x.UserId, x.ProgramCode });
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.ProgramCode).HasMaxLength(8);
    }
}

public class FieldPermissionConfiguration : IEntityTypeConfiguration<FieldPermission>
{
    public void Configure(EntityTypeBuilder<FieldPermission> b)
    {
        b.ToTable("Dynamic_FieldPermission");
        b.HasKey(x => new { x.FieldId, x.GroupId });
        b.Property(x => x.Access).HasConversion<string>().HasMaxLength(20);
    }
}

public class ModuleDataScopeConfiguration : IEntityTypeConfiguration<ModuleDataScope>
{
    public void Configure(EntityTypeBuilder<ModuleDataScope> b)
    {
        b.ToTable("Dynamic_ModuleDataScope");
        b.HasKey(x => new { x.ModuleId, x.GroupId });
        b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
    }
}

public class FieldOptionScopeConfiguration : IEntityTypeConfiguration<FieldOptionScope>
{
    public void Configure(EntityTypeBuilder<FieldOptionScope> b)
    {
        b.ToTable("Dynamic_FieldOptionScope");
        b.HasKey(x => new { x.OptionId, x.GroupId });
    }
}
