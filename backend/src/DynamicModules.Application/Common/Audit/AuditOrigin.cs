using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Audit;

/// <summary>
/// 稽核的來源管道、權杖 id、行為者種類（O2-T24），以 AsyncLocal 保存、比照 <c>ActingScope</c>：Dispose 還原上一層，可巢狀。
/// 預設 <c>ui</c>、權杖 null、種類 <c>user</c>；<c>JobRunner</c> 開 <c>job</c>，mcp1 在 <c>/mcp</c> 驗完權杖後開 <c>mcp</c>。
/// </summary>
public static class AuditOrigin
{
    private static readonly AsyncLocal<Frame?> Current = new();

    public static AuditChannel Channel => Current.Value?.Channel ?? AuditChannel.Ui;

    public static long? TokenId => Current.Value?.TokenId;

    /// <summary>有身分時的行為者種類；null＝<c>user</c>。</summary>
    public static AuditActorKind? ActorKind => Current.Value?.ActorKind;

    /// <summary>
    /// 在登入的稽核身分情境內：<see cref="IAuditLog"/> 不從 <c>ICurrentUser</c> 取行為者（請求帶的舊 cookie 可能是別的帳號或已失效），
    /// 一律當成沒有身分。只有 <c>LoginHandler</c>、<c>DemoLoginHandler</c> 開（架構測試掃引用、<see cref="BeginSignIn"/> 執行期驗直接呼叫者）。
    /// </summary>
    public static bool InSignIn => Current.Value?.SignIn ?? false;

    public static IDisposable Begin(AuditChannel channel, long? tokenId = null, AuditActorKind? actorKind = null) =>
        Push(new Frame(channel, tokenId, actorKind, Current.Value?.SignIn ?? false, Current.Value));

    /// <summary>
    /// 登入的稽核身分情境（維運設計 §17.2.1）；管道沿用外層（預設 <c>ui</c>）。
    /// 執行期檢查直接呼叫者（審查 P2：反射呼叫繞得過 IL 掃描）：反射、委派轉手、其他組件的同名型別都會丟例外。
    /// 不內嵌，堆疊上一層才一定是呼叫者；一次登入多一個堆疊框架的成本（微秒級）相對 PBKDF2 可忽略。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable BeginSignIn()
    {
        EnsureSignInCaller(new StackFrame(1, needFileInfo: false).GetMethod());
        var outer = Current.Value;
        return Push(new Frame(outer?.Channel ?? AuditChannel.Ui, outer?.TokenId, outer?.ActorKind, true, outer));
    }

    /// <summary>呼叫者（狀態機與 lambda 類別歸到最外層型別）必須是本組件的兩個登入 handler；名單寫成常值，沒有可被反射改掉的欄位。</summary>
    private static void EnsureSignInCaller(MethodBase? caller)
    {
        var type = caller?.DeclaringType;
        while (type?.DeclaringType is not null) type = type.DeclaringType;
        if (type is null || type.Assembly != typeof(AuditOrigin).Assembly || type.FullName is not
                ("DynamicModules.Application.Dynamic.AccountService.Commands.Login.LoginHandler"
                 or "DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin.DemoLoginHandler"))
            throw new InvalidOperationException("登入的稽核身分情境只有 LoginHandler、DemoLoginHandler 能開");
    }

    private static Frame Push(Frame frame)
    {
        Current.Value = frame;
        return frame;
    }

    private sealed class Frame(AuditChannel channel, long? tokenId, AuditActorKind? actorKind, bool signIn, Frame? outer) : IDisposable
    {
        public AuditChannel Channel { get; } = channel;
        public long? TokenId { get; } = tokenId;
        public AuditActorKind? ActorKind { get; } = actorKind;
        public bool SignIn { get; } = signIn;

        public void Dispose() => Current.Value = outer;
    }
}

/// <summary>請求的來源 IP（WebAPI 用 <c>ClientIpResolver</c> 實作）；背景工作與沒有請求的環境為 null。</summary>
public interface IRequestOrigin
{
    string? ClientIp { get; }
}

/// <summary>沒有 HTTP 請求的環境（Application 測試、背景流程）。</summary>
public sealed class NoRequestOrigin : IRequestOrigin
{
    public string? ClientIp => null;
}
