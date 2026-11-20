using System.Text.Json.Serialization;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Localization;

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

/// <summary>
/// 分析與重驗的輸入（i4d1 計畫判斷 25）：交易內讀的完整已安裝清單、所有目前版覆寫層與三個語系政策的生效值（用這份清單正規化）。
/// 不取自 <see cref="LocaleRegistry"/> 的快照（可能落後 60 秒）。
/// </summary>
public sealed record LocaleWorld(IReadOnlyList<InstalledLocaleInfo> Installed, IReadOnlyDictionary<string, LocaleOverlay> Overlays,
    string DefaultLocale, IReadOnlyList<string> Enabled, IReadOnlyList<string> Required);

/// <summary>語系列套用後的欄位（新安裝，或既有的匯入語系有欄位變動時）。</summary>
public sealed record LocalePackLocale(string NativeName, string? QuasarLang, string? FallbackCode);

/// <summary>語系包的 <c>pack</c> 段（只顯示、存進版本列，不比對）。</summary>
public sealed record LocalePackInfo(string? Version, string? BuiltFor, string? BaseLocale, string? Note);

/// <summary>
/// <c>report.Yes</c>／<c>report.No</c> 在撞字篩選之前的意圖（第三輪必修 2）：<see cref="Remove"/> 為 false＝設成 <see cref="Text"/>；
/// true＝從覆寫層拿掉（Q6：和內建原文相同），生效的字仍是 <see cref="Text"/>。沒提到的鍵不在字典裡。
/// </summary>
public sealed record BooleanCandidate(string Text, bool Remove);

/// <summary>
/// 套用要寫的內容（存進預覽列的 <c>PlanJson</c>；i4d1 計畫判斷 8、11）：語系列欄位、兩段「這次要設的鍵→值」與「這次要拿掉的鍵」（Q6 改回內建原文的），
/// 是／否撞字拒收的鍵（<see cref="BooleanRejected"/>）與篩選前的候選值（<see cref="BooleanCandidates"/>，套用時從它重跑篩選）。
/// <see cref="Locale"/> 是 null＝語系列不動。
/// </summary>
public sealed record LocalePackPlan(
    string Code, bool IsNew, LocalePackLocale? Locale,
    IReadOnlyDictionary<string, string> UiSet, IReadOnlyList<string> UiRemove,
    IReadOnlyDictionary<string, string> ServerSet, IReadOnlyList<string> ServerRemove,
    IReadOnlyList<string> BooleanRejected, IReadOnlyDictionary<string, BooleanCandidate> BooleanCandidates,
    LocalePackInfo Pack)
{
    private readonly IReadOnlyList<string> _uiConfirm = [], _serverConfirm = [];

    /// <summary>
    /// 原樣再匯入的「原文已更新」句子（i4d2 計畫判斷 7、Q3）：套用時只換新記下的原文雜湊，譯文不變。i4d1 形狀的 <c>PlanJson</c> 沒有這欄，
    /// 反序列化成 null 時正規化成空清單。
    /// </summary>
    public IReadOnlyList<string> UiConfirm { get => _uiConfirm; init => _uiConfirm = value ?? []; }

    /// <inheritdoc cref="UiConfirm"/>
    public IReadOnlyList<string> ServerConfirm { get => _serverConfirm; init => _serverConfirm = value ?? []; }

    /// <summary>覆寫層要寫新版本（有句子要設、要拿掉或要確認；判斷 7）。只改語系列欄位不算。</summary>
    [JsonIgnore]
    public bool OverlayChanged =>
        UiSet.Count + UiRemove.Count + ServerSet.Count + ServerRemove.Count + UiConfirm.Count + ServerConfirm.Count > 0;

    /// <summary>語系列與覆寫層都沒有任何變動（套用時不寫新版本，只記稽核）。</summary>
    public bool NoChanges => Locale is null && !OverlayChanged;
}

/// <summary>一段（<c>ui</c> 或 <c>server</c>）的筆數；<see cref="ExtraKeys"/>、<see cref="RejectedItems"/> 只列前 <see cref="LocalePackLimits.MaxListedItems"/> 筆。</summary>
public sealed record SectionReport(int Added, int Changed, int Unchanged, int Skipped, int SameAsBuiltin, int Extra, IReadOnlyList<string> ExtraKeys,
    int Rejected, IReadOnlyList<RejectedItem> RejectedItems)
{
    /// <summary>與目前覆寫相同、而且原文已更新的句子數（套用即確認，i4d2 計畫判斷 7）；i4d1 形狀的報告沒有這欄，是 0。</summary>
    public int Confirmed { get; init; }
}

/// <summary>套用後「該語系自己有句子」的鍵數（覆寫層＋該語系的內建層，不含走退回鏈補的）。</summary>
public sealed record CompletenessCount(int Have, int Total);

/// <summary>
/// 完整度（<see cref="LocaleCompleteness.Of"/>；報告用套用後的覆寫層，清單用目前的）。<see cref="TopMissing"/> 是缺最多的前
/// <see cref="LocalePackLimits.MaxMissingCategories"/> 個分類（i4d2 計畫判斷 4）；i4d1 形狀的報告沒有這欄，正規化成空清單。
/// </summary>
public sealed record CompletenessReport(CompletenessCount Ui, CompletenessCount Server)
{
    private readonly IReadOnlyList<MissingCategory> _topMissing = [];

    public IReadOnlyList<MissingCategory> TopMissing { get => _topMissing; init => _topMissing = value ?? []; }
}

/// <summary>一個缺句的分類：<see cref="Segment"/> 是 <c>ui</c> 或 <c>server</c>；分類規則見 <see cref="LocaleCompleteness"/>。</summary>
public sealed record MissingCategory(string Segment, string Category, int Missing);

/// <summary>這次套用會清掉的已失效覆寫句數（i4d2 計畫判斷 8）；不寫新版本時兩個都是 0。</summary>
public sealed record OrphanedDroppedReport(int Ui, int Server);

public sealed record DesignDataReport(int Count);

/// <summary>警告（代碼＋參數，畫面用 <c>localePacks.warnings.&lt;代碼&gt;</c> 組字）。</summary>
public sealed record LocalePackWarning(string Code, IReadOnlyList<string> Args);

/// <summary>報告的 <c>locale</c> 段：套用後的欄位、這次會變動的欄位（<see cref="Changes"/>）與內建語系略過的欄位（<see cref="Ignored"/>，Q3）。</summary>
public sealed record LocaleReport(string Code, bool IsNew, bool Builtin, string NativeName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? QuasarLang,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FallbackCode,
    IReadOnlyList<string> Changes, IReadOnlyList<string> Ignored);

/// <summary>
/// 預覽報告（i4d1 計畫判斷 13 的形狀）。<see cref="PreviewId"/>、<see cref="ExpiresUtc"/> 在 Demo（不暫存）時是 null，明示輸出 null
/// （全站 JSON 設定是 <c>WhenWritingNull</c>）。分析器產生時檔名、雜湊與暫存欄位是空的，由預覽 handler 補上。
/// </summary>
public sealed record LocalePackReport(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PreviewId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? ExpiresUtc,
    bool ImportEnabled, string FileName, string FileHash,
    LocaleReport Locale, LocalePackInfo Pack, SectionReport Ui, SectionReport Server,
    CompletenessReport Completeness, DesignDataReport DesignData, IReadOnlyList<LocalePackWarning> Warnings)
{
    private readonly OrphanedDroppedReport _orphanedDropped = new(0, 0);

    /// <summary>由預覽 handler 補（分析器不碰原始列）；i4d1 形狀的報告沒有這欄，正規化成 (0, 0)。</summary>
    public OrphanedDroppedReport OrphanedDropped { get => _orphanedDropped; init => _orphanedDropped = value ?? new(0, 0); }
}

/// <summary>分析結果：<see cref="Error"/> 不是 null＝整包拒收（代碼、自稱、上層語系、A39），此時沒有報告與計畫。</summary>
public sealed record LocalePackAnalysis(LocalePackReport? Report, LocalePackPlan? Plan, MessageRef? Error);

/// <summary>套用時重驗（判斷 16 第 3 步）：<see cref="Invalid"/> → 400；<see cref="BooleanChanged"/> → 409（Q11）。</summary>
public sealed record LocalePackRecheck(MessageRef? Invalid, bool BooleanChanged);

/// <summary>
/// 一個撞字：同一個字同時是某些語系的「是」與某些語系的「否」（或固定別名）。<see cref="PackKeys"/> 是指定語系「自己的」
/// <c>report.Yes</c>／<c>report.No</c>（不是沿退回鏈繼承來的）剛好是這個字的那幾個鍵。
/// </summary>
public sealed record BooleanCollision(string Word, IReadOnlyList<string> YesLocales, IReadOnlyList<string> NoLocales, IReadOnlyList<string> PackKeys);

public sealed record BooleanCollisionResult(IReadOnlyList<BooleanCollision> Collisions);
