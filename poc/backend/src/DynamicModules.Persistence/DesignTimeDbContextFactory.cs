using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DynamicModules.Persistence;

/// <summary>只給 dotnet-ef 用，避免為了產生 migration 而啟動整個 WebAPI host。</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DynamicDbContext>
{
    public DynamicDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite("Data Source=data/design.db", b => b.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options);
}
