using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>通過時 Permissions 有值；否則 Denied 是 Forbidden。</summary>
public sealed record AccessResult(EffectivePermissions? Permissions, ServiceResult? Denied);

public interface IDynamicAccessGuard
{
    /// <summary>
    /// 先查 DynamicProgram（不存在或未啟用 → Forbidden），再看目前使用者對該模組是否有 <paramref name="action"/>。
    /// <paramref name="module"/> 必須已載入 Blocks／Fields（有效權限要依幣別欄位判定 Pricing）。
    /// </summary>
    Task<AccessResult> AuthorizeAsync(Module module, PermissionAction action, CancellationToken ct);

    /// <summary>
    /// 明細模組的存取：動作軸、資料範圍、Pricing 取自 <paramref name="parent"/> 的 ProgramCode 與資料範圍，
    /// 欄位隱藏／唯讀與選項範圍取自 <paramref name="child"/> 自己的設定；表頭的 Program 必須啟用，明細自己的授權忽略。
    /// <paramref name="child"/> 必須已載入 Blocks／Fields。
    /// </summary>
    Task<AccessResult> AuthorizeDetailAsync(Module parent, Module child, PermissionAction action, CancellationToken ct);

    /// <summary>目前使用者在設計器（DYN00000）擁有的動作；使用者不存在回空集合。</summary>
    Task<IReadOnlySet<PermissionAction>> DesignerActionsAsync(CancellationToken ct);
}
