namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 解析後的語系包（<see cref="LocalePackParser"/> 的輸出；語系包設計 §5.1）。句子還沒驗；逐鍵的 <c>duplicate</c>、<c>notString</c> 已記在
/// <see cref="UiProblems"/>／<see cref="ServerProblems"/>，那些鍵不在 <see cref="Ui"/>／<see cref="Server"/>。空字串照收（分析時算「略過」）。
/// <c>locale</c> 段沒帶（或是 null）的欄位是 null，代表沿用既有值。
/// </summary>
public sealed record ParsedLocalePack(
    string Code, string? NativeName, string? QuasarLang, string? Fallback,
    string? PackVersion, string? BuiltFor, string? BaseLocale, string? Note,
    IReadOnlyDictionary<string, string> Ui, IReadOnlyList<RejectedItem> UiProblems,
    IReadOnlyDictionary<string, string> Server, IReadOnlyList<RejectedItem> ServerProblems,
    int DesignDataCount);

/// <summary>一句被拒收：鍵、原因代碼（<see cref="Domain.Localization.LocalePackReasons"/>）與給畫面組字的參數。</summary>
public sealed record RejectedItem(string Key, string Reason, IReadOnlyList<string> Args);
