using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Localization;

namespace DynamicModules.Application.Dynamic.LocaleService;

/// <summary>
/// 語系包匯入與語系管理回給使用者的訊息（語系包 i4d1 計畫判斷 18）：文字在 <see cref="MessageText"/> 四語表，這裡只組鍵與參數。
/// 機器碼（<c>ServiceResult.Code</c>）也集中在這裡（判斷 12 的錯誤碼表）。
/// </summary>
public static class LocalePackMessages
{
    /// <summary>判斷 12 的機器碼（camelCase，前端只認碼）。</summary>
    public const string CodeImportDisabled = "localePackImportDisabled", CodePreviewExpired = "localePackPreviewExpired",
        CodeStale = "localePackStale", CodeBooleanCollision = "localeBooleanCollision", CodeNoPrevious = "localeNoPrevious",
        CodeNoOverrides = "localeNoOverrides", CodeBuiltinNotRemovable = "localeBuiltinNotRemovable", CodeInUse = "localeInUse",
        CodeIsFallback = "localeIsFallback", CodeNotFound = "localeNotFound", CodeInvalid = "localePackInvalid";

    /// <summary>整包層級錯誤所在的欄位鍵（400 的 <c>errors.file</c>）。</summary>
    public const string FileField = "file";

    public static MessageRef ImportDisabled => MessageRef.Of(MessageText.LocalePackImportDisabled);

    /// <summary>上限以 MB 顯示，最多三位小數（2048 位元組 → 0.002）。</summary>
    public static MessageRef TooLarge(long bytes) =>
        MessageRef.Of(MessageText.LocalePackTooLarge, (bytes / 1048576.0).ToString("0.###", CultureInfo.InvariantCulture));

    public static MessageRef NotJson => MessageRef.Of(MessageText.LocalePackNotJson);
    public static MessageRef ExcelNotYet => MessageRef.Of(MessageText.LocalePackExcelNotYet);
    public static MessageRef FormatUnknown => MessageRef.Of(MessageText.LocalePackFormatUnknown);
    public static MessageRef FormatTooNew(long version, int supported) => MessageRef.Of(MessageText.LocalePackFormatTooNew, version, supported);
    public static MessageRef TooDeep => MessageRef.Of(MessageText.LocalePackTooDeep, LocalePackLimits.MaxJsonDepth);
    public static MessageRef SectionNotObject(string section) => MessageRef.Of(MessageText.LocalePackSectionNotObject, section);
    public static MessageRef TooManyKeys(string section, int count, int max) => MessageRef.Of(MessageText.LocalePackTooManyKeys, section, count, max);
    public static MessageRef Rtl => MessageRef.Of(MessageText.LocalePackRtl);
    public static MessageRef CodeInvalidFormat(string raw) => MessageRef.Of(MessageText.LocalePackCodeInvalid, raw);
    public static MessageRef NativeNameRequired => MessageRef.Of(MessageText.LocalePackNativeNameRequired);
    public static MessageRef FieldInvalid(string field, int maxLength) => MessageRef.Of(MessageText.LocalePackFieldInvalid, field, maxLength);
    public static MessageRef QuasarUnknown(string lang) => MessageRef.Of(MessageText.LocalePackQuasarUnknown, lang);
    public static MessageRef FallbackNotInstalled(string code) => MessageRef.Of(MessageText.LocaleFallbackNotInstalled, code);
    public static MessageRef FallbackSelf => MessageRef.Of(MessageText.LocaleFallbackSelf);
    public static MessageRef FallbackCycle(IEnumerable<string> path) => MessageRef.Of(MessageText.LocaleFallbackCycle, string.Join(" → ", path));
    public static MessageRef FallbackTooDeep => MessageRef.Of(MessageText.LocaleFallbackTooDeep, LocaleChain.MaxParentDepth);
    public static MessageRef MaxInstalled(int max) => MessageRef.Of(MessageText.LocaleMaxInstalled, max);
    public static MessageRef PreviewExpired => MessageRef.Of(MessageText.LocalePackPreviewExpired);
    public static MessageRef Stale => MessageRef.Of(MessageText.LocalePackStale);
    public static MessageRef NotFound => MessageRef.Of(MessageText.LocaleNotFound);
    public static MessageRef BuiltinNotRemovable => MessageRef.Of(MessageText.LocaleBuiltinNotRemovable);
    public static MessageRef InUse => MessageRef.Of(MessageText.LocaleInUse);
    /// <summary>多個下游語系的自稱用「、」接（判斷 19）。</summary>
    public static MessageRef IsFallback(IEnumerable<string> nativeNames) => MessageRef.Of(MessageText.LocaleIsFallback, string.Join("、", nativeNames));
    public static MessageRef NoPrevious => MessageRef.Of(MessageText.LocaleNoPrevious);
    public static MessageRef NoOverrides => MessageRef.Of(MessageText.LocaleNoOverrides);
    public static MessageRef BooleanCollision(string locale, string other, string word) => MessageRef.Of(MessageText.LocaleBooleanCollision, locale, other, word);
    public static MessageRef TemplateLocaleNotInstalled(string code) => MessageRef.Of(MessageText.LocaleTemplateLocaleNotInstalled, code);
    public static MessageRef TemplateScopeInvalid => MessageRef.Of(MessageText.LocaleTemplateScopeInvalid);
}
