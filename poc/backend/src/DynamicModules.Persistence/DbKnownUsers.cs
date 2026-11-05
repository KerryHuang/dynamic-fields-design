using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Persistence;

/// <summary>讀 Dynamic_AppUser；POC 簡化：使用者集合在程序存活期間只載入一次。</summary>
public class DbKnownUsers(IServiceScopeFactory scopes) : IKnownUsers
{
    private readonly Lazy<HashSet<string>> _ids = new(() =>
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        return db.Set<AppUser>().Select(u => u.UserId).ToHashSet();
    });

    public bool Exists(string userId) => _ids.Value.Contains(userId);
}
