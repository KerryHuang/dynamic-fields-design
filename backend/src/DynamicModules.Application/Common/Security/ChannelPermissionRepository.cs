using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 權限輸入的通道裝飾（mcp1a 計畫判斷 10 第 2 點）：MCP 通道時 <see cref="LoadInputsAsync"/> 回傳的輸入標 <c>AiMasked</c>，
/// <see cref="PermissionResolver"/> 因此把「不提供給 AI」的欄位當隱藏欄位。所有經 <see cref="IPermissionRepository"/> 的權限解析
/// （guard、簽核臨時讀取權、簽核歷程遮蔽、明細）都吃到同一個旗標。<see cref="LoadProgramInputsAsync"/> 不加：程式層動作軸不涉欄位。
/// </summary>
public sealed class ChannelPermissionRepository(IPermissionRepository inner, IRequestChannel channel) : IPermissionRepository
{
    /// <summary>被裝飾的原實作（註冊測試用）。</summary>
    public IPermissionRepository Inner => inner;

    public async Task<PermissionInputs?> LoadInputsAsync(string userId, Module module, CancellationToken ct)
    {
        var inputs = await inner.LoadInputsAsync(userId, module, ct);
        return inputs is not null && channel.Channel == RequestChannel.Mcp ? inputs with { AiMasked = true } : inputs;
    }

    public Task<PermissionInputs?> LoadProgramInputsAsync(string userId, string programCode, CancellationToken ct) =>
        inner.LoadProgramInputsAsync(userId, programCode, ct);

    public Task<List<PermissionGroup>> ListGroupsAsync(CancellationToken ct) => inner.ListGroupsAsync(ct);
    public Task<List<UserGroupMapping>> ListMembershipsAsync(CancellationToken ct) => inner.ListMembershipsAsync(ct);
    public Task<List<AppUser>> ListUsersAsync(CancellationToken ct) => inner.ListUsersAsync(ct);
    public Task<AppUser?> GetUserAsync(string userId, CancellationToken ct) => inner.GetUserAsync(userId, ct);
    public Task<ModulePermissionRows> LoadModuleRowsAsync(Module module, CancellationToken ct) => inner.LoadModuleRowsAsync(module, ct);
    public Task ReplaceModuleRowsAsync(Module module, ModulePermissionRows rows, CancellationToken ct) => inner.ReplaceModuleRowsAsync(module, rows, ct);
    public Task<SystemPrivilegeRows> LoadSystemPrivilegeRowsAsync(CancellationToken ct) => inner.LoadSystemPrivilegeRowsAsync(ct);
    public Task ReplaceSystemRowsAsync(long? groupId, string? userId, IReadOnlyList<ProgramPrivilege> desired, CancellationToken ct) =>
        inner.ReplaceSystemRowsAsync(groupId, userId, desired, ct);
}

public static class ChannelPermissionServiceCollectionExtensions
{
    /// <summary>
    /// 把最後一筆 <see cref="IPermissionRepository"/> 包上 <see cref="ChannelPermissionRepository"/>，lifetime 不變。
    /// 必須在資料庫 provider 註冊之後呼叫（provider 經 AddPersistenceRepositories 註冊原實作，放在 AddApplication 會被蓋掉）。
    /// </summary>
    public static IServiceCollection AddChannelPermissionMasking(this IServiceCollection services)
    {
        ServiceCollectionDecoration.Decorate<IPermissionRepository>(services,
            (sp, inner) => new ChannelPermissionRepository(inner, sp.GetRequiredService<IRequestChannel>()), nameof(AddChannelPermissionMasking));
        return services;
    }
}
