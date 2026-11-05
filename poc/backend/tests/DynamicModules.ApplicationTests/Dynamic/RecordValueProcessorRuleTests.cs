using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

public class RecordValueProcessorRuleTests
{
    private static readonly FieldTypeRegistry Registry = FieldTypeRegistry.CreateDefault();
    private static readonly RuleContext Ctx = new(new DateOnly(2026, 11, 3));
    private static readonly HashSet<string> NoUsers = [];

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Field Fld(long id, FieldType type, FieldConfig? config = null, params long[] options) => new()
    {
        Id = id, Key = $"f_{id}", Type = type, Status = FieldStatus.Active, Config = config ?? new FieldConfig(),
        Options = options.Select((o, i) => new FieldOption { Id = o, Active = true, Sequence = i }).ToList()
    };

    // 1 flag(boolean) 2 memo(text) 3 tags(multienum 21..23)
    private static Module Mod(FieldConfig? memo = null, params Rule[] rules) => new()
    {
        Blocks = [new Block { Id = 1, Fields = [Fld(1, FieldType.Boolean), Fld(2, FieldType.Text, memo), Fld(3, FieldType.MultiEnum, null, 21, 22, 23)] }],
        Rules = rules.ToList()
    };

    private static Rule R(RuleKind kind, string condition, string[] targets, long[]? optionIds = null, string? message = null) => new()
    {
        Kind = kind, Condition = J(condition), Targets = targets.ToList(), OptionIds = optionIds?.ToList() ?? [],
        MessageI18n = message is null ? null : new I18nText([new("zh-TW", message)]), Active = true
    };

    private static EffectivePermissions Perms(params long[] hidden) => new(
        new HashSet<PermissionAction> { PermissionAction.Add, PermissionAction.Edit }, DataScope.All, null,
        hidden.ToHashSet(), new HashSet<long>(), new HashSet<long>(), "");

    private static Dictionary<string, JsonElement?> V(params (string Key, string Json)[] items) =>
        items.ToDictionary(i => i.Key, i => (JsonElement?)J(i.Json));

    private const string FlagTrue = "{\"field\":1,\"op\":\"eq\",\"value\":true}";

    [Fact]
    public void ReadOnlyAllOnStored_UpdateRejectedOnRecordKey()
    {
        var module = Mod(null, R(RuleKind.ReadOnly, FlagTrue, ["*"]));

        var (_, errors) = RecordValueProcessor.Process(module, V(("f_2", "\"x\"")), V(("f_1", "true")), NoUsers, Registry, Perms(), Ctx);

        errors.Should().ContainKey("_record").WhoseValue.Should().Equal("此記錄目前不可修改");
    }

    [Fact]
    public void RuleHidden_ValueKeptAndRequiredSkipped()
    {
        var module = Mod(new FieldConfig { Required = true }, R(RuleKind.Visibility, FlagTrue, ["field:2"]));

        var (values, errors) = RecordValueProcessor.Process(
            module, V(("f_1", "false")), V(("f_2", "\"keep\"")), NoUsers, Registry, Perms(), Ctx);

        errors.Should().BeEmpty();
        values["f_2"]!.Value.GetString().Should().Be("keep");
    }

    [Fact]
    public void ConfigReadOnly_CreateAppliesDefault_UpdateKeepsStored()
    {
        var module = Mod(new FieldConfig { ReadOnly = true, DefaultValue = J("\"預設\"") });

        var (created, _) = RecordValueProcessor.Process(module, V(("f_2", "\"x\"")), null, NoUsers, Registry, Perms(), Ctx);
        created["f_2"]!.Value.GetString().Should().Be("預設");

        var (updated, _) = RecordValueProcessor.Process(module, V(("f_2", "\"y\"")), created, NoUsers, Registry, Perms(), Ctx);
        updated["f_2"]!.Value.GetString().Should().Be("預設");
    }

    [Fact]
    public void MultiEnumOptionFilter_OnlyNewlyAddedOptionsChecked()
    {
        var module = Mod(null, R(RuleKind.OptionFilter, "{\"all\":[]}", ["field:3"], [22]));
        var stored = V(("f_3", "[\"opt_21\"]")); // 21 已不在可選範圍，但是舊值

        var (_, keep) = RecordValueProcessor.Process(module, V(("f_3", "[\"opt_21\",\"opt_22\"]")), stored, NoUsers, Registry, Perms(), Ctx);
        keep.Should().BeEmpty();

        var (_, add) = RecordValueProcessor.Process(module, V(("f_3", "[\"opt_21\",\"opt_23\"]")), stored, NoUsers, Registry, Perms(), Ctx);
        add["f_3"].Should().Equal("選項不在可選範圍");
    }

    [Fact]
    public void PermissionHiddenField_RulesHaveNoEffect()
    {
        // memo 對使用者隱藏：規則必填、選項限制都不生效
        var module = Mod(null, R(RuleKind.Required, "{\"all\":[]}", ["field:2"]));

        var (_, errors) = RecordValueProcessor.Process(module, V(), null, NoUsers, Registry, Perms(2), Ctx);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validation_FirstVisibleTargetOtherwiseRecordKey()
    {
        var rule = R(RuleKind.Validation, FlagTrue, ["field:2", "field:3"], null, "旗標必須為是");
        var module = Mod(null, rule);
        var input = V(("f_1", "false"));

        var (_, visible) = RecordValueProcessor.Process(module, input, null, NoUsers, Registry, Perms(2), Ctx);
        visible.Should().ContainKey("f_3").WhoseValue.Should().Equal("旗標必須為是");

        var (_, none) = RecordValueProcessor.Process(module, input, null, NoUsers, Registry, Perms(2, 3), Ctx);
        none.Should().ContainKey("_record").WhoseValue.Should().Equal("旗標必須為是");
    }
    private static EffectivePermissions PermsReadOnly(params long[] readOnly) => new(
        new HashSet<PermissionAction> { PermissionAction.Add, PermissionAction.Edit }, DataScope.All, null,
        new HashSet<long>(), readOnly.ToHashSet(), new HashSet<long>(), "");

    // 旗標為真時 tags 只能選 22；舊記錄旗標為假（範圍不限），存檔改成真 → 範圍改變，整個值重驗
    private static Rule Only22() => R(RuleKind.OptionFilter, FlagTrue, ["field:3"], [22]);

    [Fact]
    public void OptionRange_PermissionReadOnlyChild_NoRangeError()
    {
        var module = Mod(null, Only22());
        var stored = V(("f_1", "false"), ("f_3", "[\"opt_21\"]"));

        var (_, errors) = RecordValueProcessor.Process(module, V(("f_1", "true")), stored, NoUsers, Registry, PermsReadOnly(3), Ctx);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void OptionRange_ConfigReadOnlyChildDefaultOutOfRange_NoRangeErrorOnCreate()
    {
        var tags = Fld(3, FieldType.MultiEnum, new FieldConfig { ReadOnly = true, DefaultValue = J("[\"opt_21\"]") }, 21, 22, 23);
        var module = new Module { Blocks = [new Block { Id = 1, Fields = [Fld(1, FieldType.Boolean), tags] }], Rules = [Only22()] };

        var (_, errors) = RecordValueProcessor.Process(module, V(("f_1", "true")), null, NoUsers, Registry, Perms(), Ctx);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void OptionRange_RuleReadOnlyChild_NoRangeError()
    {
        var module = Mod(null, Only22(), R(RuleKind.ReadOnly, "{\"all\":[]}", ["field:3"]));
        var stored = V(("f_1", "false"), ("f_3", "[\"opt_21\"]"));

        var (_, errors) = RecordValueProcessor.Process(module, V(("f_1", "true")), stored, NoUsers, Registry, Perms(), Ctx);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validation_SkipsRuleHiddenTarget_PicksNextVisible()
    {
        var module = Mod(null,
            R(RuleKind.Visibility, FlagTrue, ["field:2"]),
            R(RuleKind.Validation, FlagTrue, ["field:2", "field:3"], null, "旗標必須為是"));

        var (_, errors) = RecordValueProcessor.Process(module, V(("f_1", "false")), null, NoUsers, Registry, Perms(), Ctx);

        errors.Should().ContainKey("f_3").WhoseValue.Should().Equal("旗標必須為是");
        errors.Should().NotContainKey("f_2");
    }

    [Fact]
    public void Validation_AllTargetsRuleHidden_FallsBackToRecordKey()
    {
        var module = Mod(null,
            R(RuleKind.Visibility, FlagTrue, ["field:2"]),
            R(RuleKind.Validation, FlagTrue, ["field:2"], null, "旗標必須為是"));

        var (_, errors) = RecordValueProcessor.Process(module, V(("f_1", "false")), null, NoUsers, Registry, Perms(), Ctx);

        errors.Should().ContainKey("_record").WhoseValue.Should().Equal("旗標必須為是");
    }
}
