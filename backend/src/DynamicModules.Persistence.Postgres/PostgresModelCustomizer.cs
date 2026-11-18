using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// PostgreSQL 的模型調整（PostgreSQL provider 設計 §5.4、§14 T8）：先套用既有的全部 <c>IEntityTypeConfiguration</c>，
/// 再改 PostgreSQL 需要的部分；<c>Domain/Data/Configurations/</c> 不動，SQLite 的模型與 migration 不受影響。
/// 規則是可寫死的技術限制（設計 §11 第 4–6 項）。
/// </summary>
public sealed class PostgresModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public const string Collation = "C";
    public const string WallClockType = "timestamp without time zone";
    public const string UtcType = "timestamp with time zone";

    /// <summary>SQLite 寫法在 PostgreSQL 無效的部分索引 filter，依索引名覆寫（設計 §4 #4）；找不到索引就丟例外，不默默失效。</summary>
    public static readonly IReadOnlyDictionary<string, string> FilterOverrides = new Dictionary<string, string>
    {
        ["IX_Dynamic_Record_ModuleId_HostKey"] = "\"HostKey\" IS NOT NULL AND NOT \"DeleteFlag\"",
        ["IX_Dynamic_RecordIndex_Truncated"] = "\"TextTruncated\"",
        ["IX_Dynamic_RecordIndex_NoScaled"] = "\"ScaledValue\" IS NULL AND \"NumberValue\" IS NOT NULL",
    };

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties()) Adjust(property);
        }
        ApplyFilterOverrides(modelBuilder.Model);
    }

    private static void Adjust(IMutableProperty property)
    {
        var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        // 依儲存型別判斷：列舉的 HasConversion<string>() 與 JSON 轉換欄位也是字串欄位
        var stored = property.GetProviderClrType() ?? property.GetValueConverter()?.ProviderClrType ?? clr;
        stored = Nullable.GetUnderlyingType(stored) ?? stored;

        if (stored == typeof(string))
        {
            // 一律 text（不照 HasMaxLength 建 varchar(n)，設計 §14 T6）；位元組序 collation 等同 SQLite 的 BINARY（§13 U2）
            property.SetColumnType("text");
            property.SetCollation(Collation);
        }
        else if (stored == typeof(DateTime))
        {
            // 名稱以 Utc 結尾才是 UTC（timestamptz），其餘是業務牆上時間（設計 §14 T5）；不靠 Npgsql 的預設（timestamptz）
            property.SetColumnType(property.Name.EndsWith("Utc", StringComparison.Ordinal) ? UtcType : WallClockType);
        }
        else if (stored == typeof(DateTimeOffset))
        {
            property.SetColumnType(UtcType);
        }
    }

    private static void ApplyFilterOverrides(IMutableModel model)
    {
        var indexes = model.GetEntityTypes().SelectMany(e => e.GetDeclaredIndexes()).ToList();
        foreach (var (name, filter) in FilterOverrides)
        {
            var index = indexes.SingleOrDefault(i => i.GetDatabaseName() == name)
                ?? throw new InvalidOperationException($"PostgreSQL 模型調整找不到索引 {name}；改了 SQLite 的索引設定時要一起改這份覆寫清單");
            index.SetFilter(filter);
        }
    }
}
