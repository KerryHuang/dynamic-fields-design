using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

public class EfTransactionManagerTests
{
    [Fact]
    public async Task Commit_Persists_And_Rollback_Discards()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var manager = new EfTransactionManager(db.Context);

        await using (var tx = await manager.BeginAsync(default))
        {
            db.Context.Set<AppUser>().Add(new AppUser { UserId = "kept", DisplayName = "kept" });
            await db.Context.SaveChangesAsync();
            await tx.CommitAsync(default);
        }

        await using (var tx = await manager.BeginAsync(default))
        {
            db.Context.Set<AppUser>().Add(new AppUser { UserId = "dropped", DisplayName = "dropped" });
            await db.Context.SaveChangesAsync();
            await tx.RollbackAsync(default);
        }

        var ids = await db.Context.Set<AppUser>().AsNoTracking().Select(u => u.UserId).ToListAsync();
        ids.Should().Equal("kept");
    }
}
