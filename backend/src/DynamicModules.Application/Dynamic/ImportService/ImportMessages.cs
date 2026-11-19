using System.Globalization;

namespace DynamicModules.Application.Dynamic.ImportService;

/// <summary>匯入的檔案層級訊息；上限是政策 A21（import.maxRows、import.maxFileBytes）的生效值。</summary>
public static class ImportMessages
{
    public static string TooManyRows(int max) => $"一次最多匯入 {max} 筆";

    /// <summary>以 MB 顯示（1048576 → 1 MB、524288 → 0.5 MB）。</summary>
    public static string FileTooLarge(long maxBytes) =>
        $"檔案不能超過 {(maxBytes / 1048576m).ToString("0.##", CultureInfo.InvariantCulture)} MB";
}
