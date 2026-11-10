namespace DynamicModules.Domain.Organization;

/// <summary>主管解析（設計 §6.2）：主管空缺、停用部門、停用主管、主管是發起人本人，都繼續往上找。</summary>
public static class ManagerChain
{
    public const int MinLevel = 1;
    public const int MaxLevel = 5;

    /// <summary>level 1..5；找不到回 null。第 N 層從第 N−1 層被找到的那個部門的上級開始找。</summary>
    public static string? Resolve(OrgSnapshot org, string initiatorUserId, int level)
    {
        if (level is < MinLevel or > MaxLevel) return null;
        if (!org.UserDepartment.TryGetValue(initiatorUserId, out var startId)) return null;

        long? current = startId;
        var visited = new HashSet<long>();
        string? found = null;
        for (var n = 1; n <= level; n++)
        {
            found = null;
            while (current is { } id && visited.Add(id) && org.Departments.TryGetValue(id, out var dept))
            {
                current = dept.ParentId;
                if (IsEligible(org, dept, initiatorUserId))
                {
                    found = dept.ManagerUserId;
                    break;
                }
            }
            if (found is null) return null;
        }
        return found;
    }

    private static bool IsEligible(OrgSnapshot org, OrgDepartment dept, string initiatorUserId) =>
        dept.Active
        && dept.ManagerUserId is { Length: > 0 } manager
        && manager != initiatorUserId
        && org.ActiveUsers.Contains(manager);
}
