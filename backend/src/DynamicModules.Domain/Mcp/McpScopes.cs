namespace DynamicModules.Domain.Mcp;

/// <summary>
/// MCP 權杖的範圍代碼（設計 §5.2）：平台功能清單，寫在程式、不是政策。存進資料表時以逗號串接，讀回時由
/// <see cref="TryParse"/> 解析；有任何未知代碼就整串不認（權杖認證視為無效，計畫判斷 8）。
/// </summary>
public static class McpScopes
{
    public const string BusinessRead = "business.read", BusinessWrite = "business.write", DesignRead = "design.read",
        DesignWrite = "design.write", OpsRead = "ops.read", OpsAct = "ops.act";

    /// <summary>六個範圍，固定順序（串接與顯示都照這個順序）。</summary>
    public static IReadOnlyList<string> All { get; } = [BusinessRead, BusinessWrite, DesignRead, DesignWrite, OpsRead, OpsAct];

    /// <summary>可以建立的範圍：<c>business.read</c>、<c>business.write</c>（mcp2a；其餘範圍還沒有工具，不給建）。</summary>
    public static IReadOnlyList<string> Issuable { get; } = [BusinessRead, BusinessWrite];

    /// <summary>建立對話框預設勾選的範圍：只有唯讀（寫入範圍預設不勾，使用者 2026-11-21 裁決）；前端從 <c>me/mcp</c> 取，不自己判斷。</summary>
    public static IReadOnlyList<string> DefaultChecked { get; } = [BusinessRead];

    /// <summary>解析逗號串接的範圍：去重、依 <see cref="All"/> 排序；空字串、空項目或未知代碼（區分大小寫）回 false。</summary>
    public static bool TryParse(string csv, out IReadOnlyList<string> scopes)
    {
        scopes = [];
        if (string.IsNullOrEmpty(csv)) return false;
        var parts = csv.Split(',');
        if (parts.Any(p => !All.Contains(p, StringComparer.Ordinal))) return false;
        scopes = All.Where(s => parts.Contains(s, StringComparer.Ordinal)).ToList();
        return true;
    }

    /// <summary>依 <see cref="All"/> 的順序去重串接；呼叫端先確認都是已知代碼。</summary>
    public static string Join(IEnumerable<string> scopes)
    {
        var set = scopes.ToHashSet(StringComparer.Ordinal);
        return string.Join(',', All.Where(set.Contains));
    }
}
