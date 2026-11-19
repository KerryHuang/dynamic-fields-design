using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Domain.Data;

public class DynamicDbContext(DbContextOptions<DynamicDbContext> options) : DbContext(options)
{
    private IDisposable? _heldUntilSaveEnds;

    /// <summary>授權寫入邊界（權限軸設計 §7.4）掛在每一個 context 上，不靠各 provider 或測試記得註冊。</summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(AuthorizationSaveChangesInterceptor.Instance);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DynamicDbContext).Assembly);

    /// <summary>
    /// 掛上這次 SaveChanges 結束時一定放開的資源（PostgreSQL 的寫入閘門，PostgreSQL provider 設計 §5.6）。
    /// EF 的 <c>SavingChanges</c> 攔截器與 <c>DetectChanges</c> 在 SaveChanges 的 try 之外，那裡丟例外時不發任何結束事件，
    /// 所以由這裡覆寫的 SaveChanges 以 finally 放開。SQLite 沒有人掛，行為不變。
    /// </summary>
    public void HoldUntilSaveEnds(IDisposable resource)
    {
        _heldUntilSaveEnds?.Dispose();
        _heldUntilSaveEnds = resource;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try { return base.SaveChanges(acceptAllChangesOnSuccess); }
        finally { ReleaseHeld(); }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try { return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
        finally { ReleaseHeld(); }
    }

    private void ReleaseHeld()
    {
        var held = _heldUntilSaveEnds;
        _heldUntilSaveEnds = null;
        held?.Dispose();
    }
}
