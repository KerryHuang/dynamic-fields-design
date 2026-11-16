using System.Text.Json;

namespace DynamicModules.Domain.Reports;

/// <summary>
/// 儀表板版面（指派版、自己的儀表板、個人版共用格式）裡指向欄位的內容，供刪除前的引用盤點使用。
/// 卡片裡只有 <see cref="CardOptions.SortKey"/> 會指到欄位；ReportId 指向報表（報表另外盤點）、LinkDashboardId 指向儀表板、Range 只是區間種類。
/// </summary>
public static class DashboardReferences
{
    /// <summary>版面 JSON 無法解析時回 null，呼叫端要當成「引用了一切」（fail-closed）。量值 Id 不是欄位鍵，不算。</summary>
    public static IReadOnlySet<long>? TryFieldIdsOf(string layoutJson)
    {
        try
        {
            var fields = new HashSet<long>();
            foreach (var card in DashboardLayout.Parse(layoutJson).Cards)
            {
                if (card.Options.SortKey is not { } key || ReportFieldKeys.TryParse(key) is not { } r) continue;
                fields.Add(r.FieldId);
                if (r.ViaFieldId is { } via) fields.Add(via);
            }
            return fields;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
