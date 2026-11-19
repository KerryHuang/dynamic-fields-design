namespace DynamicModules.Application.Common;

/// <summary>
/// 資料庫存得下的時間精度（維運設計 §17.12 審查修正）：PostgreSQL 的 timestamp 只到微秒，.NET 的 DateTime 是 100 奈秒，
/// 寫入時末位會被截掉。會拿記憶體裡的時間和資料庫讀回的時間比「誰比較新」的地方，在寫入前先截到微秒，兩邊才是同一個值。
/// </summary>
public static class StorageTime
{
    public static DateTime ToMicroseconds(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, value.Kind);
}
