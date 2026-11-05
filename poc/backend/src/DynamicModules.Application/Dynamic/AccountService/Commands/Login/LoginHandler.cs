using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.Login;

public class LoginHandler(
    IAccountRepository accounts, IPasswordService passwords, IUnitOfWork uow, TimeProvider time, ILogger<LoginHandler> logger)
    : IRequestHandler<LoginRequest, ServiceResult>
{
    public const string FailureMessage = "帳號或密碼錯誤，或帳號暫時無法登入";
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    // 同一帳號的登入序列化：失敗計數是「讀、驗 PBKDF2、寫」，並行猜測會互相覆蓋而超過 5 次。
    // 只在單一站台實例內有效（假設只有一個程序服務這個資料庫；多實例要改用資料庫層的原子更新）。
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    // 帳號不存在、停用、鎖定、沒密碼這幾條路也跑一次 PBKDF2，回應時間才不會洩漏帳號狀態
    private static string? _dummyHash;

    public async Task<ServiceResult> Handle(LoginRequest request, CancellationToken ct)
    {
        var userId = (request.UserId ?? "").Trim().ToLowerInvariant();
        var gate = Locks[StripeOf(userId)];
        await gate.WaitAsync(ct);
        try
        {
            return await LoginAsync(userId, request.Password ?? "", ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ServiceResult> LoginAsync(string userId, string password, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(userId, ct);
        if (user is null) return Fail(userId, "帳號不存在", password);
        if (!user.IsActive) return Fail(userId, "帳號已停用", password);

        var now = time.GetUtcNow();
        if (user.LockedUntil is { } until && until > now) return Fail(userId, "帳號鎖定中", password);
        if (user.PasswordHash is null) return Fail(userId, "帳號尚未設定密碼", password);

        // 鎖定已到期：先歸零，之後輸錯一次不該立刻再鎖
        var lockExpired = user.LockedUntil is not null;
        if (lockExpired)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
        }

        if (!passwords.Verify(user.PasswordHash, password))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailures)
            {
                user.LockedUntil = now + LockDuration;
                logger.LogWarning("帳號 {UserId} 連續輸錯 {Count} 次，鎖定到 {Until}", userId, user.FailedLoginCount, user.LockedUntil);
            }
            await uow.CommitAsync(ct);
            return Fail(userId, "密碼錯誤");
        }

        // 平常登入不寫資料庫（Demo 產生器的寫入交易會鎖表）
        if (user.FailedLoginCount != 0 || lockExpired) await uow.CommitAsync(ct);
        logger.LogInformation("帳號 {UserId} 登入成功", userId);
        return ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp));
    }

    private static int StripeOf(string userId)
    {
        var hash = 2166136261u; // FNV-1a：跨程序穩定，不受 string.GetHashCode 隨機化影響
        foreach (var c in userId) hash = (hash ^ c) * 16777619u;
        return (int)(hash % (uint)Locks.Length);
    }

    private ServiceResult Fail(string userId, string reason, string? passwordToBurn = null)
    {
        if (passwordToBurn is not null)
            passwords.Verify(_dummyHash ??= passwords.Hash("dummy-password-for-timing"), passwordToBurn);
        logger.LogWarning("帳號 {UserId} 登入失敗：{Reason}", userId, reason);
        return ServiceResult.Unauthorized(FailureMessage);
    }
}
