using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Tasks;

/// <summary>
/// 任務版本字串：(ModifiedTime ?? CreatedTime) 的牆上時間，精確到毫秒（yyyyMMddHHmmssfff），不看 Kind。
/// 寫入當下的值（營運時鐘可能帶 Local）與從資料庫讀回的值（Unspecified）必須得到同一個字串，所以不做時區換算。
/// 毫秒精度讓 SQLite 與 PostgreSQL 往返都穩定；同一毫秒內的兩次寫入視為同一版本（已知限制）。
/// </summary>
public static class TaskVersion
{
    public static string Of(TaskItem task) =>
        (task.ModifiedTime ?? task.CreatedTime).ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
}
