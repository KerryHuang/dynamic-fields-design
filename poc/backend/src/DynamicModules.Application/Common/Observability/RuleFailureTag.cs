namespace DynamicModules.Application.Common.Observability;

/// <summary>
/// 規則歸屬標記：事件、檢查、任務規則在執行每條規則的外面用 <c>catch (Exception e) when (RuleFailureTag.Mark(...))</c>，
/// 把規則種類與編號寫進例外的 <c>Data</c>（最內層先寫者為準）；最後一道例外處理讀出來記 log 與計數。
/// <c>Mark</c> 只寫標記、不計數（穿過規則迴圈的例外很多是正常流程，如 409、503），永遠回 false。
/// </summary>
public static class RuleFailureTag
{
    public const string Event = "event", Check = "check", TaskRule = "taskRule";
    private const string KindKey = "formwright.ruleKind", IdKey = "formwright.ruleId";

    public static bool Mark(Exception e, string ruleKind, long ruleId)
    {
        try
        {
            if (!e.Data.Contains(KindKey))
            {
                e.Data[KindKey] = ruleKind;
                e.Data[IdKey] = ruleId;
            }
        }
        catch (Exception)
        {
            // Data 唯讀或不可序列化時放棄標記，不能讓標記本身改變例外
        }
        return false;
    }

    /// <summary>讀標記；例外被包進別的例外時，沿 InnerException 往內找第一個有標記的。</summary>
    public static (string? Kind, long? Id) Read(Exception e)
    {
        for (var cur = e; cur is not null; cur = cur.InnerException)
            if (cur.Data[KindKey] is string kind) return (kind, cur.Data[IdKey] as long?);
        return (null, null);
    }
}
