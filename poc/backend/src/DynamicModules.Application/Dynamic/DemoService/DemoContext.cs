using System.Text.Json;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>一次產生用的共用資料：今天、送指令的 ISender、「MES 模具廠」套件的 id 對照、群組 id。</summary>
public sealed class DemoContext(DateOnly today, ISender sender, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds)
{
    public DateOnly Today { get; } = today;
    public ISender Sender { get; } = sender;
    public TemplateIdMap Ids { get; } = ids;
    public IReadOnlyDictionary<string, long> GroupIds { get; } = groupIds;

    /// <summary>每一步開始前呼叫：整個產生過程共用同一個 scope，要把「每個請求一份」的狀態（寫入日誌、事件額度）歸零，讓每一步都像獨立的請求。</summary>
    public Action? BeginStep { get; init; }

    /// <summary>「CRM 與銷售」應用程式的對照（它的模組代碼 order 等與 MES 重名，所以另放一份）；沒有開啟時是 null。</summary>
    public DemoContext? Crm { get; set; }

    /// <summary>「進銷存」應用程式的對照（模組代碼 customer、shipment 等與 MES 重名，所以另放一份）；沒有開啟時是 null。</summary>
    public DemoContext? Inventory { get; set; }

    /// <summary>限定名 &lt;模組代號&gt;.&lt;欄位代號&gt; → Field.Key。</summary>
    public string Key(string qualified) => Ids.FieldKeys[qualified];

    /// <summary>選項值 "opt_&lt;id&gt;"。</summary>
    public JsonElement Opt(string module, string code) => JsonSerializer.SerializeToElement(OptValue(module, code));

    /// <summary>多選值：選項值陣列。</summary>
    public JsonElement Opts(string module, params string[] codes) =>
        JsonSerializer.SerializeToElement(codes.Select(c => OptValue(module, c)).ToArray());

    /// <summary>記錄值：鍵換成 Field.Key；值是 JsonElement 時原樣放，其他值序列化。</summary>
    public Dictionary<string, JsonElement?> Values(string module, params (string Code, object? Value)[] items) =>
        items.ToDictionary(i => Key($"{module}.{i.Code}"),
            i => (JsonElement?)(i.Value is JsonElement e ? e : JsonSerializer.SerializeToElement(i.Value)));

    private string OptValue(string module, string code) => $"opt_{Ids.OptionIds[$"{module}.{code}"]}";
}
