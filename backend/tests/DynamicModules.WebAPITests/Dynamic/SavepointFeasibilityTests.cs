using DynamicModules.Application.Common;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>整合設計 §12.1 #1：外層交易內的 savepoint 能隔離一段寫入，回滾不影響之前已儲存的內容。</summary>
public class SavepointFeasibilityTests : IDisposable
{
    private readonly TestApp _app = new(fileDatabase: true);

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Rollback_UndoesOnlyTheWorkAfterTheSavepoint_AndTheOuterTransactionStillCommits()
    {
        using (_app.AsUser()) { }
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionManager>();
        var savepoints = scope.ServiceProvider.GetRequiredService<IWorkSavepoints>();

        await using (var tx = await transactions.BeginAsync(default))
        {
            db.Set<SystemSetting>().Add(new SystemSetting { Key = "sp.before", Value = "1" });
            await db.SaveChangesAsync();

            var sp = await savepoints.CreateAsync(default);
            db.Set<SystemSetting>().Add(new SystemSetting { Key = "sp.inside", Value = "1" });
            await db.SaveChangesAsync();
            await sp.RollbackAsync(default);

            (await db.Set<SystemSetting>().AnyAsync(s => s.Key == "sp.before")).Should().BeTrue();
            (await db.Set<SystemSetting>().AnyAsync(s => s.Key == "sp.inside")).Should().BeFalse();

            // 回滾後 change tracker 乾淨，後續的寫入照常
            db.Set<SystemSetting>().Add(new SystemSetting { Key = "sp.after", Value = "1" });
            await db.SaveChangesAsync();

            var kept = await savepoints.CreateAsync(default);
            db.Set<SystemSetting>().Add(new SystemSetting { Key = "sp.kept", Value = "1" });
            await db.SaveChangesAsync();
            await kept.ReleaseAsync(default);
            await tx.CommitAsync(default);
        }

        using var verify = _app.Services.CreateScope();
        var keys = await verify.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<SystemSetting>()
            .Where(s => s.Key.StartsWith("sp.")).Select(s => s.Key).OrderBy(k => k).ToListAsync();
        keys.Should().Equal("sp.after", "sp.before", "sp.kept");
    }
}
