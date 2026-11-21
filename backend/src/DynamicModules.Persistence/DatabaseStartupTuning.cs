using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

/// <summary>
/// 啟動初始化中、migrate 之後的 provider 專屬資料庫調校（#20）。
/// SQLite 要把檔案切成 WAL：EF Migrate 只在「檔案不存在」時才設，部署時檔案已存在（如空的 .db）就停在 rollback journal，
/// 長交易期間其他連線的讀取會噴 database is locked。PostgreSQL 不需要，註冊不做事的版本。
/// </summary>
public interface IDatabaseStartupTuning
{
    Task ApplyAsync(DynamicDbContext db, CancellationToken ct);
}

public sealed class SqliteStartupTuning : IDatabaseStartupTuning
{
    public async Task ApplyAsync(DynamicDbContext db, CancellationToken ct)
    {
        // 記憶體資料庫設 WAL 會回 memory 而不報錯；已是 WAL 時是空操作
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = wal", ct);
    }
}

public sealed class NoDatabaseStartupTuning : IDatabaseStartupTuning
{
    public Task ApplyAsync(DynamicDbContext db, CancellationToken ct) => Task.CompletedTask;
}
