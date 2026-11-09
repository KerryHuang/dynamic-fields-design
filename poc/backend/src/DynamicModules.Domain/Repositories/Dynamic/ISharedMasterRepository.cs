using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>共用主檔的帳（設計 2026-11-10-shared-master-design §6～§8）：使用關係、權限來源、逐動作範圍、可見授予、孤兒。</summary>
public interface ISharedMasterRepository
{
    /// <summary>全部使用關係（不追蹤）。</summary>
    Task<List<AppMasterUse>> ListUsesAsync(CancellationToken ct);
    void AddUse(AppMasterUse use);

    /// <summary>主檔模組上的權限來源記錄（追蹤中）。</summary>
    Task<List<AppGrant>> ListGrantsAsync(long moduleId, CancellationToken ct);
    void AddGrant(AppGrant grant);
    void RemoveGrant(AppGrant grant);

    /// <summary>重算結果：刪除該模組的逐動作範圍再寫入新列；需在交易內呼叫。</summary>
    Task ReplaceActionScopesAsync(long moduleId, IReadOnlyList<ModuleActionScope> rows, CancellationToken ct);
    Task<List<ModuleActionScope>> ListActionScopesAsync(long moduleId, CancellationToken ct);

    /// <summary>這些欄位的可見授予（追蹤中）。</summary>
    Task<List<FieldGrant>> ListFieldGrantsAsync(IReadOnlyCollection<long> fieldIds, CancellationToken ct);
    void AddFieldGrant(FieldGrant grant);
    void RemoveFieldGrant(FieldGrant grant);

    /// <summary>formerAppKey 為 null 時列出全部（追蹤中）。</summary>
    Task<List<AppOrphan>> ListOrphansAsync(string? formerAppKey, CancellationToken ct);
    void AddOrphan(AppOrphan orphan);
    void RemoveOrphan(AppOrphan orphan);

    /// <summary>模組上的貢獻欄位與它的貢獻者（對照列名稱含 /）；沒有貢獻時是空的。</summary>
    Task<List<ContributedField>> ListContributedFieldsAsync(long moduleId, CancellationToken ct);

    /// <summary>模組上屬於貢獻者的區塊：貢獻者的對照列（含 /），加上孤兒區塊（擁有者記前擁有者 id）。基本區塊不在裡面。</summary>
    Task<List<BlockOwner>> ListBlockOwnersAsync(long moduleId, CancellationToken ct);
}

public sealed record BlockOwner(long BlockId, string OwnerKey);

/// <summary>貢獻欄位與貢獻者；凍結鎖用 AppStatus 判斷，前端提示用 AppKey 與 AppName。</summary>
public sealed record ContributedField(long FieldId, long AppId, string AppKey, I18nText AppName, AppStatus AppStatus);
