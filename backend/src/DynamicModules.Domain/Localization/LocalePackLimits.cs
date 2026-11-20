namespace DynamicModules.Domain.Localization;

/// <summary>
/// 語系包的技術上限（語系包設計 §13.1、§14 可寫死清單；i4d1 計畫判斷 5）。一處宣告，Application 與測試都引用這裡。
/// 檔案大小不在這裡：那是政策 A38 <c>locale.packMaxBytes</c>。
/// </summary>
public static class LocalePackLimits
{
    /// <summary>每句（介面文字、後端範本）最長字元數。</summary>
    public const int MaxTextLength = 2000;
    public const int MaxPackVersion = 50;
    public const int MaxPackNote = 500;
    public const int MaxPackBuiltFor = 20;
    public const int MaxPackBaseLocale = LocaleCode.MaxLength;
    /// <summary>語系自稱長度（<c>Dynamic_Locale.NativeName</c> 欄長）。</summary>
    public const int MaxNativeName = 50;
    /// <summary><c>ui</c>、<c>server</c> 各段鍵數超過該段基準鍵數的這個倍數就整包拒收。</summary>
    public const int MaxKeysFactor = 2;
    /// <summary>JSON 最大深度。</summary>
    public const int MaxJsonDepth = 16;
    /// <summary>預覽暫存保留時間。</summary>
    public static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(10);
    /// <summary>全系統同時暫存的預覽筆數上限（超過時最舊的先丟）。</summary>
    public const int MaxPreviews = 5;
    /// <summary>報告的拒收清單與多餘鍵清單只列前幾筆。</summary>
    public const int MaxListedItems = 50;
}

/// <summary>逐句拒收原因代碼（報告與前端 i18n 鍵 <c>localePacks.reasons.&lt;代碼&gt;</c> 共用）。</summary>
public static class LocalePackReasons
{
    public const string Syntax = "syntax", Placeholder = "placeholder", FormatClause = "formatClause", Control = "control",
        TooLong = "tooLong", NotString = "notString", Duplicate = "duplicate", BooleanCollision = "booleanCollision";
}
