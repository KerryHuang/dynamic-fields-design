using DynamicModules.Application.Common.Security;

namespace DynamicModules.WebAPI.Security;

/// <summary>X-User 有效時回傳使用者 id，否則空字串（IdentityMiddleware 已先擋掉需要身分的請求）。</summary>
public class HeaderCurrentUser(IHttpContextAccessor accessor, IKnownUsers knownUsers) : ICurrentUser
{
    public string UserId
    {
        get
        {
            var value = accessor.HttpContext?.Request.Headers["X-User"].FirstOrDefault();
            return !string.IsNullOrEmpty(value) && knownUsers.Exists(value) ? value : "";
        }
    }
}
