using System.Text.Json;
using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class FieldServiceTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private ServiceProvider _sp = null!;

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 3, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(_db.Context);
        await DemoPeople.EnsureAsync(_db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        services.AddTestEntities();
        services.AddScoped(_ => _db.Context);
        services.AddScoped<ITransactionManager, EfTransactionManager>();
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime, FakeTime>();
        _sp = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private Task<TRes> Send<TRes>(IRequest<TRes> request) => _sp.GetRequiredService<IMediator>().Send(request);

    private static I18nText Text(string zh = "欄位") => new([KeyValuePair.Create("zh-TW", zh)]);

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private async Task<ModuleDetailResponse> Detail(string key = "order") =>
        (ModuleDetailResponse)(await Send(new GetModuleRequest(key))).Data!;

    private async Task<ModuleDetailResponse> CreateModule()
    {
        await Send(new CreateModuleRequest("order", Text("訂單"), null, null));
        return await Detail();
    }

    private async Task<FieldResponse> AddField(ModuleDetailResponse m, FieldType type, FieldConfig? config = null, long? blockId = null)
    {
        var r = await Send(new AddFieldRequest(m.Key, blockId ?? m.Blocks[0].Id, type, Text(), config ?? new FieldConfig()));
        r.IsCreated.Should().BeTrue();
        return (FieldResponse)r.Data!;
    }

    [Fact]
    public async Task AddField_AssignsKeyFromId()
    {
        var m = await CreateModule();

        var field = await AddField(m, FieldType.Integer);

        field.Key.Should().Be($"f_{field.Id}");
        field.Sequence.Should().Be(2);
        field.Status.Should().Be("Active");
        (await Detail()).Blocks[0].Fields.Should().Contain(f => f.Id == field.Id && f.Key == field.Key);
    }

    [Fact]
    public async Task AddField_BumpsModuleVersion()
    {
        var m = await CreateModule();

        await AddField(m, FieldType.Integer);

        (await Detail()).Version.Should().Be(m.Version + 1);
    }

    [Fact]
    public async Task AddField_UnknownBlock_ReturnsNotFound()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, 9999, FieldType.Text, Text(), new FieldConfig()));

        r.IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task AddField_MaxLengthOnInteger_ReturnsValidationError()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Integer, Text(), new FieldConfig { MaxLength = 10 }));

        r.Errors.Should().ContainKey("Config.MaxLength");
        r.Errors["Config.MaxLength"].Should().Contain("長度上限只適用文字欄位，文字欄位最多 4000");
    }

    [Fact]
    public async Task AddField_Formula_WithRequired_Rejected()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Formula, Text(),
            new FieldConfig { Required = true, ResultType = "number" }));

        r.Errors.Should().ContainKey("Config");
        r.Errors["Config"].Should().Contain("系統計算欄位不能設定必填、預設值、唯讀或長度");
    }

    [Fact]
    public async Task AddField_Formula_InvalidExpression_Rejected()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Formula, Text(),
            new FieldConfig { Formula = "1 +", ResultType = "number" }));

        r.Errors["Config.Formula"].Should().Contain(e => e.StartsWith("公式不正確：") && e.EndsWith("語法錯誤"));
    }

    [Fact]
    public async Task AddField_Formula_TypeMismatch_Rejected()
    {
        var m = await CreateModule();
        var qty = await AddField(m, FieldType.Integer);

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Formula, Text(),
            new FieldConfig { Formula = $"{{{qty.Id}}}*2", ResultType = "date" }));

        r.Errors.Values.SelectMany(v => v).Should().Contain("公式不正確：結果型別與設定不符");
        foreach (var rt in new[] { "number", "currency" })
            (await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Formula, Text(),
                new FieldConfig { Formula = $"{{{qty.Id}}}*2", ResultType = rt }))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateField_Formula_Cycle_Rejected()
    {
        var m = await CreateModule();
        var qty = await AddField(m, FieldType.Integer);
        var a = await AddField(m, FieldType.Formula, new FieldConfig { Formula = $"{{{qty.Id}}}", ResultType = "number" });
        var b = await AddField(m, FieldType.Formula, new FieldConfig { Formula = $"{{{a.Id}}}+1", ResultType = "number" });

        var r = await Send(new UpdateFieldRequest(a.Id, FieldType.Formula, Text(),
            new FieldConfig { Formula = $"{{{b.Id}}}+1", ResultType = "number" }, m.Blocks[0].Id));

        r.Errors.Values.SelectMany(v => v).Should().Contain("公式有循環引用");
    }

    [Fact]
    public async Task SetFieldStatus_ReferencedByFormula_Rejected()
    {
        var m = await CreateModule();
        var qty = await AddField(m, FieldType.Integer);
        await AddField(m, FieldType.Formula, new FieldConfig { Formula = $"{{{qty.Id}}}*2", ResultType = "number" });

        var r = await Send(new SetFieldStatusRequest(qty.Id, FieldStatus.Inactive));

        r.Errors["Status"].Should().Contain("此欄位被公式或規則引用，不能停用");
    }

    [Fact]
    public async Task AddField_AutoNumber_BadFormat_Rejected()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.AutoNumber, Text(),
            new FieldConfig { Format = "NO-{yyyy}" }));

        r.Errors["Config.Format"].Should().Contain("自動編號格式不正確");
    }

    [Fact]
    public async Task AddField_AutoNumber_Valid_Created()
    {
        var m = await CreateModule();

        var f = await AddField(m, FieldType.AutoNumber,
            new FieldConfig { Format = "REP-{yyyyMM}-{0000}", IsFilterable = true, IsSortable = true });

        f.Type.Should().Be(FieldType.AutoNumber);
    }

    [Fact]
    public async Task AddField_FormulaDateWithScale_Rejected()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Formula, Text(),
            new FieldConfig { ResultType = "date", Scale = 2 }));

        r.Errors.Should().ContainKey("Config.Scale");
    }

    [Fact]
    public async Task AddField_FormulaBadResultType_Rejected()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Formula, Text(),
            new FieldConfig { ResultType = "text" }));

        r.Errors.Should().ContainKey("Config.ResultType");
    }

    [Fact]
    public async Task AddField_TextareaFilterable_ReturnsValidationError()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Textarea, Text(), new FieldConfig { IsFilterable = true }));

        r.Errors.Should().ContainKey("Config.IsFilterable");
        r.Errors["Config.IsFilterable"].Should().Contain("此欄位型別不支援篩選");
    }

    [Fact]
    public async Task AddField_MultiEnumSortable_ReturnsValidationError()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.MultiEnum, Text(), new FieldConfig { IsSortable = true }));

        r.Errors.Should().ContainKey("Config.IsSortable");
        r.Errors["Config.IsSortable"].Should().Contain("此欄位型別不支援排序");
    }

    [Fact]
    public async Task AddField_MultiEnumFilterable_Succeeds()
    {
        var m = await CreateModule();

        var f = await AddField(m, FieldType.MultiEnum, new FieldConfig { IsFilterable = true });

        f.Config.IsFilterable.Should().BeTrue();
    }

    [Fact]
    public async Task AddField_ScaleOutOfRange_ReturnsValidationError()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Decimal, Text(), new FieldConfig { Scale = 7 }));

        r.Errors["Config.Scale"].Should().Contain("小數位數只適用數值欄位，範圍 0 到 6");
    }

    [Fact]
    public async Task AddField_EnumDefault_RejectedBecauseNoOptionsYet()
    {
        var m = await CreateModule();

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Enum, Text(),
            new FieldConfig { DefaultValue = Json("\"opt_1\"") }));

        r.Errors.Should().ContainKey("Config.DefaultValue");
    }

    [Fact]
    public async Task UpdateField_ChangingType_ReturnsValidationError()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.Integer);

        var r = await Send(new UpdateFieldRequest(f.Id, FieldType.Text, Text(), new FieldConfig(), f.BlockId));

        r.Errors["Type"].Should().Contain("欄位型別建立後不能更改");
    }

    [Fact]
    public async Task UpdateField_InvalidDefaultValue_ReturnsValidationError()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.Decimal);

        var r = await Send(new UpdateFieldRequest(f.Id, FieldType.Decimal, Text(),
            new FieldConfig { DefaultValue = Json("\"abc\"") }, f.BlockId));

        r.Errors.Should().ContainKey("Config.DefaultValue");
    }

    [Fact]
    public async Task UpdateField_EnumDefault_AcceptsExistingOption()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.Enum);
        var opt = (OptionResponse)(await Send(new AddOptionRequest(f.Id, Text("A"), null))).Data!;

        var r = await Send(new UpdateFieldRequest(f.Id, FieldType.Enum, Text(),
            new FieldConfig { DefaultValue = Json($"\"{opt.Value}\"") }, f.BlockId));

        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateField_MoveToBlockOfOtherModule_ReturnsValidationError()
    {
        var m = await CreateModule();
        await Send(new CreateModuleRequest("contract", Text("合約"), null, null));
        var other = await Detail("contract");
        var f = await AddField(m, FieldType.Integer);

        var r = await Send(new UpdateFieldRequest(f.Id, FieldType.Integer, Text(), new FieldConfig(), other.Blocks[0].Id));

        r.Errors["BlockId"].Should().Contain("區塊不屬於此模組");
    }

    [Fact]
    public async Task UpdateField_MoveToOtherBlock_AppendsToTargetBlock()
    {
        var m = await CreateModule();
        var block = (BlockResponse)(await Send(new AddBlockRequest(m.Key, Text("明細")))).Data!;
        var f = await AddField(m, FieldType.Integer);

        var r = await Send(new UpdateFieldRequest(f.Id, FieldType.Integer, Text("新名"), new FieldConfig(), block.Id));

        r.IsSuccess.Should().BeTrue();
        var detail = await Detail();
        detail.Blocks[0].Fields.Should().NotContain(x => x.Id == f.Id);
        var moved = detail.Blocks[1].Fields.Single();
        moved.Id.Should().Be(f.Id);
        moved.Sequence.Should().Be(1);
        moved.BlockId.Should().Be(block.Id);
    }

    [Fact]
    public async Task SetFieldStatus_TitleField_CannotDeactivate()
    {
        var m = await CreateModule();

        var r = await Send(new SetFieldStatusRequest(m.TitleFieldId!.Value, FieldStatus.Inactive));

        r.Errors.Values.SelectMany(v => v).Should().Contain("記錄標題欄位不能停用");
    }

    [Fact]
    public async Task SetFieldStatus_OrdinaryField_TogglesAndBumpsVersion()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.Integer);

        var r = await Send(new SetFieldStatusRequest(f.Id, FieldStatus.Inactive));

        r.IsSuccess.Should().BeTrue();
        ((FieldResponse)r.Data!).Status.Should().Be("Inactive");
        (await Detail()).Version.Should().Be(m.Version + 2);
    }

    [Fact]
    public async Task AddOption_OnTextField_ReturnsValidationError()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.Text);

        var r = await Send(new AddOptionRequest(f.Id, Text("A"), null));

        r.Errors.Values.SelectMany(v => v).Should().Contain("只有下拉欄位可以有選項");
    }

    [Fact]
    public async Task Validators_UseChineseMessages_ForColorAndEnums()
    {
        var m = await CreateModule();
        var longColor = new string('x', 21);

        (await Send(new AddOptionRequest(1, Text("A"), longColor))).Errors["Color"].Should().Contain("顏色最多 20 個字元");
        (await Send(new UpdateOptionRequest(1, Text("A"), longColor, true))).Errors["Color"].Should().Contain("顏色最多 20 個字元");
        (await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, (FieldType)99, Text(), new FieldConfig())))
            .Errors["Type"].Should().Contain("欄位型別不正確");
        (await Send(new SetFieldStatusRequest(1, (FieldStatus)99))).Errors["Status"].Should().Contain("欄位狀態不正確");
    }

    [Fact]
    public async Task AddOption_AssignsSequenceAndUpdateOptionDeactivates()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.MultiEnum);

        var a = (OptionResponse)(await Send(new AddOptionRequest(f.Id, Text("A"), "#f00"))).Data!;
        var b = (OptionResponse)(await Send(new AddOptionRequest(f.Id, Text("B"), null))).Data!;
        var r = await Send(new UpdateOptionRequest(a.Id, Text("A2"), "#0f0", false));

        a.Value.Should().Be($"opt_{a.Id}");
        (a.Sequence, b.Sequence).Should().Be((1, 2));
        r.IsSuccess.Should().BeTrue();
        var options = (await Detail()).Blocks[0].Fields.Single(x => x.Id == f.Id).Options;
        options[0].Active.Should().BeFalse();
        options[0].Label.Values["zh-TW"].Should().Be("A2");
        (await Detail()).Version.Should().Be(m.Version + 4);
    }

    [Fact]
    public async Task UpdateOption_Unknown_ReturnsNotFound()
    {
        await CreateModule();

        (await Send(new UpdateOptionRequest(9999, Text(), null, true))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task AddBlock_SequenceIsMaxPlusOne_AndUpdateBlockChangesLabel()
    {
        var m = await CreateModule();

        var block = (BlockResponse)(await Send(new AddBlockRequest(m.Key, Text("明細")))).Data!;
        var r = await Send(new UpdateBlockRequest(block.Id, Text("明細二"), true));

        block.Sequence.Should().Be(2);
        r.IsSuccess.Should().BeTrue();
        var updated = (await Detail()).Blocks[1];
        (updated.Label.Values["zh-TW"], updated.Collapsed).Should().Be(("明細二", true));
        (await Detail()).Version.Should().Be(m.Version + 2);
    }

    [Fact]
    public async Task AddBlock_DefaultsToTwoColumns()
    {
        var m = await CreateModule();

        var block = (BlockResponse)(await Send(new AddBlockRequest(m.Key, Text("明細")))).Data!;

        block.Columns.Should().Be(2);
        (await Detail()).Blocks.Should().AllSatisfy(b => b.Columns.Should().Be(2));
    }

    [Fact]
    public async Task UpdateBlock_Columns_SavesAndNullKeepsValue()
    {
        var m = await CreateModule();
        var blockId = m.Blocks[0].Id;

        (await Send(new UpdateBlockRequest(blockId, Text("基本"), false, 3))).IsSuccess.Should().BeTrue();
        (await Detail()).Blocks[0].Columns.Should().Be(3);

        (await Send(new UpdateBlockRequest(blockId, Text("基本二"), false))).IsSuccess.Should().BeTrue();
        (await Detail()).Blocks[0].Columns.Should().Be(3);
    }

    [Fact]
    public async Task UpdateBlock_ColumnsOutOfRange_ReturnsValidationError()
    {
        var m = await CreateModule();

        var r = await Send(new UpdateBlockRequest(m.Blocks[0].Id, Text("基本"), false, 4));

        r.Errors["Columns"].Should().Contain("每列欄數必須是 1 到 3");
    }

    [Fact]
    public async Task ReorderFields_MismatchedIds_ReturnsValidationError()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.Integer);

        var r = await Send(new ReorderFieldsRequest(m.Blocks[0].Id, [f.Id]));

        r.Errors["FieldIds"].Should().Contain("欄位清單與區塊內欄位不一致");
    }

    [Fact]
    public async Task ReorderFields_RewritesSequences()
    {
        var m = await CreateModule();
        var f = await AddField(m, FieldType.Integer);
        var title = m.TitleFieldId!.Value;

        var r = await Send(new ReorderFieldsRequest(m.Blocks[0].Id, [f.Id, title]));

        r.IsSuccess.Should().BeTrue();
        (await Detail()).Blocks[0].Fields.Select(x => x.Id).Should().Equal(f.Id, title);
    }

    private static FieldConfig ModRef(string key, ReferenceOnDelete? onDelete = null, bool required = false) =>
        new() { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, key), OnDelete = onDelete, Required = required };

    private static FieldConfig EntRef(string key = "test.entity") =>
        new() { RefTarget = new ReferenceTarget(ReferenceTarget.EntityKind, key) };

    private async Task<ServiceResult> TryAdd(ModuleDetailResponse m, FieldConfig config) =>
        await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Reference, Text(), config));

    private async Task CreateCustomer()
    {
        await Send(new CreateModuleRequest("customer", Text("客戶"), null, null));
    }

    [Fact]
    public async Task AddField_Reference_ModuleTarget_Created()
    {
        var m = await CreateModule();
        await CreateCustomer();

        var r = await TryAdd(m, ModRef("customer", ReferenceOnDelete.Cascade));

        r.IsCreated.Should().BeTrue();
        ((FieldResponse)r.Data!).Config.RefTarget.Should().Be(new ReferenceTarget("module", "customer"));
    }

    [Fact]
    public async Task AddField_Reference_EntityTarget_Created()
    {
        var m = await CreateModule();

        (await TryAdd(m, EntRef())).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task AddField_Reference_UnknownModule_Rejected()
    {
        var m = await CreateModule();

        var r = await TryAdd(m, ModRef("nope"));

        r.Errors["Config.RefTarget"].Should().Contain("參照目標不正確");
    }

    [Fact]
    public async Task AddField_Reference_ExtensionModule_Rejected()
    {
        var m = await CreateModule();
        await CreateCustomer();
        var ext = _db.Context.Set<Module>().Single(x => x.Key == "customer");
        ext.Kind = ModuleKind.Extension;
        await _db.Context.SaveChangesAsync();

        var r = await TryAdd(m, ModRef("customer"));

        r.Errors["Config.RefTarget"].Should().Contain("參照目標不正確");
    }

    [Fact]
    public async Task AddField_Reference_UnknownEntity_Rejected()
    {
        var m = await CreateModule();

        var r = await TryAdd(m, EntRef("no.such"));

        r.Errors["Config.RefTarget"].Should().Contain("參照目標不正確");
    }

    [Fact]
    public async Task AddField_Reference_MissingTarget_Rejected()
    {
        var m = await CreateModule();

        var r = await TryAdd(m, new FieldConfig());

        r.Errors["Config.RefTarget"].Should().Contain("參照目標不正確");
    }

    [Fact]
    public async Task AddField_Reference_SelfModule_Allowed()
    {
        var m = await CreateModule();

        (await TryAdd(m, ModRef("order"))).IsCreated.Should().BeTrue();
    }

    [Fact]
    public async Task AddField_Reference_OnDeleteOnEntity_Rejected()
    {
        var m = await CreateModule();

        var r = await TryAdd(m, EntRef() with { OnDelete = ReferenceOnDelete.Cascade });

        r.Errors["Config.OnDelete"].Should().Contain("刪除方式只能用在參照動態模組的欄位");
    }

    [Fact]
    public async Task AddField_Reference_SetNullRequired_Rejected()
    {
        var m = await CreateModule();
        await CreateCustomer();

        var r = await TryAdd(m, ModRef("customer", ReferenceOnDelete.SetNull, required: true));

        r.Errors["Config.OnDelete"].Should().Contain("刪除時清空不能用在必填欄位");
    }

    [Fact]
    public async Task AddField_Reference_WithDefault_Rejected()
    {
        var m = await CreateModule();
        await CreateCustomer();

        var r = await TryAdd(m, ModRef("customer") with { DefaultValue = Json("1") });

        r.Errors["Config"].Should().Contain("參照欄位不能設定預設值、長度、小數位、格式或排序");
    }

    [Fact]
    public async Task AddField_Reference_Sortable_Rejected()
    {
        var m = await CreateModule();
        await CreateCustomer();

        var r = await TryAdd(m, ModRef("customer") with { IsSortable = true });

        r.Errors["Config"].Should().Contain("參照欄位不能設定預設值、長度、小數位、格式或排序");
    }

    [Fact]
    public async Task UpdateField_Reference_ChangeTarget_Rejected()
    {
        var m = await CreateModule();
        await CreateCustomer();
        var f = (FieldResponse)(await TryAdd(m, ModRef("customer"))).Data!;

        var r = await Send(new UpdateFieldRequest(f.Id, FieldType.Reference, Text(), ModRef("order"), m.Blocks[0].Id));

        r.Errors["Config.RefTarget"].Should().Contain("參照目標建立後不能更改");
    }

    [Fact]
    public async Task UpdateField_Reference_ChangeOnDelete_Ok()
    {
        var m = await CreateModule();
        await CreateCustomer();
        var f = (FieldResponse)(await TryAdd(m, ModRef("customer"))).Data!;

        var r = await Send(new UpdateFieldRequest(f.Id, FieldType.Reference, Text(), ModRef("customer", ReferenceOnDelete.Cascade), m.Blocks[0].Id));

        r.IsSuccess.Should().BeTrue();
        ((FieldResponse)r.Data!).Config.OnDelete.Should().Be(ReferenceOnDelete.Cascade);
    }

    [Fact]
    public async Task AddField_Formula_UsesReference_Rejected()
    {
        var m = await CreateModule();
        await CreateCustomer();
        var f = (FieldResponse)(await TryAdd(m, ModRef("customer"))).Data!;

        var r = await Send(new AddFieldRequest(m.Key, m.Blocks[0].Id, FieldType.Formula, Text(),
            new FieldConfig { Formula = $"{{{f.Id}}}*2", ResultType = "number" }));

        r.Errors["Config.Formula"].Should().Contain($"公式不正確：欄位 {f.Id} 的型別不能用在公式");
    }

    [Fact]
    public async Task UpdateModule_TitleToReference_Rejected()
    {
        var m = await CreateModule();
        await CreateCustomer();
        var f = (FieldResponse)(await TryAdd(m, ModRef("customer"))).Data!;

        var r = await Send(new UpdateModuleRequest("order", Text("訂單"), null, null, f.Id));

        r.Errors["TitleFieldId"].Should().Contain("記錄標題欄位必須是啟用中的文字或自動編號欄位");
    }
}
