namespace DynamicModules.Domain.Tasks;

/// <summary>某模組的記錄讀取範圍；OwnerUserIds 為 null 代表全部記錄，否則只含這些擁有者的記錄。</summary>
public sealed record ModuleReadScope(long ModuleId, IReadOnlySet<string>? OwnerUserIds);

/// <summary>
/// 一位使用者的任務可見條件（看板設計 §4.4）：管理員、自己、部屬名單、每個可讀（非明細）模組的資料範圍。
/// 只描述條件，不含任何任務資料；Persistence 轉成單一 EF 條件。
/// </summary>
public sealed record TaskVisibility(bool Admin, string UserId, IReadOnlySet<string> Subordinates, IReadOnlyList<ModuleReadScope> Modules);
