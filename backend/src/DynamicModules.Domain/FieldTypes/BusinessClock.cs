using System.Globalization;

namespace DynamicModules.Domain.FieldTypes;

/// <summary>
/// 日期時間字串的解析：不依主機時區。營運時區目前固定為台北（+08:00）；
/// 帶偏移的值換成台北牆上時間，不帶偏移的值本身就是台北牆上時間（spec §6.4）。
/// </summary>
public static class BusinessClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    public static DateTime ParseWallClock(string s) =>
        TryParseWallClock(s, out var at) ? at : throw new FormatException($"日期時間格式錯誤：{s}");

    public static bool TryParseWallClock(string s, out DateTime at)
    {
        at = default;
        // AssumeUniversal 讓解析不經過主機時區；Kind 只用來分辨字串有沒有帶偏移
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        if (parsed.Kind == DateTimeKind.Unspecified) { at = dto.DateTime; return true; }
        // 只有帶偏移的值要換成台北時間，換算後超出 DateTime 上限就視為格式錯誤
        if (dto.UtcDateTime > DateTime.MaxValue - Offset) return false;
        at = dto.ToOffset(Offset).DateTime;
        return true;
    }
}
