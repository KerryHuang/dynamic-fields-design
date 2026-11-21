using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Observability;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.Login;

public class LoginHandler(
    IAccountRepository accounts, IPasswordService passwords, IUnitOfWork uow, TimeProvider time, LoginThrottle throttle, IAuditLog audit,
    ILogger<LoginHandler> logger)
    : IRequestHandler<LoginRequest, ServiceResult>
{
    /// <summary>一次登入嘗試的結果與稽核內容；<see cref="Throttled"/> 的不逐筆寫稽核（O2-T3）。</summary>
    private sealed record Outcome(ServiceResult Result, string? UserId, string? Reason, bool Throttled = false);

    // 同一帳號的登入序列化：失敗計數是「讀、驗 PBKDF2、寫」，並行猜測會互相覆蓋而超過 5 次。
    // 只在單一站台實例內有效（假設只有一個程序服務這個資料庫；多實例要改用資料庫層的原子更新）。
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    // 帳號不存在、停用、鎖定、沒密碼這幾條路也跑一次 PBKDF2，回應時間才不會洩漏帳號狀態
    private static string? _dummyHash;

    public async Task<ServiceResult> Handle(LoginRequest request, CancellationToken ct)
    {
        var outcome = await AttemptAsync(request, ct);
        if (outcome.Throttled)
        {
            OpsMetrics.LoginThrottled.Add(1);
            return outcome.Result;
        }
        // 寫不進稽核不擋登入（§5.6）：TryWriteNowAsync 只記 Error log。
        // 行為者明確傳入剛驗證的帳號（失敗為 null），不從請求帶的舊 cookie 取（維運設計 §17.2.1）
        await audit.TryWriteNowAsync(outcome.Result.Data is AuthUserResponse ok
            ? SignInAudit.Succeeded(ok.UserId, ok.DisplayName, Method)
            : SignInAudit.Failed(outcome.UserId, outcome.Reason, Method), ct);
        return outcome.Result;
    }

    private static readonly string? Method = new AuditSummary().Add("method", "password").ToJson();

    private async Task<Outcome> AttemptAsync(LoginRequest request, CancellationToken ct)
    {
        var userId = (request.UserId ?? "").Trim().ToLowerInvariant();
        var gate = Locks[StripeOf(userId)];
        await gate.WaitAsync(ct);
        try
        {
            var ip = string.IsNullOrEmpty(request.ClientIp) ? "unknown" : request.ClientIp;
            // 超長輸入不進限流表（避免匿名者灌爆記憶體）；截斷不影響比對，反正帳號也不會存在
            var key = userId.Length > 128 ? userId[..128] : userId;
            if (!throttle.TryBegin(key, ip))
                return Fail(null, "來源或帳號嘗試過於頻繁", null, request.Password ?? "") with { Throttled = true };
            Outcome result;
            try
            {
                result = await LoginAsync(userId, request.Password ?? "", ct);
            }
            catch
            {
                throttle.Release(key, ip); // 例外不算失敗，歸還名額
                throw;
            }
            throttle.Complete(key, ip, result.Result.Data is AuthUserResponse);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Outcome> LoginAsync(string userId, string password, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(userId, ct);
        if (user is null) return Fail(null, "帳號不存在", "unknownAccount", password);
        if (!user.IsActive) return Fail(user.UserId, "帳號已停用", "inactive", password);

        var now = time.GetUtcNow();
        if (user.LockedUntil is { } until && until > now) return Fail(user.UserId, "帳號鎖定中", "locked", password);
        if (user.PasswordHash is null) return Fail(user.UserId, "帳號尚未設定密碼", "noPassword", password);

        // 鎖定已到期：先歸零，之後輸錯一次不該立刻再鎖
        var lockExpired = user.LockedUntil is not null;
        if (lockExpired)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
        }

        if (!passwords.Verify(user.PasswordHash, password))
        {
            // 失敗次數改由 LoginThrottle 依來源計算，不再寫入帳號（否則匿名者能鎖住任何人）
            if (lockExpired) await uow.CommitAsync(ct);
            return Fail(user.UserId, "密碼錯誤", "wrongPassword");
        }

        // 平常登入不寫資料庫（Demo 產生器的寫入交易會鎖表）；有失敗紀錄時才歸零並寫入
        if (user.FailedLoginCount != 0 || lockExpired)
        {
            user.FailedLoginCount = 0;
            await uow.CommitAsync(ct);
        }
        logger.LogInformation("帳號 {UserId} 登入成功", user.UserId);
        return new Outcome(ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp)), user.UserId, null);
    }

    private static int StripeOf(string userId)
    {
        var hash = 2166136261u; // FNV-1a：跨程序穩定，不受 string.GetHashCode 隨機化影響
        foreach (var c in userId) hash = (hash ^ c) * 16777619u;
        return (int)(hash % (uint)Locks.Length);
    }

    /// <param name="storedUserId">資料庫裡查到的帳號；帳號不存在時為 null，此時絕不記錄輸入（匿名輸入可能是誤打的密碼、超長或含換行的字串）。</param>
    /// <param name="reasonCode">稽核的原因代碼（維運設計 §17.4 <c>ReasonCode</c>）。</param>
    private Outcome Fail(string? storedUserId, string reason, string? reasonCode, string? passwordToBurn = null)
    {
        if (passwordToBurn is not null)
            passwords.Verify(_dummyHash ??= passwords.Hash("dummy-password-for-timing"), passwordToBurn);
        if (storedUserId is null) logger.LogWarning("登入失敗：{Reason}（帳號不存在，不記錄輸入）", reason);
        else logger.LogWarning("帳號 {UserId} 登入失敗：{Reason}", storedUserId, reason);
        return new Outcome(ServiceResult.Unauthorized(MessageRef.Of(MessageText.LoginFailed)), storedUserId, reasonCode);
    }
}
