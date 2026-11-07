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

    /// <summary>
    /// 寫入交易必須一開始就拿寫入鎖（BEGIN IMMEDIATE）。若是 deferred，先讀後寫的交易在 WAL 下會因快照過期
    /// 立刻得到 SQLITE_BUSY，busy_timeout 完全不起作用。
    /// </summary>
    [Fact]
    public async Task Begin_TakesTheWriteLockImmediately()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dm-tx-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<DynamicModules.Domain.Data.DynamicDbContext>()
                .UseSqlite($"Data Source={file}", b => b.MigrationsAssembly("DynamicModules.Persistence")).Options;
            await using var ctx = new DynamicModules.Domain.Data.DynamicDbContext(options);
            await ctx.Database.MigrateAsync();

            await using var tx = await new EfTransactionManager(ctx).BeginAsync(default);

            // 另一條連線只等 1 秒（Default Timeout=1，0 在 ADO 是無限等待）要寫入鎖：交易一開始就持有，所以必須失敗
            await using var other = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Default Timeout=1");
            await other.OpenAsync();
            await using var cmd = other.CreateCommand();
            cmd.CommandText = "BEGIN IMMEDIATE";
            var act = () => cmd.ExecuteNonQueryAsync();
            await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>().Where(e => e.SqliteErrorCode == 5);
            await tx.RollbackAsync(default);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(file) + "*")) File.Delete(f);
        }
    }
}
