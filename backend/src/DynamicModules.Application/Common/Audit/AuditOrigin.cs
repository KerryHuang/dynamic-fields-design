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
    /// 一律當成沒有身分。只有 <c>LoginHandler</c>、<c>DemoLoginHandler</c> 開（架構測試限定呼叫者）。
    /// </summary>
    public static bool InSignIn => Current.Value?.SignIn ?? false;

    public static IDisposable Begin(AuditChannel channel, long? tokenId = null, AuditActorKind? actorKind = null) =>
        Push(new Frame(channel, tokenId, actorKind, Current.Value?.SignIn ?? false, Current.Value));

    /// <summary>登入的稽核身分情境（維運設計 §17.2.1）；管道沿用外層（預設 <c>ui</c>）。</summary>
    public static IDisposable BeginSignIn()
    {
        var outer = Current.Value;
        return Push(new Frame(outer?.Channel ?? AuditChannel.Ui, outer?.TokenId, outer?.ActorKind, true, outer));
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
