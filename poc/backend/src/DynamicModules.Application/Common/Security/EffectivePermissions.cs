using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common.Security;

public sealed record EffectivePermissions(
    IReadOnlySet<PermissionAction> Actions, DataScope Scope,
    IReadOnlySet<string>? OwnerUserIds,          // null = 不限（All）
    IReadOnlySet<long> HiddenFieldIds, IReadOnlySet<long> ReadOnlyFieldIds,
    IReadOnlySet<long> UnselectableOptionIds, string Hash)
{
    public bool Can(PermissionAction a) => Actions.Contains(a);
    public bool CanSee(Field f) => !HiddenFieldIds.Contains(f.Id);
    public bool CanWrite(Field f) => CanSee(f) && !ReadOnlyFieldIds.Contains(f.Id);

    /// <summary>
    /// 凍結的欄位：貢獻者應用程式不是 Open 的貢獻欄位（共用主檔設計 §7）。這不是權限而是狀態，系統管理員與系統寫入也一樣；
    /// 凍結的欄位同時放進 ReadOnlyFieldIds 供畫面顯示。
    /// </summary>
    public IReadOnlyDictionary<long, FieldLock> Frozen { get; init; } = new Dictionary<long, FieldLock>();

    /// <summary>
    /// 同時受另一個動作的有效權限限制：擁有者集合取交集、資料範圍取較窄，隱藏與唯讀欄位取聯集；動作與凍結照本身。
    /// 共用主檔模組的範圍逐動作不同（設計 §6），列印與匯出要同時符合讀取與列印匯出的範圍（審查必修 2）；一般模組兩者相同，結果不變。
    /// </summary>
    public EffectivePermissions Within(EffectivePermissions other)
    {
        var owners = OwnerUserIds is null ? other.OwnerUserIds
            : other.OwnerUserIds is null ? OwnerUserIds
            : OwnerUserIds.Intersect(other.OwnerUserIds).ToHashSet();
        return this with
        {
            Scope = Scope < other.Scope ? Scope : other.Scope, OwnerUserIds = owners,
            HiddenFieldIds = HiddenFieldIds.Union(other.HiddenFieldIds).ToHashSet(),
            ReadOnlyFieldIds = ReadOnlyFieldIds.Union(other.ReadOnlyFieldIds).ToHashSet(),
            Hash = Hash == other.Hash ? Hash : Hash + other.Hash
        };
    }
}

/// <summary>欄位為什麼凍結：屬於哪個已關閉的應用程式。</summary>
public sealed record FieldLock(string AppKey, I18nText AppName);
