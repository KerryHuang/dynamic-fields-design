using System.Linq.Expressions;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Tasks;

/// <summary>某模組的記錄讀取範圍；OwnerUserIds 為 null 代表全部記錄，否則只含這些擁有者的記錄。</summary>
public sealed record ModuleReadScope(long ModuleId, IReadOnlySet<string>? OwnerUserIds);

/// <summary>
/// 一位使用者的任務可見條件（看板設計 §4.4）：管理員、自己、部屬名單、每個可讀（非明細）模組的資料範圍。
/// 只描述條件，不含任何任務資料；Persistence 轉成單一 EF 條件。
/// </summary>
public sealed record TaskVisibility(bool Admin, string UserId, IReadOnlySet<string> Subordinates, IReadOnlyList<ModuleReadScope> Modules);

/// <summary>
/// 「部屬的任務」（看板設計 §4.2）：負責人是部屬，或未指派而建立者是部屬。批次可見條件、部屬的任務分頁、逐筆判斷（編譯後）共用這一份，
/// 改條件只改這裡。
/// </summary>
public static class SubordinateTasks
{
    public static Expression<Func<TaskItem, bool>> Of(IReadOnlyCollection<string> subordinates)
    {
        var ids = subordinates.ToArray();
        return t => (t.AssigneeUserId != null && ids.Contains(t.AssigneeUserId))
            || (t.AssigneeUserId == null && t.CreatedBy != null && ids.Contains(t.CreatedBy));
    }
}
