using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocaleTemplate;

/// <summary>
/// 匯出 JSON 翻譯樣板（<c>GET i18n/locales/{code}/template</c>；語系包設計 §7.1、i4d2 計畫判斷 9）：唯讀、只限系統管理員（<c>SystemAdminBehavior</c>，fail closed）。
/// 不是交易型請求、不稽核（設計 §11 的稽核動作不含匯出），也不看 Demo 開關（設計 §12：Demo 可匯出）。
/// </summary>
public sealed record GetLocaleTemplateRequest(LocaleTemplateOptions Options) : IRequest<ServiceResult>, ISystemAdminRequest;

/// <summary>
/// 匯出選項（查詢參數原樣，驗證在 <c>LocaleTemplateBuilder.Validate</c>）：<see cref="Scopes"/> 是 <c>ui</c>、<c>server</c>（至少一個）；
/// <see cref="Reference"/> 空字串＝不附參考段；<see cref="NativeName"/>、<see cref="Fallback"/>、<see cref="QuasarLang"/> 只在目標語系尚未安裝時使用。
/// </summary>
public sealed record LocaleTemplateOptions(
    string Code, IReadOnlyList<string> Scopes, bool OnlyMissing, bool OnlyBaseChanged, string Source, string Reference,
    string? NativeName = null, string? Fallback = null, string? QuasarLang = null)
{
    /// <summary>原文語系的預設（設計 §7.1：預設繁中）。</summary>
    public const string DefaultSource = "zh-TW";
    /// <summary>參考語系的預設（設計 §7.1：預設英文）。</summary>
    public const string DefaultReference = "en-US";
}

/// <summary>樣板檔（<c>&lt;代碼&gt;.langpack.json</c>，UTF-8 JSON）。</summary>
public sealed record LocaleTemplateFile(string FileName, byte[] Content);
