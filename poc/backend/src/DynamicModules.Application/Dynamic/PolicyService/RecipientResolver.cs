using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>
/// 把收件人政策的項目（<c>admins</c>、<c>group:&lt;id&gt;</c>、<c>user:&lt;userId&gt;</c>）展開成啟用中的使用者 Id，去重、排序。
/// 不存在的群組、使用者（資料庫被手改）略過並記 Warning；停用帳號直接略過。
/// </summary>
public sealed class RecipientResolver(IPermissionRepository permissions, ILogger<RecipientResolver> logger)
{
    public async Task<IReadOnlyList<string>> ResolveAsync(IReadOnlyList<string> items, CancellationToken ct)
    {
        var users = await permissions.ListUsersAsync(ct);
        var active = users.Where(u => u.IsActive).Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        var known = users.Select(u => u.UserId).ToHashSet(StringComparer.Ordinal);
        List<UserGroupMapping>? memberships = null;
        HashSet<long>? groupIds = null;
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (item == "admins")
                result.UnionWith(users.Where(u => u.IsActive && u.IsSystemAdmin).Select(u => u.UserId));
            else if (item.StartsWith("group:", StringComparison.Ordinal) && long.TryParse(item.AsSpan(6), out var gid))
            {
                groupIds ??= (await permissions.ListGroupsAsync(ct)).Select(g => g.Id).ToHashSet();
                if (!groupIds.Contains(gid)) { logger.LogWarning("收件人政策引用的群組 {Item} 不存在，略過", item); continue; }
                memberships ??= await permissions.ListMembershipsAsync(ct);
                result.UnionWith(memberships.Where(m => m.GroupId == gid && active.Contains(m.UserId)).Select(m => m.UserId));
            }
            else if (item.StartsWith("user:", StringComparison.Ordinal))
            {
                var id = item[5..];
                if (!known.Contains(id)) logger.LogWarning("收件人政策引用的使用者 {Item} 不存在，略過", item);
                else if (active.Contains(id)) result.Add(id);
            }
        }
        return result.Order(StringComparer.Ordinal).ToList();
    }
}
