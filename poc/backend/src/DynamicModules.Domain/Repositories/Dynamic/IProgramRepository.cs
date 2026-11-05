using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IProgramRepository
{
    /// <summary>DYN 開頭最大代碼加一；尚無時從 DYN00001 起（DYN00000 保留給設計器）。</summary>
    Task<string> NextModuleProgramCodeAsync(CancellationToken ct);
    Task<DynamicProgram?> GetAsync(string programCode, CancellationToken ct);
    void Add(DynamicProgram p);
}
