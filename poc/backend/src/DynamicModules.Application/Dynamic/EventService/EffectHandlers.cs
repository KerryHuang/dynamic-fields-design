using System.Text.Json;

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
}

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
