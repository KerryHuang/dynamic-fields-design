using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>覆寫層版本表與預覽暫存表（語系包設計 §6.4、§22；i4d1 計畫判斷 7–9）。雙跑。</summary>
[Trait("Category", "Postgres")]
public class LocalePackRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 1, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static LocalePack Pack(string code, string ui, DateTime? at = null) => new()
    {
        Code = code, UiJson = ui, ServerJson = "{}", BaseHashesJson = "{}", FileName = "t.langpack.json", FileHash = new string('a', 64),
        ImportedUserId = "admin", ImportedUtc = at ?? T0, ModifiedUtc = at ?? T0,
    };

    private static LocalePack CopyOf(LocalePack p, DateTime now) => new()
    {
        Code = p.Code, UiJson = p.UiJson, ServerJson = p.ServerJson, BaseHashesJson = p.BaseHashesJson, FileHash = p.FileHash, FileName = p.FileName,
        PackVersion = p.PackVersion, BuiltFor = p.BuiltFor, BaseLocale = p.BaseLocale, Note = p.Note, ReportJson = p.ReportJson,
        ImportedUserId = p.ImportedUserId, ImportedUtc = p.ImportedUtc, ModifiedUtc = now,
    };

    private static LocalePackPreview Preview(string id, string code, DateTime created) => new()
    {
        Id = id, UserId = "admin", Code = code, FileHash = new string('b', 64), FileName = "t.langpack.json", PlanJson = "{}", ReportJson = "{}",
        CreatedUtc = created, ExpiresUtc = created.AddMinutes(10),
    };

    /// <summary>照 handler 的寫法：讀目前版與上一版、AddVersion、存檔，最後清掉追蹤（下一步重讀）。</summary>
    private static async Task<LocalePack> WriteAsync(TestDb db, LocalePack next, DateTime now)
    {
        var repo = new LocalePackRepository(db.Context);
        var current = await repo.CurrentAsync(next.Code, Ct);
        var previous = await repo.PreviousAsync(next.Code, Ct);
        repo.AddVersion(next, current, previous, now);
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();
        return next;
    }

    /// <summary>回到上一版：上一版內容的複本當新的目前版。</summary>
    private static async Task<LocalePack> RollbackAsync(TestDb db, string code, DateTime now)
    {
        var repo = new LocalePackRepository(db.Context);
        var current = await repo.CurrentAsync(code, Ct);
        var previous = (await repo.PreviousAsync(code, Ct))!;
        var next = CopyOf(previous, now);
        repo.AddVersion(next, current, previous, now);
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();
        return next;
    }

    private static Task<List<LocalePack>> AllAsync(TestDb db, string code)
        => db.Context.Set<LocalePack>().AsNoTracking().Where(p => p.Code == code).OrderBy(p => p.Id).ToListAsync(Ct);

    [Fact]
    public async Task AddVersion_KeepsTwo()
    {
        await using var db = await TestDb.CreateAsync();
        var v1 = await WriteAsync(db, Pack("ja", """{"a":"1"}"""), T0);
        var v2 = await WriteAsync(db, Pack("ja", """{"a":"2"}"""), T0.AddMinutes(1));
        var v3 = await WriteAsync(db, Pack("ja", """{"a":"3"}"""), T0.AddMinutes(2));

        var rows = await AllAsync(db, "ja");
        rows.Select(r => r.Id).Should().Equal(v2.Id, v3.Id);
        rows.Single(r => r.IsCurrent).UiJson.Should().Be("""{"a":"3"}""");
        rows.Single(r => !r.IsCurrent).UiJson.Should().Be("""{"a":"2"}""");
        rows.Single(r => !r.IsCurrent).ModifiedUtc.Should().Be(T0.AddMinutes(2));
        rows.Should().NotContain(r => r.Id == v1.Id);
    }

    [Fact]
    public async Task AddVersion_CopyOfPrevious_NewId()
    {
        await using var db = await TestDb.CreateAsync();
        var a = await WriteAsync(db, Pack("ja", """{"a":"A"}"""), T0);
        var b = await WriteAsync(db, Pack("ja", """{"a":"B"}"""), T0);
        var repo = new LocalePackRepository(db.Context);

        var back = await RollbackAsync(db, "ja", T0);
        var current = (await repo.CurrentAsync("ja", Ct))!;
        current.UiJson.Should().Be("""{"a":"A"}""");
        current.Id.Should().Be(back.Id).And.BeGreaterThan(b.Id).And.BeGreaterThan(a.Id);
        (await repo.PreviousAsync("ja", Ct))!.Id.Should().Be(b.Id);
        db.Context.ChangeTracker.Clear();

        var again = await RollbackAsync(db, "ja", T0);
        current = (await repo.CurrentAsync("ja", Ct))!;
        current.UiJson.Should().Be("""{"a":"B"}""");
        current.Id.Should().Be(again.Id).And.BeGreaterThan(back.Id);
    }

    [Fact]
    public async Task Stamp_ChangesOnEveryPackWrite()
    {
        await using var db = await TestDb.CreateAsync();
        var locales = new LocaleRepository(db.Context);
        var stamps = new List<LocaleVersionStamp> { await locales.StampAsync(Ct) };
        stamps[0].PackCount.Should().Be(0);
        stamps[0].PackMaxId.Should().BeNull();

        for (var i = 0; i < 3; i++)
        {
            await WriteAsync(db, Pack("ja", $$"""{"a":"{{i}}"}"""), T0);
            stamps.Add(await locales.StampAsync(Ct));
        }
        for (var i = 0; i < 2; i++)
        {
            await RollbackAsync(db, "ja", T0);
            stamps.Add(await locales.StampAsync(Ct));
        }

        stamps.Should().OnlyHaveUniqueItems();
        stamps.Skip(1).Select(s => s.PackCount).Should().Equal(1, 2, 2, 2, 2);
        stamps.Skip(1).Select(s => s.PackMaxId!.Value).Should().BeInAscendingOrder();

        var before = await locales.StampAsync(Ct);
        db.Context.Set<LocalePackPreview>().Add(Preview("p1", "ja", T0));
        await db.Context.SaveChangesAsync(Ct);
        (await locales.StampAsync(Ct)).Should().Be(before, "預覽表不算進戳記");
    }

    [Fact]
    public async Task Id_NotReusedAfterDelete()
    {
        await using var db = await TestDb.CreateAsync();
        await WriteAsync(db, Pack("ja", "{}"), T0);
        var second = await WriteAsync(db, Pack("ja", "{}"), T0);
        await new LocalePackRepository(db.Context).DeleteForLocaleAsync("ja", Ct);
        (await AllAsync(db, "ja")).Should().BeEmpty();

        var third = await WriteAsync(db, Pack("ja", "{}"), T0);

        third.Id.Should().BeGreaterThan(second.Id);
    }

    [Fact]
    public async Task DeleteForLocale_RemovesVersionsAndPreviews()
    {
        await using var db = await TestDb.CreateAsync();
        await WriteAsync(db, Pack("ja", "{}"), T0);
        await WriteAsync(db, Pack("ja", "{}"), T0);
        await WriteAsync(db, Pack("ko", "{}"), T0);
        var repo = new LocalePackRepository(db.Context);
        repo.AddPreview(Preview("pja", "ja", T0));
        repo.AddPreview(Preview("pko", "ko", T0));
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();

        await repo.DeleteForLocaleAsync("ja", Ct);

        (await AllAsync(db, "ja")).Should().BeEmpty();
        (await AllAsync(db, "ko")).Should().ContainSingle();
        (await repo.FindPreviewAsync("pja", Ct)).Should().BeNull();
        (await repo.FindPreviewAsync("pko", Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Preview_AddFindRemove()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new LocalePackRepository(db.Context);
        repo.AddPreview(Preview("p1", "ja", T0));
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();

        var found = await repo.FindPreviewAsync("p1", Ct);
        found.Should().NotBeNull();
        db.Context.Entry(found!).State.Should().Be(EntityState.Unchanged, "追蹤");
        repo.RemovePreview(found!);
        await db.Context.SaveChangesAsync(Ct);

        (await repo.FindPreviewAsync("p1", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Prune_ExpiredThenOldest()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new LocalePackRepository(db.Context);
        var now = T0.AddMinutes(30);
        repo.AddPreview(Preview("old1", "ja", now.AddMinutes(-20)));   // 過期
        repo.AddPreview(Preview("old2", "ja", now.AddMinutes(-11)));   // 過期
        for (var i = 1; i <= 5; i++) repo.AddPreview(Preview($"p{i}", "ja", now.AddMinutes(-6 + i)));
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();

        await repo.PrunePreviewsAsync(now, keep: 4, Ct);

        var left = await db.Context.Set<LocalePackPreview>().AsNoTracking().Select(p => p.Id).ToListAsync(Ct);
        left.Should().BeEquivalentTo("p2", "p3", "p4", "p5");
    }

    [Fact]
    public async Task Json_RoundTripsLargeText()
    {
        await using var db = await TestDb.CreateAsync();
        var text = string.Concat(Enumerable.Repeat("ภาษาไทย 🙂 ทดสอบ ", 12_000));
        text.Length.Should().BeGreaterThan(200_000);
        await WriteAsync(db, Pack("th", text), T0);

        (await new LocalePackRepository(db.Context).ListCurrentAsync(Ct)).Single().UiJson.Should().Be(text);
    }

    [Fact]
    public async Task ListCurrent_NoTracking_OrderedByCode()
    {
        await using var db = await TestDb.CreateAsync();
        await WriteAsync(db, Pack("vi", "{}"), T0);
        await WriteAsync(db, Pack("ja", "{}"), T0);
        await WriteAsync(db, Pack("ja", """{"x":"y"}"""), T0);

        var list = await new LocalePackRepository(db.Context).ListCurrentAsync(Ct);

        list.Select(p => p.Code).Should().Equal("ja", "vi");
        list.Should().OnlyContain(p => p.IsCurrent);
        list.Single(p => p.Code == "ja").UiJson.Should().Be("""{"x":"y"}""");
        db.Context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Utc_KindPreserved()
    {
        await using var db = await TestDb.CreateAsync();
        await WriteAsync(db, Pack("ja", "{}", T0), T0);
        var repo = new LocalePackRepository(db.Context);
        repo.AddPreview(Preview("p1", "ja", T0));
        await db.Context.SaveChangesAsync(Ct);
        db.Context.ChangeTracker.Clear();

        var pack = (await repo.ListCurrentAsync(Ct)).Single();
        pack.ImportedUtc.Kind.Should().Be(DateTimeKind.Utc);
        pack.ModifiedUtc.Kind.Should().Be(DateTimeKind.Utc);
        pack.ImportedUtc.Should().Be(T0);
        var preview = (await repo.FindPreviewAsync("p1", Ct))!;
        preview.CreatedUtc.Kind.Should().Be(DateTimeKind.Utc);
        preview.ExpiresUtc.Should().Be(T0.AddMinutes(10));
    }
}
