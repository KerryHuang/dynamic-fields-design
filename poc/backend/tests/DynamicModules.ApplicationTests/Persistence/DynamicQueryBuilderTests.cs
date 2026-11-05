using DynamicModules.Domain.Conditions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Dynamic.Dialects;
using DynamicModules.Persistence.Dynamic.Query;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

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
            sql.Should().Contain("r.OwnerUserId IN (@o0, @o1)").And.NotContain("sales0");
        }
        parameters.Get<string>("o0").Should().Be("sales01");
        parameters.Get<string>("o1").Should().Be("sales02");

        var (emptyCount, emptyPage, _) = DynamicQueryBuilder.Build(Q(owners: new HashSet<string>()), new SqliteDialect());
        emptyCount.Should().Contain("1 = 0");
        emptyPage.Should().Contain("1 = 0");

        var (allCount, _, _) = DynamicQueryBuilder.Build(Q(), new SqliteDialect());
        allCount.Should().NotContain("OwnerUserId");
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
            sql.Should().Contain("r.Id IN (SELECT i.RecordId FROM Dynamic_RecordIndex i WHERE i.FieldId = @f0");
            sql.Should().Contain("r.Id NOT IN (SELECT i.RecordId FROM Dynamic_RecordIndex i WHERE i.FieldId = @f1");
            sql.Should().Contain("r.Id NOT IN (SELECT i.RecordId FROM Dynamic_RecordIndex i WHERE i.FieldId = @f2)");
        }
    }

    [Fact]
    public void Build_NullsLastUsesCaseExpression()
    {
        var sorts = new[] { new RecordSort(F(5, FieldType.Integer), true), new RecordSort(F(6, FieldType.Enum), false) };

        var (_, pageSql, _) = DynamicQueryBuilder.Build(Q(sorts: sorts), new SqliteDialect());

        pageSql.Should().NotContain("NULLS LAST");
        pageSql.Should().Contain("CASE WHEN s0.NumberValue IS NULL THEN 1 ELSE 0 END, s0.NumberValue DESC");
        pageSql.Should().Contain("CASE WHEN o1.Sequence IS NULL THEN 1 ELSE 0 END, o1.Sequence ASC");
        pageSql.Should().Contain("LEFT JOIN Dynamic_FieldOption o1 ON o1.Id = s1.NumberValue");
        pageSql.Should().EndWith("r.Id DESC LIMIT @limit OFFSET @offset");
    }

    [Fact]
    public void Build_CountSqlHasNoSortJoins()
    {
        var sorts = new[] { new RecordSort(F(5, FieldType.Integer), true) };

        var (countSql, pageSql, _) = DynamicQueryBuilder.Build(Q(sorts: sorts), new SqliteDialect());

        countSql.Should().NotContain("JOIN").And.NotContain("ORDER BY");
        pageSql.Should().Contain("LEFT JOIN Dynamic_RecordIndex s0");
    }
}
