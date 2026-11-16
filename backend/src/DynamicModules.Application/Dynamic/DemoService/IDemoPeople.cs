namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>Demo 產生器的示範使用者、群組、模具、客戶；實作在 Persistence。</summary>
public interface IDemoPeople
{
    /// <summary>已存在的項目，每項一則訊息；空清單表示可以新增。</summary>
    Task<IReadOnlyList<string>> ConflictsAsync(CancellationToken ct);

    Task AddAsync(CancellationToken ct);
}
