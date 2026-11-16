using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.Integrations;

/// <summary>整合的狀態（設計 §5.1）。只由當下條件推導，Dynamic_AppIntegration.Status 只是上一次調和的結果。</summary>
public static class IntegrationStatus
{
    public const string Disabled = "Disabled", Waiting = "Waiting", Active = "Active", Suspended = "Suspended", Held = "Held", Blocked = "Blocked";
}

/// <summary>由條件推導的目標，尚未考慮完整性檢查與安裝是否成功。</summary>
public enum IntegrationTarget { Disabled, Waiting, Install, Run, Suspended, Held }

public static class IntegrationState
{
    /// <summary>設計 §5.1 的狀態表。<paramref name="holdOnPartnerClosed"/> 為 true 代表 onPartnerClosed = block。</summary>
    public static IntegrationTarget Derive(bool enabled, bool installed, bool ownerOpen, bool partnersPresent, bool holdOnPartnerClosed)
    {
        if (!enabled) return IntegrationTarget.Disabled;
        if (!installed) return ownerOpen && partnersPresent ? IntegrationTarget.Install : IntegrationTarget.Waiting;
        if (!ownerOpen) return IntegrationTarget.Suspended;
        if (partnersPresent) return IntegrationTarget.Run;
        return holdOnPartnerClosed ? IntegrationTarget.Held : IntegrationTarget.Suspended;
    }
}

/// <summary>整合建立並登記的東西（Dynamic_AppIntegration.ItemsJson）。</summary>
public sealed record IntegrationItems(List<IntegrationFieldItem>? Fields, List<string>? Events, List<string>? Checks)
{
    public List<IntegrationFieldItem> Fields { get; init; } = Fields ?? [];
    public List<string> Events { get; init; } = Events ?? [];
    public List<string> Checks { get; init; } = Checks ?? [];

    /// <summary>整合自己停用（Suspended、Disabled）當下原本啟用的事件與檢查；恢復時只還原這些，客戶自己停用的不被打開（設計 §4）。</summary>
    public IntegrationSuspendedItems? SuspendedActive { get; init; }

    public static IntegrationItems Empty => new([], [], []);

    public static IntegrationItems Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? Empty : JsonSerializer.Deserialize<IntegrationItems>(json, Options) ?? Empty;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

public sealed record IntegrationSuspendedItems(List<string>? Events, List<string>? Checks)
{
    public List<string> Events { get; init; } = Events ?? [];
    public List<string> Checks { get; init; } = Checks ?? [];
}

public sealed record IntegrationFieldItem(string Module, string Code, long FieldId);

public static class IntegrationDeclarations
{
    public static List<AppIntegrationDecl> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<AppIntegrationDecl>>(json, AppJson.StrictOptions) ?? [];

    public static string ToJson(IEnumerable<AppIntegrationDecl> decls) => JsonSerializer.Serialize(decls, AppJson.StrictOptions);

    public static AppIntegrationDecl? Find(App app, string code) => Parse(app.IntegrationsJson).FirstOrDefault(d => d.Code == code);
}
