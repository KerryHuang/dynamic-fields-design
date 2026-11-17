namespace DynamicModules.Domain.Organization;

/// <summary>報表部門篩選的部門展開（看板設計 §8.2）：所選部門，includeSub 時含所有下層部門（經過停用的子部門照走）。</summary>
public static class DepartmentScope
{
    public static IReadOnlySet<long> Expand(IReadOnlyDictionary<long, OrgDepartment> departments, IEnumerable<long> selected, bool includeSub)
    {
        var scope = new HashSet<long>();
        var pending = new Queue<long>(selected);
        var children = includeSub
            ? departments.Values.Where(d => d.ParentId is not null).ToLookup(d => d.ParentId!.Value, d => d.Id)
            : null;
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!scope.Add(id) || children is null) continue;
            foreach (var child in children[id]) pending.Enqueue(child);
        }
        return scope;
    }
}
