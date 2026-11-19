namespace DynamicModules.Application.Common;

/// <summary>
/// 確定是「連資料庫」失敗、但 provider 的例外本身看不出來時，由該 provider 的連線邊界包成這個型別（維運設計 §17.2.9、§17.12 審查修正）。
/// 目前只有 PostgreSQL 的主機名稱解析失敗：Npgsql 直接丟裸的 SocketException，和其他外部連線的 DNS 失敗分不出來。
/// <see cref="IDatabaseErrors.IsUnavailable"/> 認得它，API 回 503 <c>databaseUnavailable</c>。
/// </summary>
public sealed class DatabaseUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
