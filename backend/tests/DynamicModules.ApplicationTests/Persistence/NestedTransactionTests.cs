using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

public class NestedTransactionTests
{
    [Fact]
    public async Task Nested_InnerHandleIsNoop_OuterRollbackUndoesAll()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var manager = new EfTransactionManager(db.Context);

        await using (var outer = await manager.BeginAsync(default))
        {
            await using (var inner = await manager.BeginAsync(default))
            {
                db.Context.Set<AppUser>().Add(new AppUser { UserId = "inner", DisplayName = "inner" });
                await db.Context.SaveChangesAsync();
                await inner.CommitAsync(default);
            }

            // 內層 handle 不做事：外層交易仍在、可以繼續寫入
            db.Context.Database.CurrentTransaction.Should().NotBeNull();
            db.Context.Set<AppUser>().Add(new AppUser { UserId = "outer", DisplayName = "outer" });
            await db.Context.SaveChangesAsync();
            await outer.RollbackAsync(default);
        }

        var ids = await db.Context.Set<AppUser>().AsNoTracking()
            .Where(u => u.UserId == "inner" || u.UserId == "outer").Select(u => u.UserId).ToListAsync();
        ids.Should().BeEmpty();
    }
}
