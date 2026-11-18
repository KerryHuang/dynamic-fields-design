using DynamicModules.Persistence.Postgres;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// PostgreSQL 模型的對應規則（PostgreSQL provider 設計 §5.4）：用 design-time factory 的模型檢查，不需要 PostgreSQL 伺服器。
/// 新程式要存 UTC 就得把屬性名稱取成 …Utc，否則這裡失敗。
/// </summary>
public class PostgresModelTests
{
    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        using var context = new PostgresDesignTimeDbContextFactory().CreateDbContext([]);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static IEnumerable<IProperty> Properties => Model.GetEntityTypes().SelectMany(e => e.GetProperties());

    private static Type StoredType(IProperty p)
    {
        var stored = p.GetTypeMapping().Converter?.ProviderClrType ?? p.GetProviderClrType() ?? p.ClrType;
        return Nullable.GetUnderlyingType(stored) ?? stored;
    }

    private static string Name(IProperty p) => $"{p.DeclaringType.DisplayName()}.{p.Name}";

    [Fact]
    public void StringColumns_AreText_WithByteOrderCollation()
    {
        var strings = Properties.Where(p => StoredType(p) == typeof(string)).ToList();
        strings.Should().NotBeEmpty();
        strings.Where(p => p.GetColumnType() != "text").Select(Name).Should().BeEmpty("字串欄位一律 text，不照 HasMaxLength 建 varchar(n)");
        strings.Where(p => p.GetCollation() != PostgresModelCustomizer.Collation).Select(Name).Should().BeEmpty("字串欄位一律 COLLATE \"C\"");
    }

    [Fact]
    public void JsonConvertedColumns_AreStoredAsText()
    {
        // 列舉轉字串與 JSON 轉換欄位依儲存型別算字串；JsonElement 不能被 Npgsql 當成 jsonb 原生型別
        Properties.Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(System.Text.Json.JsonElement))
            .Where(p => p.GetColumnType() != "text" || StoredType(p) != typeof(string)).Select(Name)
            .Should().BeEmpty();
    }

    [Fact]
    public void DateTimeColumns_FollowUtcNamingRule()
    {
        var times = Properties.Where(p => StoredType(p) == typeof(DateTime)).ToList();
        times.Should().NotBeEmpty();
        times.Where(p => p.Name.EndsWith("Utc", StringComparison.Ordinal) && p.GetColumnType() != PostgresModelCustomizer.UtcType)
            .Select(Name).Should().BeEmpty("名稱以 Utc 結尾的是 timestamptz");
        times.Where(p => !p.Name.EndsWith("Utc", StringComparison.Ordinal) && p.GetColumnType() != PostgresModelCustomizer.WallClockType)
            .Select(Name).Should().BeEmpty("其餘 DateTime 是業務牆上時間，timestamp without time zone");
        Properties.Where(p => StoredType(p) == typeof(DateTimeOffset) && p.GetColumnType() != PostgresModelCustomizer.UtcType)
            .Select(Name).Should().BeEmpty();
    }

    [Fact]
    public void PartialIndexFilters_AreOverridden()
    {
        var filters = Model.GetEntityTypes().SelectMany(e => e.GetIndexes())
            .ToDictionary(i => i.GetDatabaseName()!, i => i.GetFilter());
        foreach (var (name, filter) in PostgresModelCustomizer.FilterOverrides) filters[name].Should().Be(filter);
    }
}
