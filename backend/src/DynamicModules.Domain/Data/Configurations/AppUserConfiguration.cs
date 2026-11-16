using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicModules.Domain.Data.Configurations;

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ToTable("Dynamic_AppUser");
        b.HasKey(x => x.UserId);
        b.Property(x => x.UserId).HasMaxLength(50);
        b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        b.Property(x => x.IsSystemAdmin).HasDefaultValue(false);
        b.Property(x => x.PasswordHash).HasMaxLength(200);
        b.Property(x => x.MustChangePassword).HasDefaultValue(false);
        // ValueGeneratedNever：否則 EF 會把明確寫入的 false 換成資料庫預設值 true
        b.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();
        b.Property(x => x.IsDemoAccount).HasDefaultValue(false);
        b.Property(x => x.FailedLoginCount).HasDefaultValue(0);
        b.Property(x => x.ThemeMode).HasMaxLength(20);
        b.Property(x => x.ThemeStyle).HasMaxLength(20);
        b.Property(x => x.SecurityStamp).HasMaxLength(50).IsRequired().HasDefaultValue("initial");
    }
}
