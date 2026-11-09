using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 生命週期鎖（共用主檔設計 §7）：貢獻欄位的貢獻者不是 Open 時，欄位凍結，所有寫入者都不能改它。
/// 兩道：<see cref="Dynamic.RecordService.RecordValueProcessor"/> 對「明確給了不同的值」整筆 409（<see cref="FrozenFieldException"/>）；
/// 各寫回 ValuesJson 的落地點再用 <see cref="Restore"/> 把凍結欄位蓋回舊值。查詢結果在同一個請求內快取。
/// </summary>
public sealed class FieldLocks(ISharedMasterRepository shared)
{
    private static readonly IReadOnlyDictionary<long, FieldLock> None = new Dictionary<long, FieldLock>();
    private readonly Dictionary<long, IReadOnlyDictionary<long, FieldLock>> _cache = new();

    public async Task<IReadOnlyDictionary<long, FieldLock>> FrozenAsync(Module module, CancellationToken ct)
    {
        // 貢獻欄位建立時都標了限定可見：載入了欄位、又沒有任何限定可見欄位的模組不必查（一般模組零成本）
        if (module.Blocks.Count > 0 && !module.Blocks.SelectMany(b => b.Fields).Any(f => f.VisibilityRestricted)) return None;
        return await FrozenAsync(module.Id, ct);
    }

    public async Task<IReadOnlyDictionary<long, FieldLock>> FrozenAsync(long moduleId, CancellationToken ct)
    {
        if (_cache.TryGetValue(moduleId, out var hit)) return hit;
        var locks = (await shared.ListContributedFieldsAsync(moduleId, ct)).Where(c => c.AppStatus != AppStatus.Open)
            .GroupBy(c => c.FieldId).ToDictionary(g => g.Key, g => new FieldLock(g.First().AppKey, g.First().AppName));
        return _cache[moduleId] = locks.Count == 0 ? None : locks;
    }

    /// <summary>刪除記錄要清空的參照欄位裡，第一個凍結的（清空也算寫入，設計 §7 凍結第 5 點）；都沒有回 null。</summary>
    public async Task<string?> RejectClearAsync(IEnumerable<Field> cleared, CancellationToken ct)
    {
        foreach (var field in cleared)
            if ((await FrozenAsync(field.ModuleId, ct)).TryGetValue(field.Id, out var owner))
                return new FrozenFieldException(field, owner).Message;
        return null;
    }

    /// <summary>把凍結欄位加進有效權限：Frozen 給寫入管線，ReadOnlyFieldIds 給畫面；雜湊跟著變，前端快取才會更新。</summary>
    public async Task<EffectivePermissions> ApplyAsync(Module module, EffectivePermissions perms, CancellationToken ct)
    {
        var locks = await FrozenAsync(module, ct);
        if (locks.Count == 0) return perms;
        var ids = string.Join(",", locks.Keys.Order());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{perms.Hash}|z:{ids}")))[..8].ToLowerInvariant();
        return perms with { ReadOnlyFieldIds = perms.ReadOnlyFieldIds.Union(locks.Keys).ToHashSet(), Frozen = locks, Hash = hash };
    }

    /// <summary>落地點的第二道：凍結欄位一律以舊值覆蓋（新增時沒有舊值，就是沒有值）。</summary>
    public static void Restore(Module module, IReadOnlyDictionary<long, FieldLock> locks,
        IReadOnlyDictionary<string, JsonElement?>? previous, Dictionary<string, JsonElement?> values)
    {
        if (locks.Count == 0) return;
        foreach (var field in module.Blocks.SelectMany(b => b.Fields).Where(f => locks.ContainsKey(f.Id)))
            if (previous is not null && previous.TryGetValue(field.Key, out var old) && old is not null) values[field.Key] = old;
            else values.Remove(field.Key);
    }

    public async Task RestoreAsync(Module module, IReadOnlyDictionary<string, JsonElement?>? previous, Dictionary<string, JsonElement?> values, CancellationToken ct) =>
        Restore(module, await FrozenAsync(module, ct), previous, values);
}

/// <summary>
/// 對凍結欄位明確給了不同的值：整個請求 409（WebAPI 的例外處理轉成回應），交易回滾。
/// 使用者看不到的欄位（visible 為 false，例如帶值寫進去的系統寫入）只回通用訊息，不帶欄位名、應用程式名與欄位 id，免得探測隱藏值（第三輪 3）。
/// </summary>
public sealed class FrozenFieldException(Field field, FieldLock owner, bool visible = true)
    : Exception(visible
        ? $"欄位「{field.LabelI18n.Resolve(I18nText.FallbackLocale)}」屬於已關閉的應用程式「{owner.AppName.Resolve(I18nText.FallbackLocale)}」，不能修改"
        : "這筆資料有無法修改的欄位")
{
    public bool Visible { get; } = visible;
    public long FieldId { get; } = field.Id;
    public string AppKey { get; } = owner.AppKey;
}
