using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using Microsoft.Data.Sqlite;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

public class SchemaTests
{
    [Fact]
    public async Task Migrate_CreatesAllDynamicTables()
    {
        await using var db = await SqliteTestDb.CreateAsync();

        var tables = await db.Context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync();

        tables.Should().Contain([
            "Dynamic_Module", "Dynamic_Block", "Dynamic_Field", "Dynamic_FieldOption",
            "Dynamic_Record", "Dynamic_Program", "Dynamic_AppUser"
        ]);
    }

    [Fact]
    public async Task NumberSequence_Period_Allows20Chars()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        db.Context.Model.FindEntityType(typeof(NumberSequence))!.FindProperty(nameof(NumberSequence.Period))!
            .GetMaxLength().Should().Be(20);
        db.Context.Set<NumberSequence>().Add(new NumberSequence { ModuleId = 1, FieldId = 1, Period = "p1234567890", NextValue = 1 });
        await db.Context.SaveChangesAsync();
        (await db.Context.Set<NumberSequence>().SingleAsync()).Period.Should().Be("p1234567890");
    }

    [Fact]
    public async Task Schema_HasRecordIndexTableAndIndexes()
    {
        await using var db = await SqliteTestDb.CreateAsync();

        var pk = await db.Context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Dynamic_RecordIndex') WHERE pk > 0 ORDER BY pk")
            .ToListAsync();
        var indexes = await db.Context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Dynamic_RecordIndex'")
            .ToListAsync();

        pk.Should().Equal("RecordId", "FieldId", "Seq");
        indexes.Should().Contain([
            "IX_Dynamic_RecordIndex_FieldId_TextValue", "IX_Dynamic_RecordIndex_FieldId_NumberValue",
            "IX_Dynamic_RecordIndex_FieldId_DateValue", "IX_Dynamic_RecordIndex_FieldId_RefKey"
        ]);
    }

    [Fact]
    public async Task Schema_HasPermissionTables()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var expected = new Dictionary<string, string[]>
        {
            ["Dynamic_PermissionGroup"] = ["Id"],
            ["Dynamic_UserGroupMapping"] = ["GroupId", "UserId"],
            ["Dynamic_GroupProgramPrivilege"] = ["GroupId", "ProgramCode"],
            ["Dynamic_UserProgramPrivilege"] = ["UserId", "ProgramCode"],
            ["Dynamic_FieldPermission"] = ["FieldId", "GroupId"],
            ["Dynamic_ModuleDataScope"] = ["ModuleId", "GroupId"],
            ["Dynamic_FieldOptionScope"] = ["OptionId", "GroupId"],
        };

        foreach (var (table, keys) in expected)
        {
            var pk = await db.Context.Database
                .SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table}) WHERE pk > 0 ORDER BY pk")
                .ToListAsync();
            pk.Should().Equal(keys, table);
        }

        var userCols = await db.Context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Dynamic_AppUser')").ToListAsync();
        userCols.Should().Contain("IsSystemAdmin");
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        await using var db = await SqliteTestDb.CreateAsync();

        await SeedData.EnsureSeededAsync(db.Context);
        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<DynamicModules.Domain.Entities.Dynamic.AppUser>().CountAsync()).Should().Be(1);
        (await db.Context.Set<DynamicModules.Domain.Entities.Dynamic.DynamicProgram>()
            .CountAsync(p => p.ProgramCode == "DYN00000")).Should().Be(1);
    }
}

public class SqliteConnectionStringsTests
{
    [Fact]
    public void RelativeFile_IsResolvedAgainstContentRoot()
    {
        var cs = SqliteConnectionStrings.ResolveDataSource("Data Source=../../data/poc.db", "/a/b/c");
        new SqliteConnectionStringBuilder(cs).DataSource.Should().Be("/a/data/poc.db");
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=file:x?mode=memory&cache=shared")]
    public void InMemory_IsUnchanged(string cs) =>
        SqliteConnectionStrings.ResolveDataSource(cs, "/a/b/c").Should().Be(cs);
}

public class JsonChangeTrackingTests
{
    [Fact]
    public async Task InPlaceI18nMutation_IsPersisted()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        db.Context.Set<DynamicProgram>().Add(new DynamicProgram
        {
            ProgramCode = "DYN00001",
            NameI18n = new I18nText([KeyValuePair.Create("zh-TW", "甲")])
        });
        await db.Context.SaveChangesAsync();

        var loaded = await db.Context.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == "DYN00001");
        loaded.NameI18n.Set("en-US", "A");
        await db.Context.SaveChangesAsync();

        await using var fresh = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(db.Connection).Options);
        var reloaded = await fresh.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == "DYN00001");
        reloaded.NameI18n.Values.Should().Contain("en-US", "A");
    }

    [Fact]
    public async Task StoredJson_I18nIsFlatObject_ConfigIsCamelCase()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        db.Context.Set<DynamicProgram>().Add(new DynamicProgram { ProgramCode = "DYN00001" });
        var module = new Module
        {
            Key = "orders",
            ProgramCode = "DYN00001",
            NameI18n = new I18nText([KeyValuePair.Create("zh-TW", "訂單"), KeyValuePair.Create("en-US", "Order")]),
            Blocks = [new Block()]
        };
        db.Context.Set<Module>().Add(module);
        await db.Context.SaveChangesAsync();
        db.Context.Set<Field>().Add(new Field
        {
            ModuleId = module.Id,
            BlockId = module.Blocks[0].Id,
            Key = "f_1",
            Config = new FieldConfig { Required = true, MaxLength = 5 }
        });
        await db.Context.SaveChangesAsync();

        var name = await db.Context.Database.SqlQueryRaw<string>("SELECT NameI18n AS Value FROM Dynamic_Module").SingleAsync();
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(name)
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["zh-TW"] = "訂單", ["en-US"] = "Order" });
        (await db.Context.Database.SqlQueryRaw<string>("SELECT Config AS Value FROM Dynamic_Field").SingleAsync())
            .Should().Contain("\"required\":true").And.Contain("\"maxLength\":5");
    }
}
