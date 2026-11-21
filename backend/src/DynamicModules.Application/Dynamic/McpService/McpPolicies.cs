using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// MCP 政策的一次讀值（mcp1a 判斷 6）。<see cref="AccessInvalid"/>：存取類政策（<c>mcp.enabled</c>、<c>mcp.allowedGroups</c>）
/// 有存值但無法使用，或讀取丟例外；這時建立權杖與 <c>/mcp</c> 一律拒絕，不靠退回的出廠值（允許對象的出廠值是空＝全部使用者）。
/// <see cref="TokenDefaultLifetimeDays"/> 已夾到最長效期（T15）。
/// </summary>
public sealed record McpPolicySnapshot(
    bool Enabled, IReadOnlyList<string> AllowedItems, bool AccessInvalid,
    int TokenMaxLifetimeDays, int TokenDefaultLifetimeDays, int TokenMaxPerUser,
    int CallsPerMinute, int CallsPerDay, int MaxRows, int ConfirmTtlSeconds);

/// <summary>MCP 政策的讀值與存取判斷；每次呼叫都經 <see cref="IPolicyReader"/> 讀（不另外快取，改值下一個請求就生效）。</summary>
public sealed class McpPolicies(IPolicyReader policies, RecipientResolver recipients, ILogger<McpPolicies> logger)
{
    public async Task<McpPolicySnapshot> ReadAsync(CancellationToken ct)
    {
        PolicyResolution enabled, allowed;
        try
        {
            enabled = await policies.ResolveAsync(PolicyCatalog.McpEnabled, null, ct);
            allowed = await policies.ResolveAsync(PolicyCatalog.McpAllowedGroups, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 讀不到就拒絕；之後不再發查詢（交易內的資料庫錯誤會讓 PostgreSQL 整個交易失效），整數項用程式出廠值
            logger.LogError(ex, "讀取 MCP 存取政策失敗，AI 連線一律拒絕");
            return Rejected();
        }

        var invalid = enabled.SystemUnusable || allowed.SystemUnusable;
        if (invalid) logger.LogWarning("MCP 存取政策有無法使用的值，AI 連線一律拒絕");
        var max = await policies.IntAsync(PolicyCatalog.McpTokenMaxLifetimeDays, null, ct);
        return new McpPolicySnapshot(
            enabled.Effective == "true", PolicyDefinition.RecipientItems(allowed.Effective), invalid,
            max, Math.Min(await policies.IntAsync(PolicyCatalog.McpTokenDefaultLifetimeDays, null, ct), max),
            await policies.IntAsync(PolicyCatalog.McpTokenMaxPerUser, null, ct),
            await policies.IntAsync(PolicyCatalog.McpCallsPerMinute, null, ct),
            await policies.IntAsync(PolicyCatalog.McpCallsPerDay, null, ct),
            await policies.IntAsync(PolicyCatalog.McpMaxRows, null, ct),
            await policies.IntAsync(PolicyCatalog.McpConfirmTtlSeconds, null, ct));
    }

    /// <summary>
    /// 這位使用者能不能用 AI 連線：不能時回 403 機器碼（依序：政策壞值、未啟用、不在允許對象），能用回 null。
    /// 允許對象是合法的空值＝全部使用者；有項目時展開成啟用中的使用者再比對，展開為空＝沒有人。帳號本身的狀態由呼叫端先查。
    /// </summary>
    public async Task<string?> AccessDenialAsync(McpPolicySnapshot snapshot, string userId, CancellationToken ct)
    {
        if (snapshot.AccessInvalid) return McpMessages.CodePolicyInvalid;
        if (!snapshot.Enabled) return McpMessages.CodeDisabled;
        if (snapshot.AllowedItems.Count == 0) return null;
        var users = await recipients.ResolveAsync(snapshot.AllowedItems, ct);
        return users.Contains(userId, StringComparer.Ordinal) ? null : McpMessages.CodeNotAllowed;
    }

    private static int Int(PolicyDefinition def) => int.Parse(def.Factory, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>讀取失敗時的快照：拒絕存取，整數項用程式出廠值。</summary>
    private static McpPolicySnapshot Rejected()
    {
        var max = Int(PolicyCatalog.McpTokenMaxLifetimeDays);
        return new McpPolicySnapshot(false, [], true, max, Math.Min(Int(PolicyCatalog.McpTokenDefaultLifetimeDays), max),
            Int(PolicyCatalog.McpTokenMaxPerUser), Int(PolicyCatalog.McpCallsPerMinute), Int(PolicyCatalog.McpCallsPerDay),
            Int(PolicyCatalog.McpMaxRows), Int(PolicyCatalog.McpConfirmTtlSeconds));
    }
}
