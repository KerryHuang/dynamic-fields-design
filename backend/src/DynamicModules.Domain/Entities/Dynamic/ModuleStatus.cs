using System.Diagnostics.CodeAnalysis;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// Closed 只由應用程式的關閉動作設定：資料與設定原封保留、可以查閱，但不能寫入。
/// </summary>
public enum ModuleStatus { Draft, Active, Inactive, Closed }

/// <summary>
/// 模組狀態的集中判定。各處不要自己寫 <c>Status == Active</c>，要先決定這個動作是「讀」還是「寫」。
/// 新增狀態時漏改的地方會落在 fail-closed 的一邊（兩者都回 false）。
/// </summary>
public static class ModuleStatusExtensions
{
    /// <summary>能查閱資料與設定：Active 或 Closed。</summary>
    public static bool IsReadable(this ModuleStatus status) => status is ModuleStatus.Active or ModuleStatus.Closed;

    /// <summary>能寫入記錄、檢視、參照值：只有 Active。</summary>
    public static bool IsWritable(this ModuleStatus status) => status is ModuleStatus.Active;

    /// <summary>模組存在且能查閱（null 安全，通過時編譯器視為非 null）。</summary>
    public static bool IsReadable([NotNullWhen(true)] this Module? module) => module is not null && module.Status.IsReadable();

    /// <summary>模組存在且能寫入（null 安全，通過時編譯器視為非 null）。</summary>
    public static bool IsWritable([NotNullWhen(true)] this Module? module) => module is not null && module.Status.IsWritable();
}
