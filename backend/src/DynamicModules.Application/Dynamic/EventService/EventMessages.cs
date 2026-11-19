namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>連動事件的上限訊息；上限是政策 A22（event.*）的生效值。</summary>
public static class EventMessages
{
    public static string TooManyEffects(int max) => $"效果至少 1 個、至多 {max} 個";
    public static string TooManyEvents(int max) => $"每個來源模組至多 {max} 個事件";
    public static string TooDeep(int max) => $"連動事件串聯過深（上限 {max} 層）";
    public static string TooManyWrites(int max) => $"連動事件寫入的記錄超過 {max} 筆，請縮小範圍";
    public static string TooManyEachRows(int max) => $"明細列超過 {max} 列，無法產生連動記錄";
}
