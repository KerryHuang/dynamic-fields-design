using System.Globalization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class TaskViewRepository(DynamicDbContext db) : ITaskViewRepository
{
    public Task<TaskView?> GetAsync(long id, CancellationToken ct) =>
        db.Set<TaskView>().Include(v => v.Shares).FirstOrDefaultAsync(v => v.Id == id, ct);

    public Task<List<TaskView>> ListVisibleAsync(string kind, string userId, IReadOnlyCollection<long> groupIds, long? departmentId,
        bool includeAllShared, CancellationToken ct)
    {
        var groups = groupIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        var department = departmentId?.ToString(CultureInfo.InvariantCulture);
        const string user = TaskViewShareKinds.User, group = TaskViewShareKinds.Group, dept = TaskViewShareKinds.Department;
        return db.Set<TaskView>().AsNoTracking().Include(v => v.Shares)
            .Where(v => v.Kind == kind && (v.OwnerUserId == userId
                || (includeAllShared && v.Shares.Any())
                || v.Shares.Any(s => (s.TargetKind == user && s.TargetId == userId)
                    || (s.TargetKind == group && groups.Contains(s.TargetId))
                    || (department != null && s.TargetKind == dept && s.TargetId == department))))
            .OrderBy(v => v.Id).ToListAsync(ct);
    }

    public async Task RemoveSharesToAsync(string targetKind, string targetId, CancellationToken ct) =>
        db.Set<TaskViewShare>().RemoveRange(
            await db.Set<TaskViewShare>().Where(s => s.TargetKind == targetKind && s.TargetId == targetId).ToListAsync(ct));

    public void Add(TaskView view) => db.Set<TaskView>().Add(view);

    public void Remove(TaskView view) => db.Set<TaskView>().Remove(view);
}
