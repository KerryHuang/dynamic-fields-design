using System.Text.Json;
using DynamicModules.Application.Common;

namespace DynamicModules.Application.Dynamic.EventService;

/// <summary>擴充點：宣告式事件之外，外掛可以在效果裡做別的事（設計 §4.9）。目前沒有任何內建實作，也沒有腳本執行環境。</summary>
public interface IEffectHandler
{
    /// <summary>例如 "notify-webhook"，^[a-z][a-z0-9-]{1,39}$。</summary>
    string Name { get; }

    /// <summary>定義時呼叫；回傳錯誤訊息清單，空代表通過。</summary>
    IReadOnlyList<string> ValidateArgs(JsonElement? args);

    /// <summary>
    /// 依 args 宣告會寫哪些模組（不寫資料就回空清單）。定義時用來建循環圖的邊；
    /// 執行時受限寫入器只放行宣告過的組合，宣告以外的寫入立刻失敗並整筆回滾。
    /// </summary>
    IReadOnlyList<EffectWrite> DeclareWrites(JsonElement? args);

    /// <summary>執行效果。只能透過 <see cref="EffectContext.Writer"/> 寫資料，寫入器只放行 <see cref="DeclareWrites"/> 宣告過的組合。</summary>
    Task<EffectResult> ApplyAsync(EffectContext context, CancellationToken ct);
}

public sealed record EffectResult(string? Error)
{
    public static readonly EffectResult Ok = new((string?)null);
    public static EffectResult Fail(string error) => new(error);
}

/// <summary>受限寫入器：與內建效果同一條寫入路徑（系統身分、同一個交易），所以同樣進寫入日誌、受深度與總量約束。</summary>
public interface IEffectWriter
{
    Task<ServiceResult> CreateAsync(string moduleKey, Dictionary<string, JsonElement?> values, CancellationToken ct);
    Task<ServiceResult> UpdateAsync(string moduleKey, long recordId, Dictionary<string, JsonElement?> values, CancellationToken ct);
}

public sealed record EffectContext(
    string SourceModuleKey, long SourceRecordId, IReadOnlyDictionary<string, JsonElement?> Source, JsonElement? Args,
    string OperationId, int Depth, IEffectWriter Writer);

/// <summary>Create 為 false 代表更新；TouchesStatus：更新時會不會改該模組的狀態欄位。</summary>
public record EffectWrite(string ModuleKey, bool Create, bool TouchesStatus = false);

public interface IEffectHandlerRegistry
{
    IEffectHandler? Find(string? name);
}

public class EffectHandlerRegistry(IEnumerable<IEffectHandler> handlers) : IEffectHandlerRegistry
{
    private readonly Dictionary<string, IEffectHandler> _byName = handlers.ToDictionary(h => h.Name);

    public IEffectHandler? Find(string? name) => name is not null && _byName.TryGetValue(name, out var h) ? h : null;
}
