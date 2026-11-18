using System.Data;
using Dapper;
using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.Dynamic.Query;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DynamicModules.ApplicationTests.Persistence;

[Trait("Category", "Postgres")]
public class DynamicQueryBuilderTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static Field F(long id, FieldType type) => new() { Id = id, Type = type };

    private static RecordQuery Q(
        ConditionNode? filter = null, IReadOnlyList<RecordSort>? sorts = null, string? keyword = null,
        IReadOnlySet<string>? owners = null) =>
        new(1, filter, sorts ?? [], keyword, Today, 1, 10, owners);

    [Fact]
    public void Build_OwnerUserIds_AreParameters()
    {
        var (countSql, pageSql, parameters) = DynamicQueryBuilder.Build(
            Q(owners: new HashSet<string> { "sales01", "sales02" }), new SqliteDialect());

        foreach (var sql in new[] { countSql, pageSql })
        {
            sql.Should().Contain("r.\"OwnerUserId\" IN (@o0, @o1)").And.NotContain("sales0");
        }
        parameters.Get<string>("o0").Should().Be("sales01");
        parameters.Get<string>("o1").Should().Be("sales02");

        var (emptyCount, emptyPage, _) = DynamicQueryBuilder.Build(Q(owners: new HashSet<string>()), new SqliteDialect());
        emptyCount.Should().Contain("1 = 0");
        emptyPage.Should().Contain("1 = 0");

        var (allCount, _, _) = DynamicQueryBuilder.Build(Q(), new SqliteDialect());
        allCount.Should().NotContain("OwnerUserId");
    }

    [Theory]
    [InlineData(FieldType.Formula, "number", "NumberValue")]
    [InlineData(FieldType.Formula, "currency", "NumberValue")]
    [InlineData(FieldType.Formula, "date", "DateValue")]
    [InlineData(FieldType.Rollup, "number", "NumberValue")]
    [InlineData(FieldType.Rollup, "date", "DateValue")]
    public void Build_ComputedFieldsCompareTheColumnTheirIndexRowsUse(FieldType type, string resultType, string column)
    {
        // 公式與彙總的索引列依 ResultType 存在 NumberValue 或 DateValue；比較與排序必須用同一欄，否則永遠比不到
        var field = new Field { Id = 7, Type = type, Config = new() { ResultType = resultType } };
        var value = resultType == "date" ? new IndexValue(0, null, null, new DateTime(2026, 11, 3), null) : new IndexValue(0, null, 5, null, null);
        var filter = new FieldCondition(field, FilterOperator.Gte, [value], null);

        var (countSql, pageSql, parameters) = DynamicQueryBuilder.Build(
            Q(filter, sorts: [new RecordSort(field, true)]), new SqliteDialect());

        foreach (var sql in new[] { countSql, pageSql }) sql.Should().Contain($"i.\"{column}\" >= @v0_0").And.NotContain("TextValue");
        pageSql.Should().Contain($"s0.\"{column}\"");
        parameters.Get<object>("v0_0").Should().NotBeNull();
    }

    [Fact]
    public void Build_NeverEmbedsUserValuesInSql()
    {
        const string evil = "x'; DROP TABLE Dynamic_Record;--";
        const string wild = "50%_off";
        var filter = new ConditionGroup(true,
        [
            new FieldCondition(F(11, FieldType.Text), FilterOperator.Eq, [new IndexValue(0, evil, null, null, null)], null),
            new FieldCondition(F(12, FieldType.Text), FilterOperator.Contains, [new IndexValue(0, wild, null, null, null)], null),
            new FieldCondition(F(13, FieldType.Text), FilterOperator.In,
                [new IndexValue(0, evil, null, null, null), new IndexValue(1, wild, null, null, null)], null),
        ]);

        var (countSql, pageSql, parameters) = DynamicQueryBuilder.Build(Q(filter, keyword: wild), new SqliteDialect());

        foreach (var sql in new[] { countSql, pageSql })
        {
            sql.Should().NotContain("DROP").And.NotContain("50%").And.NotContain("x'");
            sql.Should().NotContain("11").And.NotContain("12").And.NotContain("13");
        }
        var values = parameters.ParameterNames.Select(n => parameters.Get<object>(n)).ToList();
        values.Should().Contain(evil);
        values.Should().Contain(@"%50\%\_off%");
    }

    [Fact]
    public void Build_ConditionsUseUncorrelatedInSubquery()
    {
        var filter = new ConditionGroup(true,
        [
            new FieldCondition(F(1, FieldType.Integer), FilterOperator.Gte, [new IndexValue(0, null, 5, null, null)], null),
            new FieldCondition(F(2, FieldType.Text), FilterOperator.Ne, [new IndexValue(0, "a", null, null, null)], null),
            new FieldCondition(F(3, FieldType.Text), FilterOperator.IsEmpty, [], null),
        ]);

        var (countSql, pageSql, _) = DynamicQueryBuilder.Build(Q(filter), new SqliteDialect());

        foreach (var sql in new[] { countSql, pageSql })
        {
            sql.Should().NotContain("EXISTS");
            sql.Should().Contain("r.\"Id\" IN (SELECT i.\"RecordId\" FROM \"Dynamic_RecordIndex\" i WHERE i.\"FieldId\" = @f0");
            sql.Should().Contain("r.\"Id\" NOT IN (SELECT i.\"RecordId\" FROM \"Dynamic_RecordIndex\" i WHERE i.\"FieldId\" = @f1");
            sql.Should().Contain("r.\"Id\" NOT IN (SELECT i.\"RecordId\" FROM \"Dynamic_RecordIndex\" i WHERE i.\"FieldId\" = @f2)");
        }
    }

    [Fact]
    public void Build_NullsLastUsesCaseExpression()
    {
        var sorts = new[] { new RecordSort(F(5, FieldType.Integer), true), new RecordSort(F(6, FieldType.Enum), false) };

        var (_, pageSql, _) = DynamicQueryBuilder.Build(Q(sorts: sorts), new SqliteDialect());

        pageSql.Should().NotContain("NULLS LAST");
        pageSql.Should().Contain("CASE WHEN s0.\"NumberValue\" IS NULL THEN 1 ELSE 0 END, s0.\"NumberValue\" DESC");
        pageSql.Should().Contain("CASE WHEN o1.\"Sequence\" IS NULL THEN 1 ELSE 0 END, o1.\"Sequence\" ASC");
        pageSql.Should().Contain("LEFT JOIN \"Dynamic_FieldOption\" o1 ON o1.\"Id\" = s1.\"NumberValue\"");
        pageSql.Should().EndWith("r.\"Id\" DESC LIMIT @limit OFFSET @offset");
    }

    [Fact]
    public void Build_CountSqlHasNoSortJoins()
    {
        var sorts = new[] { new RecordSort(F(5, FieldType.Integer), true) };

        var (countSql, pageSql, _) = DynamicQueryBuilder.Build(Q(sorts: sorts), new SqliteDialect());

        countSql.Should().NotContain("JOIN").And.NotContain("ORDER BY");
        pageSql.Should().Contain("LEFT JOIN \"Dynamic_RecordIndex\" s0");
    }

    [Fact]
    public void Build_BooleanAndCaseInsensitiveLikeComeFromTheDialect()
    {
        // SQLite 的方言產出與改用方言前逐字相同：DeleteFlag 比 0、LIKE 帶 ESCAPE
        var filter = new FieldCondition(F(4, FieldType.Text), FilterOperator.StartsWith, [new IndexValue(0, "ab", null, null, null)], null);

        var (countSql, pageSql, _) = DynamicQueryBuilder.Build(Q(filter, keyword: "k"), new SqliteDialect());

        foreach (var sql in new[] { countSql, pageSql })
        {
            sql.Should().Contain("r.\"ModuleId\" = @m AND r.\"DeleteFlag\" = 0");
            sql.Should().Contain("r.\"Title\" LIKE @q ESCAPE '\\'");
            sql.Should().Contain("i.\"TextValue\" LIKE @v0_0 ESCAPE '\\'");
        }
    }

    [Fact]
    public void BuildAggregate_CastsSumsAndConstantsToBigint()
    {
        var sum = new AggregateQuery(1, [], null, F(9, FieldType.Decimal), Today);
        var count = sum with { SumField = null };

        var (sumSql, _) = DynamicQueryBuilder.BuildAggregate(sum, new SqliteDialect());
        var (countSql, _) = DynamicQueryBuilder.BuildAggregate(count, new SqliteDialect());

        sumSql.Should().Contain("CAST(COALESCE(SUM(s.\"ScaledValue\"), 0) AS BIGINT) AS \"ScaledSum\"");
        sumSql.Should().Contain("AS BIGINT) AS \"MissingScaled\"");
        countSql.Should().Contain("CAST(0 AS BIGINT) AS \"ScaledSum\", CAST(0 AS BIGINT) AS \"MissingScaled\"");
        foreach (var sql in new[] { sumSql, countSql }) sql.Should().Contain("r.\"DeleteFlag\" = 0");
    }

    [Fact]
    public async Task Build_DateParametersAreDateTime2_AndMatchTheSameRowsAsBefore()
    {
        // Dapper 預設把 DateTime 設成 DbType.DateTime，Npgsql 會當 timestamptz 而拒收牆上時間；日期篩選的參數一律 DateTime2（設計 §5.5）。
        // 資料用 EF 寫進 SQLite 的實際格式（yyyy-MM-dd HH:mm:ss.FFFFFFF，見 RecordQueryTests.Query_DateParameterMatchesEfStoredFormat）
        await using var connection = new CapturingConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            """CREATE TABLE "Dynamic_Record" ("Id" INTEGER, "ModuleId" INTEGER, "DeleteFlag" INTEGER, "OwnerUserId" TEXT, "Title" TEXT)""");
        await connection.ExecuteAsync(
            """CREATE TABLE "Dynamic_RecordIndex" ("RecordId" INTEGER, "FieldId" INTEGER, "Seq" INTEGER, "TextValue" TEXT, "NumberValue" REAL, "DateValue" TEXT, "RefKey" TEXT)""");
        // 欄位 1 日期、2 日期時間、3 日期；今天 2026-11-03
        (long Record, long Field, string Value)[] rows =
        [
            (1, 1, "2026-11-02 00:00:00"), (1, 2, "2026-11-02 23:59:00"), (1, 3, "2026-11-03 00:00:00"),
            (2, 1, "2026-11-03 00:00:00"), (2, 2, "2026-11-03 00:00:00"), (2, 3, "2026-11-04 00:00:00"),
            (3, 1, "2026-11-10 00:00:00"), (3, 2, "2026-11-02 23:59:59.5"),
            (4, 1, "2026-11-11 00:00:00"), (4, 2, "2026-11-04 08:00:00"), (4, 3, "2026-11-02 00:00:00"),
        ];
        for (var id = 1; id <= 4; id++)
            await connection.ExecuteAsync("""INSERT INTO "Dynamic_Record" VALUES (@id, 1, 0, 'admin', 't')""", new { id });
        foreach (var (record, field, value) in rows)
            await connection.ExecuteAsync("""INSERT INTO "Dynamic_RecordIndex" VALUES (@record, @field, 0, NULL, NULL, @value, NULL)""",
                new { record, field, value });

        async Task<long[]> Run(FieldCondition condition, params string[] dateParams)
        {
            var (_, pageSql, parameters) = DynamicQueryBuilder.Build(Q(condition), new SqliteDialect());
            connection.Captured = null;
            var ids = (await connection.QueryAsync<long>(pageSql, parameters)).Order().ToArray();
            var sent = connection.Captured!.Parameters.Cast<SqliteParameter>().ToDictionary(p => p.ParameterName.TrimStart('@'));
            foreach (var name in dateParams)
            {
                sent[name].Value.Should().BeOfType<DateTime>();
                sent[name].DbType.Should().Be(DbType.DateTime2, name);
            }
            // 改動前的參數（同值、不指定 DbType）命中同一批記錄
            var before = new DynamicParameters();
            foreach (var name in parameters.ParameterNames) before.Add(name, parameters.Get<object>(name));
            (await connection.QueryAsync<long>(pageSql, before)).Order().Should().Equal(ids);
            return ids;
        }

        // >= 11-03 00:00 且 < 11-11 00:00：含今天、含第 7 天，不含昨天與第 8 天
        (await Run(new FieldCondition(F(1, FieldType.Date), FilterOperator.WithinNextDays, [], 7), "v0_0", "v0_1"))
            .Should().Equal(2, 3);
        // < 11-03 00:00：前一天 23:59:59.5 算，今天 00:00 不算
        (await Run(new FieldCondition(F(2, FieldType.DateTime), FilterOperator.BeforeToday, [], null), "v0_0"))
            .Should().Equal(1, 3);
        (await Run(new FieldCondition(F(3, FieldType.Date), FilterOperator.Eq, [new IndexValue(0, null, null, new DateTime(2026, 11, 3), null)], null), "v0_0"))
            .Should().Equal(1);
    }

    /// <summary>記下 Dapper 建的最後一個指令，用來檢查參數實際送出的 DbType。</summary>
    private sealed class CapturingConnection() : SqliteConnection("Data Source=:memory:")
    {
        public SqliteCommand? Captured { get; set; }

        public override SqliteCommand CreateCommand() => Captured = base.CreateCommand();
    }
}
