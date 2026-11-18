using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>測試期寫入守衛（PostgreSQL provider 設計 §5.4）：字串長度看值轉換後的結果、DateTime Kind 依欄位名稱判斷。</summary>
public class WriteGuardInterceptorTests : IAsyncLifetime
{
    private enum Color { Red, Yellowish }

    private sealed class Probe
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public Color Color { get; set; }
        public DateTime? CreatedTime { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }

    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Probe>().Property(x => x.Name).HasMaxLength(3);
            b.Entity<Probe>().Property(x => x.Color).HasConversion<string>().HasMaxLength(3);
        }
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ProbeContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new ProbeContext(new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlite(_connection).AddInterceptors(WriteGuardInterceptor.Instance).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<int> Add(Probe p)
    {
        _db.Add(p);
        return _db.SaveChangesAsync();
    }

    [Fact]
    public async Task StringWithinLength_Saves_CountingCodePoints()
    {
        // 三個代理對：UTF-16 長度 6，字元數 3，與 PostgreSQL varchar(3) 的算法相同
        (await Add(new Probe { Name = "😀😀😀" })).Should().Be(1);
    }

    [Fact]
    public async Task StringOverDeclaredLength_Throws()
    {
        var act = () => Add(new Probe { Name = "abcd" });

        (await act.Should().ThrowAsync<WriteGuardViolationException>())
            .Which.Violations.Should().ContainSingle().Which.Should().Contain("Name").And.Contain("4").And.Contain("3");
    }

    [Fact]
    public async Task ConvertedValueIsChecked_NotTheClrValue()
    {
        // 列舉轉字串後 "Yellowish" 是 9 個字元
        var act = () => Add(new Probe { Color = Color.Yellowish });

        (await act.Should().ThrowAsync<WriteGuardViolationException>()).Which.Message.Should().Contain("Color");
    }

    [Fact]
    public async Task ModifiedEntity_OnlyModifiedPropertiesAreChecked()
    {
        await Add(new Probe { Name = "ok" });
        await _db.Database.ExecuteSqlRawAsync("UPDATE \"Probe\" SET \"Name\" = 'too long'");
        _db.ChangeTracker.Clear();
        var row = await _db.Set<Probe>().SingleAsync();

        row.CreatedTime = new DateTime(2026, 11, 18, 9, 0, 0);
        (await _db.SaveChangesAsync()).Should().Be(1);

        row.Name = "long again";
        var act = () => _db.SaveChangesAsync();
        await act.Should().ThrowAsync<WriteGuardViolationException>();
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task WallClockColumn_AcceptsNonUtc(DateTimeKind kind)
    {
        (await Add(new Probe { CreatedTime = new DateTime(2026, 11, 18, 9, 0, 0, kind) })).Should().Be(1);
    }

    [Fact]
    public async Task WallClockColumn_RejectsUtc()
    {
        var act = () => Add(new Probe { CreatedTime = DateTime.UtcNow });

        (await act.Should().ThrowAsync<WriteGuardViolationException>()).Which.Message.Should().Contain("CreatedTime");
    }

    [Fact]
    public async Task UtcNamedColumn_RequiresUtc()
    {
        (await Add(new Probe { CreatedUtc = DateTime.UtcNow })).Should().Be(1);

        var act = () => Add(new Probe { CreatedUtc = new DateTime(2026, 11, 18, 9, 0, 0) });
        (await act.Should().ThrowAsync<WriteGuardViolationException>()).Which.Message.Should().Contain("CreatedUtc");
    }

    [Fact]
    public async Task DateTimeOffset_RequiresZeroOffset()
    {
        (await Add(new Probe { LockedUntil = DateTimeOffset.UtcNow })).Should().Be(1);

        var act = () => Add(new Probe { LockedUntil = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.FromHours(8)) });
        (await act.Should().ThrowAsync<WriteGuardViolationException>()).Which.Message.Should().Contain("LockedUntil");
    }

    [Fact]
    public async Task TestDb_HasTheGuard()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<AppUser>().Add(new AppUser { UserId = "u1", DisplayName = new string('名', 101) });

        var act = () => db.Context.SaveChangesAsync();

        (await act.Should().ThrowAsync<WriteGuardViolationException>()).Which.Message.Should().Contain("DisplayName");
    }
}
