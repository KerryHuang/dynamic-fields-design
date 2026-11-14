using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>計算送到資料庫的 SQL 命令數（查詢次數是否隨資料筆數成長）。</summary>
public sealed class CountingCommandInterceptor : DbCommandInterceptor
{
    private int _count;

    public int Count => _count;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecuting(command, eventData, result);
    }
}
