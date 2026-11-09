using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService.Data;

public record DynamicRecordResponse(
    long Id, string ModuleKey, string Title, Dictionary<string, JsonElement?> Values, string OwnerUserId,
    string CreatedUserId, DateTime CreatedTime, string ModifiedUserId, DateTime ModifiedTime, string RowVersion,
    IReadOnlyDictionary<string, ReferenceInfo> References, string? HostKey,
    IReadOnlyDictionary<string, IReadOnlyList<DynamicRecordResponse>>? Details = null, bool FrozenByEffects = false)
{
    /// <summary>凍結的貢獻欄位（欄位 key → 所屬的已關閉應用程式，共用主檔設計 §7）；沒有時是 null。</summary>
    public IReadOnlyDictionary<string, SchemaService.Data.SchemaLock>? LockedBy { get; init; }
}

public static class RecordMapper
{
    public const string ModuleNotFound = "模組不存在或未啟用";
    public const string RecordNotFound = "記錄不存在";
    public const string Stale = "資料已被其他人修改，請重新載入";

    /// <summary>白名單：values 只留模組內且使用者看得到的欄位；Module 沒載入欄位時結果為空（fail-closed）。</summary>
    public static DynamicRecordResponse ToResponse(this Record r, string moduleKey, EffectivePermissions p, Module module,
        IReadOnlyDictionary<string, ReferenceInfo>? references = null)
    {
        var values = RecordValueProcessor.Parse(r.ValuesJson);
        var visible = module.Blocks.SelectMany(b => b.Fields).Where(p.CanSee).Select(f => f.Key).ToHashSet();
        values = values.Where(kv => visible.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        var title = module.TitleVisibleTo(p) ? r.Title : "";
        var locked = p.Frozen.Count == 0 ? null : module.Blocks.SelectMany(b => b.Fields).Where(f => p.Frozen.ContainsKey(f.Id) && visible.Contains(f.Key))
            .ToDictionary(f => f.Key, f => new SchemaService.Data.SchemaLock(p.Frozen[f.Id].AppKey, p.Frozen[f.Id].AppName));
        return new(r.Id, moduleKey, title, values, r.OwnerUserId,
            r.CreatedUserId, r.CreatedTime, r.ModifiedUserId ?? r.CreatedUserId, r.ModifiedTime ?? r.CreatedTime,
            r.RowVersion, references ?? new Dictionary<string, ReferenceInfo>(), r.HostKey) { LockedBy = locked };
    }

    /// <summary>
    /// 決定標題的欄位（標題欄位與標題樣板引用的欄位）有任何一個對使用者不可見時，Title 與關鍵字搜尋都不能外露
    /// （fail-closed：找不到標題欄位也視為不可見）。
    /// </summary>
    public static bool TitleVisibleTo(this Module module, EffectivePermissions p)
    {
        if (module.Kind == ModuleKind.Extension) return true;
        var ids = module.TitleFieldIds();
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => ids.Contains(f.Id)).ToList();
        return module.TitleFieldId is not null && fields.Count == ids.Count && fields.All(p.CanSee);
    }
}
