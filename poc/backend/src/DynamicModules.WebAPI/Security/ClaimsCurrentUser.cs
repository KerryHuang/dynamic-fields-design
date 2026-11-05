using System.Security.Claims;
using DynamicModules.Application.Common.Security;

namespace DynamicModules.WebAPI.Security;

/// <summary>讀登入 cookie 的 NameIdentifier；沒有 HttpContext（背景流程）或未登入回空字串。</summary>
public class ClaimsCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
}
