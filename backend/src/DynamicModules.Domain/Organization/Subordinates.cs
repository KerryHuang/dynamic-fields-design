namespace DynamicModules.Domain.Organization;

/// <summary>主管範圍的部屬解析（任務看板設計 §4.1）：主管管理的啟用部門，recursive 時含所有下層部門（經過停用的子部門照走）。</summary>
public static class Subordinates
{
    public static IReadOnlySet<string> Of(OrgSnapshot org, string managerUserId, bool recursive)
    {
        var scope = new HashSet<long>();
        var pending = new Queue<long>(org.Departments.Values
            .Where(d => d.Active && d.ManagerUserId == managerUserId)
            .Select(d => d.Id));
        if (pending.Count == 0) return new HashSet<string>();

        var children = recursive
            ? org.Departments.Values.Where(d => d.ParentId is not null).ToLookup(d => d.ParentId!.Value, d => d.Id)
            : null;
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!scope.Add(id) || children is null) continue;
            foreach (var child in children[id]) pending.Enqueue(child);
        }

        return org.UserDepartment
            .Where(p => scope.Contains(p.Value) && p.Key != managerUserId)
            .Select(p => p.Key)
            .ToHashSet();
    }
}
