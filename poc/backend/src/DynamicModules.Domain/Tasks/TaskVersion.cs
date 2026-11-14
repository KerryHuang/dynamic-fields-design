using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Tasks;

/// <summary>
/// 任務版本字串：(ModifiedTime ?? CreatedTime) 視為 UTC，精確到毫秒（yyyyMMddHHmmssfff）。
/// 毫秒精度讓 SQLite 與 PostgreSQL 往返都穩定；同一毫秒內的兩次寫入視為同一版本（已知限制）。
/// </summary>
public static class TaskVersion
{
    public static string Of(TaskItem task)
    {
        var t = task.ModifiedTime ?? task.CreatedTime;
        var utc = t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return utc.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
    }
}
