using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>
/// 給使用者看的訊息：<see cref="MessageText"/> 的鍵加參數（參數若是 <see cref="I18nText"/>，如欄位標籤、應用程式名稱，會依語系挑文字），或還沒搬進字串表的字面值（字串可直接隱含轉換，舊呼叫端照常可用）。
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

    /// <summary>欄位值驗證失敗（Domain 帶鍵與參數，見 <see cref="DynamicModules.Domain.FieldTypes.FieldErrors"/>）。</summary>
    public static MessageRef Of(FieldValueResult failed) => Of(failed.Error!, failed.ErrorArgs ?? []);

    public static MessageRef Literal(string text) => new(null, [], text);

    /// <summary>null 轉成 null，與原本傳 null 訊息的行為相同。</summary>
    public static implicit operator MessageRef?(string? text) => text is null ? null : Literal(text);

    /// <summary>
    /// 有鍵就依語系取字串表的文字；字面值原樣回傳。<paramref name="translateLiteral"/> 為 true 時，字面值剛好是字串表某一句（無參數）
    /// 的繁中原文也翻譯，給還沒改成鍵的 <see cref="ServiceResult"/> 訊息用；欄位錯誤不用，免得設計者自己寫的文字被系統譯文取代。
    /// </summary>
    public string Resolve(string? locale, bool translateLiteral = false) =>
        Key is not null ? MessageText.Get(locale, Key, Args.Select(a => a is I18nText t ? t.Resolve(locale ?? I18nText.FallbackLocale) : a).ToArray())
        : translateLiteral ? MessageText.TranslateLiteral(locale, _literal ?? "") : _literal ?? "";
}
