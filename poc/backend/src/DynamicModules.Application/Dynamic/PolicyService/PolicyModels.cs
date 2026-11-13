using System.Globalization;
using System.Text.Json;
using DynamicModules.Domain.Policies;

namespace DynamicModules.Application.Dynamic.PolicyService;

/// <summary>系統政策一列。Value 沒設時為 null（序列化時省略）；Min／Max 只在 Integer、CodeList，Options 只在 Choice、RoleSet。</summary>
public sealed record SystemPolicyResponse(
    string Code, string Category, PolicyKind Kind, PolicyScope Scope, string? Unit, int? Min, int? Max, IReadOnlyList<string>? Options,
    JsonElement Factory, JsonElement? Value, JsonElement Effective);

/// <summary>模組政策一列。SystemValue 是全系統生效值（全系統值或出廠值）；Override 沒設時為 null。</summary>
public sealed record ModulePolicyResponse(
    string Code, string Category, PolicyKind Kind, string? Unit, int? Min, int? Max, IReadOnlyList<string>? Options,
    JsonElement SystemValue, JsonElement? Override, JsonElement Effective);

/// <summary>全部政策（含 system 範圍）的生效值，以代碼為鍵。</summary>
public sealed record EffectivePoliciesResponse(string? ModuleKey, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>政策標準字串與 API JSON 值之間的轉換。</summary>
public static class PolicyJson
{
    public static JsonElement ToJson(PolicyDefinition def, string canonical) => def.Kind switch
    {
        PolicyKind.Integer => JsonSerializer.SerializeToElement(int.Parse(canonical, NumberStyles.None, CultureInfo.InvariantCulture)),
        PolicyKind.Boolean => JsonSerializer.SerializeToElement(canonical == "true"),
        PolicyKind.Choice => JsonSerializer.SerializeToElement(canonical),
        _ => JsonSerializer.SerializeToElement(canonical.Length == 0 ? [] : canonical.Split(',')),
    };

    /// <summary>JSON 型別不對回 null，其餘交給 <see cref="PolicyDefinition.Normalize"/>。</summary>
    public static string? FromJson(PolicyDefinition def, JsonElement value)
    {
        string? raw = def.Kind switch
        {
            PolicyKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
                ? n.ToString(CultureInfo.InvariantCulture) : null,
            PolicyKind.Boolean => value.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null },
            PolicyKind.Choice => value.ValueKind == JsonValueKind.String ? value.GetString() : null,
            _ => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                ? string.Join(",", value.EnumerateArray().Select(e => e.GetString())) : null,
        };
        return def.Normalize(raw);
    }

    /// <summary>CodeList 的值裡落在寫死黑名單的項目（正規化後）；沒有黑名單或型別不對回空清單。</summary>
    public static IReadOnlyList<string> ForbiddenItems(PolicyDefinition def, JsonElement value) =>
        def.Forbidden is null || value.ValueKind != JsonValueKind.Array || !value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
            ? []
            : def.ForbiddenIn(string.Join(",", value.EnumerateArray().Select(e => e.GetString())));

    public static bool HasRange(PolicyDefinition def) => def.Kind is PolicyKind.Integer or PolicyKind.CodeList or PolicyKind.Recipients;

    public static bool HasOptions(PolicyDefinition def) => def.Kind is PolicyKind.Choice or PolicyKind.RoleSet;

    public static SystemPolicyResponse System(PolicyDefinition def, PolicyResolution r) => new(
        def.Code, def.Category, def.Kind, def.Scope, def.Unit,
        HasRange(def) ? def.Min : null, HasRange(def) ? def.Max : null, HasOptions(def) ? def.Options : null,
        ToJson(def, r.Factory), r.System is null ? null : ToJson(def, r.System), ToJson(def, r.System ?? r.Factory));

    public static ModulePolicyResponse Module(PolicyDefinition def, PolicyResolution r) => new(
        def.Code, def.Category, def.Kind, def.Unit,
        HasRange(def) ? def.Min : null, HasRange(def) ? def.Max : null, HasOptions(def) ? def.Options : null,
        ToJson(def, r.System ?? r.Factory), r.Override is null ? null : ToJson(def, r.Override), ToJson(def, r.Effective));
}
