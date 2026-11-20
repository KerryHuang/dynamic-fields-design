using System.Text.Encodings.Web;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocaleTemplate;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// JSON 翻譯樣板（語系包設計 §5.1、§7.1；i4d2 計畫判斷 9、10），純函式。輸出可直接再匯入：Parser 只讀 <c>format</c>、<c>formatVersion</c>、
/// <c>locale</c>、<c>pack</c>、<c>ui</c>、<c>server</c>，<c>source</c>、<c>reference</c>、<c>review</c> 三段匯入時略過。
/// <list type="bullet">
/// <item><c>ui</c>／<c>server</c>：目標語系自己的句子（覆寫層 → 該語系內建層，不含退回鏈補的），沒有是空字串；扁平鍵、Ordinal 排序；不在範圍的段整段不寫。</item>
/// <item>篩選：都不勾＝全部基準鍵；<c>onlyMissing</c>＝自己沒有句子；<c>onlyBaseChanged</c>＝原文已更新 ∪ 參數不符（完整集合）；都勾取聯集。</item>
/// <item><c>source</c>／<c>reference</c>：同一組鍵沿該語系自己的退回鏈取第一個有值的層；<c>reference</c> 空字串不寫。</item>
/// <item><c>review</c>：只在目標語系有目前版時寫，<c>orphaned</c> 帶舊譯文。</item>
/// </list>
/// </summary>
public static class LocaleTemplateBuilder
{
    private static readonly JsonWriterOptions Writer = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>都回 400 <c>localePackInvalid</c>（訊息在 <c>errors.file</c>）；上層語系圖沿用匯入的 <see cref="LocalePackAnalyzer.Graph"/>，匯出成功的樣板匯入不會因此被拒。</summary>
    public static MessageRef? Validate(LocaleWorld world, LocaleTemplateOptions options)
    {
        if (!LocaleCode.TryNormalize(options.Code, out var code)) return LocalePackMessages.CodeInvalidFormat(options.Code);
        if (options.Scopes.Count == 0 || options.Scopes.Any(s => s is not (LocaleWorldText.Ui or LocaleWorldText.Server)))
            return LocalePackMessages.TemplateScopeInvalid;
        bool Installed(string c) => world.Installed.Any(i => i.Code == c);
        if (!Installed(code))
        {
            if (string.IsNullOrWhiteSpace(options.NativeName)) return LocalePackMessages.NativeNameRequired;
            if (LocaleTextSafety.Check(options.NativeName, LocalePackLimits.MaxNativeName) is not null)
                return LocalePackMessages.FieldInvalid("locale.nativeName", LocalePackLimits.MaxNativeName);
            if (options.QuasarLang is { } q && !BuiltinLocaleResources.QuasarLangs.Contains(q)) return LocalePackMessages.QuasarUnknown(q);
            string? fallback = null;
            if (options.Fallback is { } rawFallback)
            {
                if (!LocaleCode.TryNormalize(rawFallback, out var normalized)) return LocalePackMessages.CodeInvalidFormat(rawFallback);
                fallback = normalized;
            }
            var pairs = world.Installed.Select(i => (i.Code, i.FallbackCode)).Append((code, fallback)).ToList();
            if (LocalePackAnalyzer.Graph(pairs, code) is { } graphError) return graphError;
        }
        if (!Installed(options.Source)) return LocalePackMessages.TemplateLocaleNotInstalled(options.Source);
        if (options.Reference.Length > 0 && !Installed(options.Reference)) return LocalePackMessages.TemplateLocaleNotInstalled(options.Reference);
        return null;
    }

    /// <summary>先過 <see cref="Validate"/>。回檔名 <c>&lt;正規化代碼&gt;.langpack.json</c> 與 UTF-8 內容。</summary>
    public static (string FileName, byte[] Content) Build(LocaleWorld world, IReadOnlyDictionary<string, LocalePack> current,
        LocaleTemplateOptions options, string? platformVersion)
    {
        if (!LocaleCode.TryNormalize(options.Code, out var code)) throw new ArgumentException("先呼叫 Validate", nameof(options));
        var info = world.Installed.FirstOrDefault(i => i.Code == code);
        var row = info is null ? null : current.GetValueOrDefault(code);
        var review = LocaleOverlayReview.Of(row);
        var segments = new[] { LocaleWorldText.Ui, LocaleWorldText.Server }.Where(options.Scopes.Contains).ToList();
        var keys = segments.ToDictionary(s => s, s => Keys(world, code, s, s == LocaleWorldText.Ui ? review.Ui : review.Server, options));

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Writer))
        {
            w.WriteStartObject();
            w.WriteString("format", LocalePackParser.Format);
            w.WriteNumber("formatVersion", LocalePackParser.SupportedFormatVersion);
            w.WriteStartObject("locale");
            w.WriteString("code", code);
            if (info is not null)
            {
                w.WriteString("nativeName", info.NativeName);
                WriteNullable(w, "quasarLang", info.QuasarLang);
                WriteNullable(w, "fallback", info.FallbackCode);
            }
            else
            {
                w.WriteString("nativeName", options.NativeName);
                WriteNullable(w, "quasarLang", options.QuasarLang);
                WriteNullable(w, "fallback", options.Fallback is { } f && LocaleCode.TryNormalize(f, out var nf) ? nf : null);
            }
            w.WriteEndObject();
            w.WriteStartObject("pack");
            w.WriteString("builtFor", LocalePackVersions.PlatformBuiltFor(platformVersion));
            w.WriteString("baseLocale", options.Source);
            w.WriteEndObject();

            foreach (var s in segments)
                WriteTexts(w, s, keys[s], k => info is null ? "" : LocaleWorldText.OwnText(world, code, s, k) ?? "");
            WriteResolved(w, "source", world, options.Source, segments, keys);
            if (options.Reference.Length > 0) WriteResolved(w, "reference", world, options.Reference, segments, keys);
            if (row is not null) WriteReview(w, row, review, segments);
            w.WriteEndObject();
        }
        return (code + ".langpack.json", stream.ToArray());
    }

    private static List<string> Keys(LocaleWorld world, string code, string segment, OverlayReviewSection review, LocaleTemplateOptions options)
    {
        var all = segment == LocaleWorldText.Ui ? BuiltinLocaleResources.BaseKeys : BuiltinServerText.BaseKeys;
        IEnumerable<string> keys = all;
        if (options.OnlyMissing || options.OnlyBaseChanged)
            keys = all.Where(k => (options.OnlyMissing && LocaleWorldText.OwnText(world, code, segment, k) is null)
                || (options.OnlyBaseChanged && (review.AllBaseChanged.Contains(k) || review.AllParamsChanged.Contains(k))));
        return keys.Order(StringComparer.Ordinal).ToList();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void WriteTexts(Utf8JsonWriter w, string name, IEnumerable<string> keys, Func<string, string> text)
    {
        w.WriteStartObject(name);
        foreach (var k in keys) w.WriteString(k, text(k));
        w.WriteEndObject();
    }

    private static void WriteResolved(Utf8JsonWriter w, string name, LocaleWorld world, string locale, IReadOnlyList<string> segments,
        IReadOnlyDictionary<string, List<string>> keys)
    {
        var chain = LocaleWorldText.ChainOf(world, locale);
        w.WriteStartObject(name);
        w.WriteString("locale", locale);
        foreach (var s in segments) WriteTexts(w, s, keys[s], k => LocaleWorldText.Resolve(world, chain, s, k).Text);
        w.WriteEndObject();
    }

    private static void WriteReview(Utf8JsonWriter w, LocalePack row, OverlayReview review, IReadOnlyList<string> segments)
    {
        OverlayReviewSection Of(string s) => s == LocaleWorldText.Ui ? review.Ui : review.Server;
        void Lists(string name, Func<OverlayReviewSection, IEnumerable<string>> pick)
        {
            w.WriteStartObject(name);
            foreach (var s in segments)
            {
                w.WriteStartArray(s);
                foreach (var k in pick(Of(s)).Order(StringComparer.Ordinal)) w.WriteStringValue(k);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteStartObject("review");
        Lists("baseChanged", r => r.AllBaseChanged);
        Lists("paramsChanged", r => r.AllParamsChanged);
        Lists("runtimeFallback", r => r.AllRuntimeFallback);
        w.WriteStartObject("orphaned");
        foreach (var s in segments)
        {
            w.WriteStartObject(s);
            foreach (var (k, text) in LocaleOverlayReview.OrphanedTexts(row, s)) w.WriteString(k, text);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
