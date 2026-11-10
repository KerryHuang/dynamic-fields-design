namespace DynamicModules.Application.Common;

/// <summary>
/// 給使用者看的訊息：<see cref="MessageText"/> 的鍵加參數，或還沒搬進字串表的字面值（字串可直接隱含轉換，舊呼叫端照常可用）。
/// 存在 <see cref="ServiceResult"/> 裡，到 WebAPI 才依請求語系解析，Application 層不用傳 locale。
/// </summary>
public sealed class MessageRef
{
    private readonly string? _literal;

    private MessageRef(string? key, object[] args, string? literal)
    {
        Key = key;
        Args = args;
        _literal = literal;
    }

    public string? Key { get; }
    public object[] Args { get; }

    public static MessageRef Of(string key, params object[] args) => new(key, args, null);

    public static MessageRef Literal(string text) => new(null, [], text);

    /// <summary>null 轉成 null，與原本傳 null 訊息的行為相同。</summary>
    public static implicit operator MessageRef?(string? text) => text is null ? null : Literal(text);

    /// <summary>字面值剛好是字串表某一句（無參數）的繁中原文時也翻譯，例如各處的「找不到模組」，不必逐一改呼叫端。</summary>
    public string Resolve(string? locale) => Key is not null ? MessageText.Get(locale, Key, Args) : MessageText.TranslateLiteral(locale, _literal ?? "");
}
