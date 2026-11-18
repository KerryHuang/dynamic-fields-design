using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DynamicModules.TestSupport;

/// <summary>
/// 測試期寫入守衛（PostgreSQL provider 設計 §5.4），兩種資料庫都掛；SaveChanges 要寫的值違規就丟例外，讓測試失敗：
/// <list type="bullet">
/// <item>字串長度：值轉換後實際寫進資料庫的字串（含列舉轉字串、JSON 轉換欄位）超過模型宣告的 HasMaxLength。
/// 長度以字元（code point）計，與 PostgreSQL 的 varchar(n) 相同。</item>
/// <item>DateTime Kind：屬性名稱以 Utc 結尾的是 timestamptz，只收 Kind=Utc；其餘是牆上時間（timestamp），不收 Kind=Utc。
/// DateTimeOffset 一律 timestamptz，offset 必須是 0。這是 Npgsql 執行期才會丟的錯，SQLite 上靠這裡先抓。</item>
/// </list>
/// 只看 SaveChanges 要寫的值（新增的整列、修改的欄位）；ExecuteUpdate／raw SQL 不經過這裡。
/// </summary>
public sealed class WriteGuardInterceptor : SaveChangesInterceptor
{
    public static readonly WriteGuardInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Check(DbContext? context)
    {
        if (context is null) return;
        var violations = new List<string>();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            foreach (var property in entry.Properties)
            {
                if (property.IsTemporary || (entry.State == EntityState.Modified && !property.IsModified)) continue;
                var violation = Inspect(property);
                if (violation is not null) violations.Add($"{entry.Metadata.DisplayName()}.{property.Metadata.Name}：{violation}");
            }
        }
        if (violations.Count > 0) throw new WriteGuardViolationException(violations);
    }

    private static string? Inspect(PropertyEntry property)
    {
        var value = property.CurrentValue;
        if (value is null) return null;
        var converter = property.Metadata.GetTypeMapping().Converter;
        var stored = converter is null ? value : converter.ConvertToProvider(value);

        switch (stored)
        {
            case string text when property.Metadata.GetMaxLength() is { } max && text.Length > max:
                // UTF-16 長度超過才數 code point（代理對算一個字元）
                var length = text.EnumerateRunes().Count();
                return length > max ? $"字串長度 {length} 超過宣告的 {max}" : null;
            case DateTime time:
                var utcColumn = property.Metadata.Name.EndsWith("Utc", StringComparison.Ordinal);
                if (utcColumn && time.Kind != DateTimeKind.Utc) return $"UTC 欄位（timestamptz）必須寫 Kind=Utc，實際是 {time.Kind}";
                if (!utcColumn && time.Kind == DateTimeKind.Utc) return "牆上時間欄位（timestamp）不能寫 Kind=Utc；UTC 值請存在名稱以 Utc 結尾的欄位";
                return null;
            case DateTimeOffset offset when offset.Offset != TimeSpan.Zero:
                return $"DateTimeOffset 欄位（timestamptz）的 offset 必須是 0，實際是 {offset.Offset}";
            default:
                return null;
        }
    }
}

/// <summary>測試期寫入守衛抓到違規；刻意不是 DbUpdateException，產品程式接資料庫錯誤的地方不會把它吞掉或轉成 409。</summary>
public sealed class WriteGuardViolationException(IReadOnlyList<string> violations)
    : Exception("測試期寫入守衛（PostgreSQL provider 設計 §5.4）：\n" + string.Join("\n", violations))
{
    public IReadOnlyList<string> Violations { get; } = violations;
}
