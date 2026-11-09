using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>生命週期鎖第一道（共用主檔設計 §7）：凍結欄位給了不同的值整筆失敗，優先於 systemKeys；值沒變照常；不套預設、公式不重算。</summary>
public class RecordValueProcessorFrozenTests
{
    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();
    private static readonly RuleContext Ctx = new(new DateOnly(2026, 11, 9));
    private static readonly HashSet<string> NoUsers = [];

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 1 名稱(text) 2 備忘(text, 凍結, 有預設) 3 人數(integer) 4 兩倍(formula, 凍結)
    private static Module Mod() => new()
    {
        Blocks = [new Block { Id = 1, Fields =
        [
            new Field { Id = 1, Key = "f_1", Type = FieldType.Text, Status = FieldStatus.Active, Config = new FieldConfig() },
            new Field { Id = 2, Key = "f_2", Type = FieldType.Text, Status = FieldStatus.Active, Config = new FieldConfig { DefaultValue = J("\"預設\"") } },
            new Field { Id = 3, Key = "f_3", Type = FieldType.Integer, Status = FieldStatus.Active, Config = new FieldConfig() },
            new Field { Id = 4, Key = "f_4", Type = FieldType.Formula, Status = FieldStatus.Active, Config = new FieldConfig { Formula = "{3} * 2", ResultType = "number" } }
        ] }]
    };

    private static EffectivePermissions Perms() => new(
        new HashSet<PermissionAction> { PermissionAction.Add, PermissionAction.Edit }, DataScope.All, null,
        new HashSet<long>(), new HashSet<long> { 2, 4 }, new HashSet<long>(), "")
    {
        Frozen = new Dictionary<long, FieldLock>
        {
            [2] = new("crm", new I18nText([new("zh-TW", "客戶關係")])), [4] = new("crm", new I18nText([new("zh-TW", "客戶關係")]))
        }
    };

    private static Dictionary<string, JsonElement?> V(params (string Key, string Json)[] items) =>
        items.ToDictionary(i => i.Key, i => (JsonElement?)J(i.Json));

    [Fact]
    public void ADifferentValue_Throws_EvenAsASystemKey()
    {
        var act = () => RecordValueProcessor.Process(Mod(), V(("f_2", "\"新\"")), V(("f_2", "\"舊\"")), NoUsers, Registry, Perms(), Ctx,
            systemKeys: new HashSet<string> { "f_2" });
        act.Should().Throw<FrozenFieldException>().WithMessage("*已關閉的應用程式「客戶關係」*");
    }

    [Fact]
    public void ClearingIt_Throws_ButTheSameValueOrNoValuePasses()
    {
        var previous = V(("f_1", "\"甲\""), ("f_2", "\"舊\""), ("f_3", "5"), ("f_4", "10"));
        ((Action)(() => RecordValueProcessor.Process(Mod(), new Dictionary<string, JsonElement?> { ["f_2"] = null }, previous, NoUsers, Registry, Perms(), Ctx)))
            .Should().Throw<FrozenFieldException>();

        var (values, errors) = RecordValueProcessor.Process(Mod(), V(("f_1", "\"乙\""), ("f_2", "\"舊\""), ("f_3", "7")), previous, NoUsers, Registry, Perms(), Ctx);
        errors.Should().BeEmpty();
        values["f_1"]!.Value.GetString().Should().Be("乙");
        values["f_2"]!.Value.GetString().Should().Be("舊");
        values["f_4"]!.Value.GetDecimal().Should().Be(10, "凍結的公式保持最後的值");
    }

    [Fact]
    public void Create_FrozenFieldGetsNoDefault()
    {
        var (values, errors) = RecordValueProcessor.Process(Mod(), V(("f_1", "\"甲\""), ("f_3", "5")), null, NoUsers, Registry, Perms(), Ctx);
        errors.Should().BeEmpty();
        values.Should().NotContainKey("f_2").And.NotContainKey("f_4");
    }

    [Fact]
    public void Restore_PutsBackTheOldValues_AndDropsThemOnCreate()
    {
        var module = Mod();
        var values = V(("f_1", "\"乙\""), ("f_2", "\"被改\""), ("f_4", "99"));
        FieldLocks.Restore(module, Perms().Frozen, V(("f_2", "\"舊\""), ("f_4", "10")), values);
        values["f_2"]!.Value.GetString().Should().Be("舊");
        values["f_4"]!.Value.GetDecimal().Should().Be(10);

        var created = V(("f_1", "\"乙\""), ("f_2", "\"x\""));
        FieldLocks.Restore(module, Perms().Frozen, null, created);
        created.Should().NotContainKey("f_2");
    }
}
