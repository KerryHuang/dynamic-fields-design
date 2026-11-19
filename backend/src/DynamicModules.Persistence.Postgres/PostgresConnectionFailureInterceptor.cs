using System.Data.Common;
using System.Net.Sockets;
using DynamicModules.Application.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// PostgreSQL 的連線邊界（維運設計 §17.12 審查修正）：EF 開連線失敗、而且是裸的主機名稱解析失敗（Npgsql 不包裝、直接丟 SocketException）時，
/// 改丟 <see cref="DatabaseUnavailableException"/>（原例外放在內層）。錯誤分類器因此不必全域接受裸 SocketException，
/// 其他外部連線的 DNS 失敗照一般未處理例外（500、開缺陷事件）。只認名稱解析類的錯誤碼，其他 socket 錯誤 Npgsql 本來就會包成 NpgsqlException。
/// </summary>
public sealed class PostgresConnectionFailureInterceptor : DbConnectionInterceptor
{
    public static readonly PostgresConnectionFailureInterceptor Instance = new();

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => Wrap(eventData.Exception);

    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken ct = default)
    {
        Wrap(eventData.Exception);
        return Task.CompletedTask;
    }

    private static void Wrap(Exception exception)
    {
        if (exception is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData })
            throw new DatabaseUnavailableException("無法解析資料庫主機名稱", exception);
    }
}
