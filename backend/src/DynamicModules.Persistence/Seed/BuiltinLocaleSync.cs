using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

/// <summary>
/// 啟動時把 <see cref="BuiltinLocales"/> 同步成 <c>Dynamic_Locale</c> 的 builtin 列（語系包設計 §4.1）：缺的新增、自稱或 Quasar 語言包不同才更新、
/// 相同不寫；被改成 installed 的內建代碼改回 builtin。不刪任何列、不碰匯入的列。整次只呼叫一次 SaveChanges（全成或全不寫）。
/// </summary>
public static class BuiltinLocaleSync
{
    public static async Task EnsureAsync(DynamicDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var codes = BuiltinLocales.Codes.ToList();
        var existing = (await db.Set<InstalledLocale>().Where(l => codes.Contains(l.Code)).ToListAsync(ct)).ToDictionary(l => l.Code);
        foreach (var b in BuiltinLocales.All)
        {
            if (!existing.TryGetValue(b.Code, out var row))
            {
                db.Set<InstalledLocale>().Add(new InstalledLocale
                {
                    Code = b.Code, NativeName = b.NativeName, QuasarLang = b.QuasarLang, Origin = LocaleOrigin.Builtin, CreatedUtc = now, ModifiedUtc = now,
                });
                continue;
            }
            if (row.NativeName == b.NativeName && row.QuasarLang == b.QuasarLang && row.Origin == LocaleOrigin.Builtin) continue;
            row.NativeName = b.NativeName;
            row.QuasarLang = b.QuasarLang;
            row.Origin = LocaleOrigin.Builtin;
            row.ModifiedUserId = null;
            row.ModifiedUtc = now;
        }
        await db.SaveChangesAsync(ct);
    }
}
