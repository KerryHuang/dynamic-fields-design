using DynamicModules.ApplicationTests.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using DynamicModules.Persistence.Seed;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>已安裝語系表 <c>Dynamic_Locale</c> 的讀取：列表排序與版本戳記（語系包設計 §4.1、§4.2；i4a 計畫判斷 3）。雙跑。</summary>
[Trait("Category", "Postgres")]
public class LocaleRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

    private static InstalledLocale Installed(string code, DateTime at) => new()
    {
        Code = code, NativeName = code, Origin = LocaleOrigin.Installed, CreatedUtc = at, ModifiedUtc = at,
    };

    [Fact]
    public async Task ListAsync_BuiltinFirst_ThenInstalledByCreatedTime()
    {
        await using var db = await TestDb.CreateAsync();
        await BuiltinLocaleSync.EnsureAsync(db.Context, new ManualTimeProvider(T0), CancellationToken.None);
        db.Context.Set<InstalledLocale>().AddRange(Installed("ja", T0.AddMinutes(2)), Installed("ko", T0.AddMinutes(1)));
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        var list = await new LocaleRepository(db.Context).ListAsync(CancellationToken.None);

        list.Take(4).Should().OnlyContain(l => l.Origin == LocaleOrigin.Builtin);
        list.Take(4).Select(l => l.Code).Should().BeEquivalentTo("zh-TW", "en-US", "th", "vi");
        list.Skip(4).Select(l => l.Code).Should().Equal("ko", "ja");
    }

    [Fact]
    public async Task StampAsync_EmptyThenCountAndMaxModified()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new LocaleRepository(db.Context);

        (await repo.StampAsync(CancellationToken.None)).Should().Be(new LocaleVersionStamp(0, null, 0, null));

        await BuiltinLocaleSync.EnsureAsync(db.Context, new ManualTimeProvider(T0), CancellationToken.None);
        var before = await repo.StampAsync(CancellationToken.None);
        before.Count.Should().Be(4);
        before.MaxModifiedUtc.Should().Be(T0);

        db.Context.Set<InstalledLocale>().Add(Installed("ja", T0.AddMinutes(5)));
        await db.Context.SaveChangesAsync();
        var after = await repo.StampAsync(CancellationToken.None);
        after.Count.Should().Be(5);
        after.MaxModifiedUtc.Should().Be(T0.AddMinutes(5));
        after.MaxModifiedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task FindAddRemove()
    {
        await using var db = await TestDb.CreateAsync();
        await BuiltinLocaleSync.EnsureAsync(db.Context, new ManualTimeProvider(T0), CancellationToken.None);
        var repo = new LocaleRepository(db.Context);
        var before = await repo.StampAsync(CancellationToken.None);

        repo.Add(Installed("ja", T0.AddMinutes(1)));
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        var found = await repo.FindAsync("ja", CancellationToken.None);
        found.Should().NotBeNull();
        db.Context.Entry(found!).State.Should().Be(Microsoft.EntityFrameworkCore.EntityState.Unchanged, "追蹤");
        (await repo.StampAsync(CancellationToken.None)).Count.Should().Be(before.Count + 1);

        repo.Remove(found!);
        await db.Context.SaveChangesAsync();

        (await repo.FindAsync("ja", CancellationToken.None)).Should().BeNull();
        (await repo.StampAsync(CancellationToken.None)).Count.Should().Be(before.Count);
    }
}
