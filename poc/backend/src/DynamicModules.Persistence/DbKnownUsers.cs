using System.Collections.Concurrent;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Persistence;

/// <summary>
/// 讀 Dynamic_AppUser。存在的帳號記在記憶體（目前沒有刪除使用者的功能，且 Demo 產生中交易鎖著該表，
/// 已知帳號不該因此查不到）；找不到的每次都重查，因為 Demo 產生器會在執行期新增使用者。
/// </summary>
public class DbKnownUsers(IServiceScopeFactory scopes) : IKnownUsers
{
    private readonly ConcurrentDictionary<string, bool> _known = new();

    public bool Exists(string userId)
    {
        if (_known.ContainsKey(userId)) return true;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var exists = db.Set<AppUser>().AsNoTracking().Any(u => u.UserId == userId);
        if (exists) _known[userId] = true;
        return exists;
    }
}
