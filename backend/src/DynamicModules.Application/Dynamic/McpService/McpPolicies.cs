using System.Globalization;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.McpService;

/// <summary>
/// MCP 政策的一次讀值（mcp1a 判斷 6）。<see cref="AccessInvalid"/>：9 項政策任一有存值但無法使用，或讀取丟例外；
/// 這時建立權杖與 <c>/mcp</c> 一律拒絕，不靠退回的出廠值（允許對象的出廠值是空＝全部使用者；整數項退回出廠值會放寬上限）。
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
        var rows = new Dictionary<PolicyDefinition, PolicyResolution>();
        try
        {
            foreach (var def in Defs) rows[def] = await policies.ResolveAsync(def, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 讀不到就拒絕；之後不再發查詢（交易內的資料庫錯誤會讓 PostgreSQL 整個交易失效），整數項用程式出廠值
            logger.LogError(ex, "讀取 MCP 政策失敗，AI 連線一律拒絕");
            return Rejected();
        }

        // 9 項任一有存值但無法使用就拒絕（第五輪審查：整數項壞值也不退回出廠值放行）
        var invalid = rows.Values.Any(r => r.SystemUnusable);
        if (invalid) logger.LogWarning("MCP 政策有無法使用的值，AI 連線一律拒絕");
        int IntOf(PolicyDefinition def) => int.Parse(rows[def].Effective, NumberStyles.None, CultureInfo.InvariantCulture);
        var max = IntOf(PolicyCatalog.McpTokenMaxLifetimeDays);
        return new McpPolicySnapshot(
            rows[PolicyCatalog.McpEnabled].Effective == "true", PolicyDefinition.RecipientItems(rows[PolicyCatalog.McpAllowedGroups].Effective),
            invalid, max, Math.Min(IntOf(PolicyCatalog.McpTokenDefaultLifetimeDays), max), IntOf(PolicyCatalog.McpTokenMaxPerUser),
            IntOf(PolicyCatalog.McpCallsPerMinute), IntOf(PolicyCatalog.McpCallsPerDay), IntOf(PolicyCatalog.McpMaxRows), IntOf(PolicyCatalog.McpConfirmTtlSeconds));
    }

    /// <summary>
    /// 寫入迴圈的門檻與時間窗（mcp2a 判斷 8、D5）。不併進 <see cref="ReadAsync"/> 的存取判斷：這兩項壞值不該讓整個 AI 連線停擺，
    /// 照 <see cref="IPolicyReader"/> 的一般規則用出廠值。
    /// </summary>
    public async Task<(int Triggers, int WindowMinutes)> ReadLoopAsync(CancellationToken ct) =>
        (await policies.IntAsync(PolicyCatalog.OpsRuleLoopTriggersPerRecord, null, ct),
            await policies.IntAsync(PolicyCatalog.OpsRuleLoopWindowMinutes, null, ct));

    private static readonly PolicyDefinition[] Defs =
    [
        PolicyCatalog.McpEnabled, PolicyCatalog.McpAllowedGroups, PolicyCatalog.McpTokenMaxLifetimeDays, PolicyCatalog.McpTokenDefaultLifetimeDays,
        PolicyCatalog.McpTokenMaxPerUser, PolicyCatalog.McpCallsPerMinute, PolicyCatalog.McpCallsPerDay, PolicyCatalog.McpMaxRows,
        PolicyCatalog.McpConfirmTtlSeconds,
    ];

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

    private static int Int(PolicyDefinition def) => int.Parse(def.Factory, CultureInfo.InvariantCulture);

    /// <summary>讀取失敗時的快照：拒絕存取，整數項用程式出廠值。</summary>
    private static McpPolicySnapshot Rejected()
    {
        var max = Int(PolicyCatalog.McpTokenMaxLifetimeDays);
        return new McpPolicySnapshot(false, [], true, max, Math.Min(Int(PolicyCatalog.McpTokenDefaultLifetimeDays), max),
            Int(PolicyCatalog.McpTokenMaxPerUser), Int(PolicyCatalog.McpCallsPerMinute), Int(PolicyCatalog.McpCallsPerDay),
            Int(PolicyCatalog.McpMaxRows), Int(PolicyCatalog.McpConfirmTtlSeconds));
    }
}
