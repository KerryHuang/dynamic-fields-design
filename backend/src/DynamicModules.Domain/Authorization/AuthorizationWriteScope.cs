namespace DynamicModules.Domain.Authorization;

/// <summary>寫入許可的種類（權限軸設計 §7.4）。</summary>
public enum AuthorizationPermitKind { Authorized, Derived, Cleanup, Seed, Test }

/// <summary>對守衛表下批次指令（<c>ExecuteDelete*</c>）的四個登記入口。</summary>
public enum AuthorizationBatchEntry { ReplaceModuleRows, ReplaceActionScopes, PurgeModule, PurgeFields }

/// <summary>
/// 一次寫入許可：只能經 <see cref="AuthorizationWriteScope.RunAsync{T}"/> 開，body 結束就關閉；關閉後（含被 Task.Run 帶走的副本）一律無效。
/// </summary>
public sealed class AuthorizationPermit
{
    private static readonly IReadOnlySet<long> NoIds = new HashSet<long>();
    private static readonly IReadOnlySet<string> NoCodes = new HashSet<string>();

    private AuthorizationPermit(AuthorizationPermitKind kind) => Kind = kind;

    public AuthorizationPermitKind Kind { get; }

    /// <summary>寫系統代碼列與成員表要這個標記：只有同時宣告系統管理員的請求經 gate 開的許可才有；Seed、Test 視為有。</summary>
    public bool SystemAdminVerified { get; private init; }

    /// <summary>Derived：可新增可見列的欄位、應用程式、群組；Cleanup：目標欄位（含目標模組的全部欄位）。</summary>
    public IReadOnlySet<long> FieldIds { get; private init; } = NoIds;
    public IReadOnlySet<long> AppIds { get; private init; } = NoIds;
    public IReadOnlySet<long> GroupIds { get; private init; } = NoIds;
    /// <summary>Cleanup：目標模組、它們的 program 代碼、目標欄位的選項。</summary>
    public IReadOnlySet<long> ModuleIds { get; private init; } = NoIds;
    public IReadOnlySet<string> ProgramCodes { get; private init; } = NoCodes;
    public IReadOnlySet<long> OptionIds { get; private init; } = NoIds;

    public bool IsClosed { get; private set; }

    internal void Close() => IsClosed = true;

    /// <summary>每次都是新物件：關閉一個許可不會影響別的請求。</summary>
    public static AuthorizationPermit Authorized(bool systemAdminVerified) =>
        new(AuthorizationPermitKind.Authorized) { SystemAdminVerified = systemAdminVerified };

    public static AuthorizationPermit Derived(long fieldId, long appId, IEnumerable<long> groupIds) =>
        new(AuthorizationPermitKind.Derived)
        {
            FieldIds = new HashSet<long> { fieldId }, AppIds = new HashSet<long> { appId }, GroupIds = groupIds.ToHashSet(),
        };

    public static AuthorizationPermit Cleanup(
        IEnumerable<long> moduleIds, IEnumerable<string> programCodes, IEnumerable<long> fieldIds, IEnumerable<long> optionIds) =>
        new(AuthorizationPermitKind.Cleanup)
        {
            ModuleIds = moduleIds.ToHashSet(), ProgramCodes = programCodes.ToHashSet(StringComparer.Ordinal),
            FieldIds = fieldIds.ToHashSet(), OptionIds = optionIds.ToHashSet(),
        };

    public static AuthorizationPermit Seed() => new(AuthorizationPermitKind.Seed) { SystemAdminVerified = true };

    /// <summary>只給測試專案的 <c>RunForTestsAsync</c> 用；清單測試禁止 src 呼叫。</summary>
    public static AuthorizationPermit ForTests() => new(AuthorizationPermitKind.Test) { SystemAdminVerified = true };
}

/// <summary>
/// 授權寫入許可（權限軸設計 §7.4）：掛在 <see cref="AsyncLocal{T}"/> 上，只能以 callback 形式開，沒有「先拿許可、再自己寫」的用法。
/// 巢狀時只看最內層；許可沿 await 往內傳；<c>ExecutionContext.SuppressFlow</c> 起的背景工作帶不到許可。
/// </summary>
public static class AuthorizationWriteScope
{
    private static readonly AsyncLocal<AuthorizationPermit?> Slot = new();

    /// <summary>目前有效（未關閉）的最內層許可；沒有時是 null。</summary>
    public static AuthorizationPermit? Current => Slot.Value is { IsClosed: false } p ? p : null;

    /// <summary>
    /// 在 <paramref name="permit"/> 內執行 <paramref name="body"/>；守衛變更的存檔必須在 body 內。
    /// body 結束時 <paramref name="hasPendingGuardChanges"/> 回 true（body 留下未存的守衛變更）就丟例外。
    /// 進入時就已有未存的守衛變更（外層留下的）時不檢查，交給外層的許可。
    /// </summary>
    public static async Task<T> RunAsync<T>(AuthorizationPermit permit, Func<bool> hasPendingGuardChanges, Func<Task<T>> body)
    {
        var outer = Slot.Value;
        var pendingAtStart = hasPendingGuardChanges();
        Slot.Value = permit;
        try
        {
            var result = await body();
            if (!pendingAtStart && hasPendingGuardChanges())
                throw new InvalidOperationException("守衛變更要在許可內存檔：寫入許可結束時還有未存的授權資料變更");
            return result;
        }
        finally
        {
            permit.Close();
            Slot.Value = outer;
        }
    }

    public static async Task RunAsync(AuthorizationPermit permit, Func<bool> hasPendingGuardChanges, Func<Task> body) =>
        await RunAsync(permit, hasPendingGuardChanges, async () => { await body(); return true; });

    /// <summary>
    /// 批次入口第一個寫入前呼叫：目前的許可准許這個入口與目標才放行，否則丟例外。
    /// <paramref name="moduleIds"/>、<paramref name="fieldIds"/> 是這次要清的目標；<paramref name="programCodes"/>、<paramref name="optionIds"/> 是批次入口依目標 ID 從資料庫推導、實際要刪的值。
    /// Cleanup 檢查四者都在許可參數內。
    /// </summary>
    public static void Demand(
        AuthorizationBatchEntry entry, IReadOnlyCollection<long>? moduleIds = null, IReadOnlyCollection<long>? fieldIds = null,
        IReadOnlyCollection<string>? programCodes = null, IReadOnlyCollection<long>? optionIds = null)
    {
        var permit = Current ?? throw Denied(entry, "沒有寫入許可");
        var ok = permit.Kind switch
        {
            AuthorizationPermitKind.Seed or AuthorizationPermitKind.Test => true,
            AuthorizationPermitKind.Authorized => entry is AuthorizationBatchEntry.ReplaceModuleRows or AuthorizationBatchEntry.ReplaceActionScopes,
            AuthorizationPermitKind.Cleanup => entry switch
            {
                AuthorizationBatchEntry.PurgeModule => moduleIds is not null && moduleIds.All(permit.ModuleIds.Contains)
                    && (programCodes ?? []).All(permit.ProgramCodes.Contains),
                AuthorizationBatchEntry.PurgeFields => fieldIds is not null && fieldIds.All(permit.FieldIds.Contains)
                    && (optionIds ?? []).All(permit.OptionIds.Contains),
                _ => false,
            },
            _ => false,
        };
        if (!ok) throw Denied(entry, $"{permit.Kind} 許可不准許這個入口或目標");
    }

    private static InvalidOperationException Denied(AuthorizationBatchEntry entry, string why) =>
        new($"授權資料的批次寫入被拒（{entry}）：{why}");
}

/// <summary>目前這個 DbContext 有沒有未存的守衛變更；給在 Application 開許可的位置用（Application 不碰 DbContext）。</summary>
public interface IAuthorizationChangeProbe
{
    bool HasPendingGuardChanges();
}
