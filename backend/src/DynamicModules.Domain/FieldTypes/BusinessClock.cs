using System.Globalization;

namespace DynamicModules.Domain.FieldTypes;

/// <summary>
/// 日期時間字串的解析：不依主機時區。營運時區（政策 locale.timeZone）的偏移由呼叫端傳入；
/// 帶偏移的值換成營運時區的牆上時間，不帶偏移的值本身就是營運時區的牆上時間（spec §6.4）。
/// </summary>
public static class BusinessClock
{
    public static DateTime ParseWallClock(string s, TimeSpan offset) =>
        TryParseWallClock(s, offset, out var at) ? at : throw new FormatException($"日期時間格式錯誤：{s}");

    public static bool TryParseWallClock(string s, TimeSpan offset, out DateTime at)
    {
        at = default;
        // AssumeUniversal 讓解析不經過主機時區；Kind 只用來分辨字串有沒有帶偏移
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        if (parsed.Kind == DateTimeKind.Unspecified) { at = dto.DateTime; return true; }
        // 只有帶偏移的值要換成營運時區，換算後超出 DateTime 範圍就視為格式錯誤
        if (Overflows(dto, offset)) return false;
        at = dto.ToOffset(offset).DateTime;
        return true;
    }

    /// <summary>換成 <paramref name="offset"/> 的牆上時間後是否超出 DateTime 範圍（正偏移看上限、負偏移看下限）。</summary>
    public static bool Overflows(DateTimeOffset dto, TimeSpan offset) =>
        offset > TimeSpan.Zero ? dto.UtcDateTime > DateTime.MaxValue - offset
        : offset < TimeSpan.Zero && dto.UtcDateTime < DateTime.MinValue - offset;
}
