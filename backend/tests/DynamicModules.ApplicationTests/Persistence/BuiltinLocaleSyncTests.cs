using DynamicModules.ApplicationTests.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>啟動時把內建語系同步成 <c>Dynamic_Locale</c> 的 builtin 列：缺的新增、不同才更新、相同不寫、不碰匯入的列（語系包設計 §4.1）。雙跑。</summary>
[Trait("Category", "Postgres")]
public class BuiltinLocaleSyncTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

    private static async Task<List<InstalledLocale>> RowsAsync(TestDb db)
    {
        db.Context.ChangeTracker.Clear();
        return await db.Context.Set<InstalledLocale>().AsNoTracking().OrderBy(l => l.Code).ToListAsync();
    }

    [Fact]
    public async Task EmptyTable_AddsBuiltinFour()
    {
        await using var db = await TestDb.CreateAsync();

        await BuiltinLocaleSync.EnsureAsync(db.Context, new ManualTimeProvider(T0), CancellationToken.None);

        var rows = await RowsAsync(db);
        rows.Should().HaveCount(4).And.OnlyContain(r => r.Origin == LocaleOrigin.Builtin && r.CreatedUtc == T0 && r.ModifiedUtc == T0);
        foreach (var b in BuiltinLocales.All)
        {
            var row = rows.Single(r => r.Code == b.Code);
            row.NativeName.Should().Be(b.NativeName);
            row.QuasarLang.Should().Be(b.QuasarLang);
            row.FallbackCode.Should().BeNull();
        }
    }

    [Fact]
    public async Task Unchanged_DoesNotWrite_Changed_Restores_InstalledKept()
    {
        await using var db = await TestDb.CreateAsync();
        var time = new ManualTimeProvider(T0);
        await BuiltinLocaleSync.EnsureAsync(db.Context, time, CancellationToken.None);
        db.Context.Set<InstalledLocale>().Add(new InstalledLocale
        {
            Code = "ja", NativeName = "日本語", FallbackCode = "en-US", Origin = LocaleOrigin.Installed, CreatedUtc = T0, ModifiedUtc = T0,
        });
        await db.Context.SaveChangesAsync();

        time.Advance(TimeSpan.FromHours(1));
        await BuiltinLocaleSync.EnsureAsync(db.Context, time, CancellationToken.None);
        (await RowsAsync(db)).Should().OnlyContain(r => r.ModifiedUtc == T0, "不變不寫");

        var th = await db.Context.Set<InstalledLocale>().SingleAsync(r => r.Code == "th");
        th.NativeName = "x";
        var vi = await db.Context.Set<InstalledLocale>().SingleAsync(r => r.Code == "vi");
        vi.Origin = LocaleOrigin.Installed;
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        time.Advance(TimeSpan.FromHours(1));
        await BuiltinLocaleSync.EnsureAsync(db.Context, time, CancellationToken.None);

        var rows = await RowsAsync(db);
        rows.Single(r => r.Code == "th").Should().Match<InstalledLocale>(r => r.NativeName == "ไทย" && r.ModifiedUtc == T0.AddHours(2));
        rows.Single(r => r.Code == "vi").Should().Match<InstalledLocale>(r => r.Origin == LocaleOrigin.Builtin && r.ModifiedUtc == T0.AddHours(2));
        rows.Single(r => r.Code == "zh-TW").ModifiedUtc.Should().Be(T0);
        rows.Single(r => r.Code == "ja").Should().Match<InstalledLocale>(r =>
            r.Origin == LocaleOrigin.Installed && r.NativeName == "日本語" && r.FallbackCode == "en-US" && r.ModifiedUtc == T0);
    }
}
