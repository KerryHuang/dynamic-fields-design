using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Domain.Localization;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// 語系包位元組 → <see cref="ParsedLocalePack"/>（語系包設計 §5.1、§13.1；i4d1 計畫判斷 1、2、5）。只做整包層級的檢查（格式、版本、代碼、
/// <c>locale</c>／<c>pack</c> 欄位、段落形狀、鍵數），逐句的語法交給 <see cref="LocalePackAnalyzer"/>。
/// 段落自己沿 <see cref="JsonElement.EnumerateObject"/> 走（不用 <c>UiTextMerge.Flatten</c>）：重複的屬性與巢狀／扁平撞在一起的路徑兩筆都不收（<c>duplicate</c>），
/// 非字串值記 <c>notString</c>，其他鍵照收。
/// </summary>
public static class LocalePackParser
{
    public const string Format = "formwright-langpack";
    public const int SupportedFormatVersion = 1;

    public static (ParsedLocalePack? Pack, MessageRef? Error) Parse(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        if (span.StartsWith("PK\u0003\u0004"u8)) return (null, LocalePackMessages.ExcelNotYet);
        if (span.StartsWith("﻿"u8)) bytes = bytes[3..];
        var first = FirstNonWhitespace(bytes.Span);
        if (first != (byte)'{') return (null, LocalePackMessages.NotJson);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = LocalePackLimits.MaxJsonDepth });
        }
        catch (JsonException)
        {
            return (null, ValidButTooDeep(bytes.Span) ? LocalePackMessages.TooDeep : LocalePackMessages.NotJson);
        }
        using (doc) return Read(doc.RootElement);
    }

    private static int FirstNonWhitespace(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return b;
        return -1;
    }

    /// <summary>深度上限之外都合法（JSON 層數太多，而不是壞檔）；更深的極端情況當成壞檔。</summary>
    private static bool ValidButTooDeep(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 1024 });
        try
        {
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static (ParsedLocalePack?, MessageRef?) Read(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
            return (null, LocalePackMessages.FormatUnknown);
        if (!root.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var v) || v < 1)
            return (null, LocalePackMessages.FormatUnknown);
        if (v > SupportedFormatVersion) return (null, LocalePackMessages.FormatTooNew(v, SupportedFormatVersion));

        if (!root.TryGetProperty("locale", out var locale) || locale.ValueKind == JsonValueKind.Null)
            return (null, LocalePackMessages.CodeInvalidFormat(""));
        if (locale.ValueKind != JsonValueKind.Object) return (null, LocalePackMessages.SectionNotObject("locale"));

        var rawCode = locale.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
        if (!LocaleCode.TryNormalize(rawCode, out var code)) return (null, LocalePackMessages.CodeInvalidFormat(rawCode));
        if (locale.TryGetProperty("direction", out var dir) && dir.ValueKind == JsonValueKind.String
            && string.Equals(dir.GetString(), "rtl", StringComparison.OrdinalIgnoreCase))
            return (null, LocalePackMessages.Rtl);

        if (!TextField(locale, "nativeName", "locale.nativeName", LocalePackLimits.MaxNativeName, out var nativeName, out var error)) return (null, error);
        string? quasar = null;
        if (locale.TryGetProperty("quasarLang", out var q) && q.ValueKind != JsonValueKind.Null)
        {
            if (q.ValueKind != JsonValueKind.String || !BuiltinLocaleResources.QuasarLangs.Contains(q.GetString()!))
                return (null, LocalePackMessages.QuasarUnknown(q.ValueKind == JsonValueKind.String ? q.GetString()! : q.GetRawText()));
            quasar = q.GetString();
        }
        string? fallback = null;
        if (locale.TryGetProperty("fallback", out var f) && f.ValueKind != JsonValueKind.Null)
        {
            var raw = f.ValueKind == JsonValueKind.String ? f.GetString()! : f.GetRawText();
            if (f.ValueKind != JsonValueKind.String || !LocaleCode.TryNormalize(raw, out var normalized))
                return (null, LocalePackMessages.CodeInvalidFormat(raw));
            fallback = normalized;
        }

        string? packVersion = null, builtFor = null, baseLocale = null, note = null;
        if (root.TryGetProperty("pack", out var pack) && pack.ValueKind != JsonValueKind.Null)
        {
            if (pack.ValueKind != JsonValueKind.Object) return (null, LocalePackMessages.SectionNotObject("pack"));
            if (!TextField(pack, "version", "pack.version", LocalePackLimits.MaxPackVersion, out packVersion, out error)
                || !TextField(pack, "builtFor", "pack.builtFor", LocalePackLimits.MaxPackBuiltFor, out builtFor, out error)
                || !TextField(pack, "baseLocale", "pack.baseLocale", LocalePackLimits.MaxPackBaseLocale, out baseLocale, out error)
                || !TextField(pack, "note", "pack.note", LocalePackLimits.MaxPackNote, out note, out error))
                return (null, error);
        }

        var ui = new Section();
        if (!ReadSection(root, "ui", nested: true, ui, BuiltinLocaleResources.BaseKeys.Count, out error)) return (null, error);
        var server = new Section();
        if (!ReadSection(root, "server", nested: false, server, BuiltinServerText.BaseKeys.Count, out error)) return (null, error);

        var designData = 0;
        if (root.TryGetProperty("designData", out var dd) && dd.ValueKind != JsonValueKind.Null)
        {
            if (dd.ValueKind != JsonValueKind.Object) return (null, LocalePackMessages.SectionNotObject("designData"));
            designData = dd.EnumerateObject().Count();
        }

        return (new ParsedLocalePack(code, nativeName, quasar, fallback, packVersion, builtFor, baseLocale, note,
            ui.Values, ui.Problems(), server.Values, server.Problems(), designData), null);
    }

    /// <summary>選填的文字欄：沒帶、null、空白當成沒帶；非字串、控制字元或超長是整包錯誤。</summary>
    private static bool TextField(JsonElement obj, string name, string label, int maxLength, out string? value, out MessageRef? error)
    {
        value = null;
        error = null;
        if (!obj.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return true;
        if (e.ValueKind != JsonValueKind.String || LocaleTextSafety.Check(e.GetString()!, maxLength) is not null)
        {
            error = LocalePackMessages.FieldInvalid(label, maxLength);
            return false;
        }
        value = string.IsNullOrWhiteSpace(e.GetString()) ? null : e.GetString();
        return true;
    }

    private static bool ReadSection(JsonElement root, string name, bool nested, Section section, int baseCount, out MessageRef? error)
    {
        error = null;
        if (!root.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return true;
        if (e.ValueKind != JsonValueKind.Object)
        {
            error = LocalePackMessages.SectionNotObject(name);
            return false;
        }
        Walk(e, "", nested, section);
        var max = baseCount * LocalePackLimits.MaxKeysFactor;
        if (section.Count > max)
        {
            error = LocalePackMessages.TooManyKeys(name, section.Count, max);
            return false;
        }
        return true;
    }

    private static void Walk(JsonElement obj, string prefix, bool nested, Section section)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var path = prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name;
            if (nested && prop.Value.ValueKind == JsonValueKind.Object) Walk(prop.Value, path, nested, section);
            else section.Add(path, prop.Value);
        }
    }

    /// <summary>一段的累計：壓平後的路徑第二次出現就兩筆都不收。</summary>
    private sealed class Section
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal), _duplicate = new(StringComparer.Ordinal), _notString = new(StringComparer.Ordinal);

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public int Count => _seen.Count;

        public void Add(string path, JsonElement value)
        {
            if (_duplicate.Contains(path)) return;
            if (!_seen.Add(path))
            {
                _duplicate.Add(path);
                Values.Remove(path);
                _notString.Remove(path);
                return;
            }
            if (value.ValueKind == JsonValueKind.String) Values[path] = value.GetString()!;
            else _notString.Add(path);
        }

        public IReadOnlyList<RejectedItem> Problems() =>
            _duplicate.Select(k => new RejectedItem(k, LocalePackReasons.Duplicate, []))
                .Concat(_notString.Select(k => new RejectedItem(k, LocalePackReasons.NotString, [])))
                .OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
    }
}
