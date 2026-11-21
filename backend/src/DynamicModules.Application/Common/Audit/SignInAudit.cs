using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 登入稽核列（維運設計 §17.2.1）：行為者由登入 handler 明確傳入——成功是剛驗證通過的帳號，失敗是 null。
/// 帶著這個標記的列，<see cref="IAuditLog"/> 不從 <c>ICurrentUser</c> 取行為者（請求帶的舊 cookie 可能是別的帳號或已失效）。
/// 防冒用：型別是 internal，其他組件編譯不到（Application 不開 InternalsVisibleTo）；組件內的引用者由
/// <c>AuditArchitectureTests</c> 限定為 <c>LoginHandler</c>、<c>DemoLoginHandler</c>；只組得出
/// <c>login.succeeded</c>（行為者＝目標）與 <c>login.failed</c>（行為者 null）兩種列，借到了也寫不出別的動作。
/// </summary>
internal static class SignInAudit
{
    public static AuditEntry Succeeded(string userId, string? displayName, string? summary) =>
        new("login.succeeded", AuditResult.Success, AuditTargets.User, userId, displayName, Summary: summary)
        {
            SignIn = new SignInActor(userId),
        };

    /// <param name="userId">資料庫裡查到的帳號；帳號不存在時為 null（不記輸入的帳號字串）。</param>
    public static AuditEntry Failed(string? userId, string? reasonCode, string? summary) =>
        new("login.failed", AuditResult.Denied, userId is null ? null : AuditTargets.User, userId, ReasonCode: reasonCode, Summary: summary)
        {
            SignIn = new SignInActor(null),
        };
}

/// <summary>登入稽核的行為者；只由 <see cref="SignInAudit"/> 建立。</summary>
internal sealed record SignInActor(string? UserId);
