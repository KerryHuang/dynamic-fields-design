namespace DynamicModules.Application.Common;

/// <summary>
/// 資料庫的寫入鎖在等待上限內拿不到（別的寫入太久，或別的行程占著）。可重試，API 回 503 加 Retry-After，不是 500。
/// </summary>
public class DatabaseBusyException(string message, Exception? inner = null) : Exception(message, inner)
{
    public const string DefaultMessage = "系統忙碌中，請稍後再試";
}
