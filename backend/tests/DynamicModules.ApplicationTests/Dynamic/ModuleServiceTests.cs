using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.DeactivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModules;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic;

public class ModuleServiceTests : IAsyncLifetime
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

    private static I18nText Name(string zh = "訂單") => new([KeyValuePair.Create("zh-TW", zh)]);

    private static I18nText Section(params (string Locale, string Text)[] items) =>
        new(items.Select(i => KeyValuePair.Create(i.Locale, i.Text)));

    private Task<ServiceResult> Create(string key = "order", I18nText? name = null) =>
        Send(new CreateModuleRequest(key, name ?? Name(), null, null));

    [Fact]
    public async Task CreateModule_AssignsSequentialProgramCodes()
    {
        var r1 = await Create("order");
        var r2 = await Create("contract", Name("合約"));

        r1.IsCreated.Should().BeTrue();
        ((ModuleResponse)r1.Data!).ProgramCode.Should().Be("DYN00001");
        ((ModuleResponse)r2.Data!).ProgramCode.Should().Be("DYN00002");
        var programs = await _db.Context.Set<DynamicProgram>().AsNoTracking()
            .Where(p => p.ProgramCode != "DYN00000").OrderBy(p => p.ProgramCode).ToListAsync();
        programs.Select(p => p.ProgramCode).Should().Equal("DYN00001", "DYN00002");
        programs.Should().OnlyContain(p => p.IsBeta);
        programs[0].NameI18n.Values["zh-TW"].Should().Be("訂單");
    }

    [Fact]
    public async Task CreateModule_CreatesDefaultBlockAndTitleField()
    {
        var result = await Create();
        var response = (ModuleResponse)result.Data!;

        response.Status.Should().Be("Draft");
        response.Version.Should().Be(1);
        var module = await _db.Context.Set<Module>().AsNoTracking()
            .Include(m => m.Blocks).ThenInclude(b => b.Fields).SingleAsync();
        module.CreatedUserId.Should().Be("admin");
        module.Blocks.Should().ContainSingle();
        var block = module.Blocks[0];
        block.LabelI18n.Values["zh-TW"].Should().Be("基本資料");
        block.LabelI18n.Values["en-US"].Should().Be("General");
        var field = block.Fields.Should().ContainSingle().Subject;
        field.Key.Should().Be($"f_{field.Id}");
        field.Type.Should().Be(FieldType.Text);
        field.LabelI18n.Values["zh-TW"].Should().Be("名稱");
        field.LabelI18n.Values["en-US"].Should().Be("Name");
        field.Config.Required.Should().BeTrue();
        field.Config.ShowInList.Should().BeTrue();
        field.Status.Should().Be(FieldStatus.Active);
        module.TitleFieldId.Should().Be(field.Id);
        response.TitleFieldId.Should().Be(field.Id);
    }

    [Fact]
    public async Task CreateModule_DuplicateKey_ReturnsConflict()
    {
        await Create();
        var result = await Create();

        result.IsConflict.Should().BeTrue();
        result.Message.Should().Be("模組代碼已存在");
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("1abc")]
    [InlineData("a")]
    public async Task CreateModule_InvalidKey_ReturnsValidationError(string key)
    {
        var result = await Create(key);

        result.IsSuccess.Should().BeFalse();
        result.Errors["Key"].Should().Contain("模組代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭");
    }

    [Fact]
    public async Task CreateModule_SectionOver100Chars_ReturnsValidationError()
    {
        var result = await Send(new CreateModuleRequest("order", Name(), null, Name(new string('a', 101))));

        result.IsSuccess.Should().BeFalse();
        result.Errors["Section"].Should().Contain("選單分區不能超過 100 字");
    }

    [Fact]
    public async Task CreateModule_SectionOver100CharsInAnotherLocale_ReturnsValidationError()
    {
        var section = Section(("zh-TW", "主檔"), ("en-US", new string('a', 101)));

        var result = await Send(new CreateModuleRequest("order", Name(), null, section));

        result.IsSuccess.Should().BeFalse();
        result.Errors["Section"].Should().Contain("選單分區不能超過 100 字");
    }

    [Fact]
    public async Task CreateModule_SectionWithUnsupportedLocale_ReturnsValidationError()
    {
        var result = await Send(new CreateModuleRequest("order", Name(), null, Section(("zh-TW", "主檔"), ("ja", "マスタ"))));

        result.IsSuccess.Should().BeFalse();
        result.Errors["Section"].Should().Contain("不支援的語系：ja");
    }

    [Fact]
    public async Task CreateModule_SectionI18n_StoresZhTwAsGroupKeyAndEveryLocale()
    {
        var created = (ModuleResponse)(await Send(new CreateModuleRequest("order", Name(), null,
            Section(("zh-TW", "主檔"), ("en-US", "Master data"))))).Data!;

        created.Section!.Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "主檔", ["en-US"] = "Master data" });
        var row = await _db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "order");
        row.Section.Should().Be("主檔");
        row.SectionI18n!.Resolve("en-US").Should().Be("Master data");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateModule_SectionWithBlankZhTw_HasNoSection(string zh)
    {
        var created = (ModuleResponse)(await Send(new CreateModuleRequest("order", Name(), null,
            Section(("zh-TW", zh), ("en-US", "Master data"))))).Data!;

        created.Section.Should().BeNull();
        var row = await _db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "order");
        row.Section.Should().BeNull();
        row.SectionI18n.Should().BeNull();
    }

    [Fact]
    public async Task UpdateModule_SectionI18n_ReplacesBoth_AndBlankClears()
    {
        var created = (ModuleResponse)(await Create()).Data!;

        var updated = (ModuleResponse)(await Send(new UpdateModuleRequest("order", Name(), null,
            Section(("zh-TW", "銷售"), ("th", "การขาย")), created.TitleFieldId))).Data!;
        updated.Section!.Resolve("th").Should().Be("การขาย");
        (await _db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "order")).Section.Should().Be("銷售");

        var cleared = (ModuleResponse)(await Send(new UpdateModuleRequest("order", Name(), null, null, created.TitleFieldId))).Data!;
        cleared.Section.Should().BeNull();
        var row = await _db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "order");
        row.Section.Should().BeNull();
        row.SectionI18n.Should().BeNull();
    }

    [Fact]
    public async Task UpdateModule_SectionOver100Chars_ReturnsValidationError()
    {
        var created = (ModuleResponse)(await Create()).Data!;

        var result = await Send(new UpdateModuleRequest("order", Name(), null, Name(new string('a', 101)), created.TitleFieldId));

        result.IsSuccess.Should().BeFalse();
        result.Errors["Section"].Should().Contain("選單分區不能超過 100 字");
    }

    [Fact]
    public async Task CreateModule_MissingZhTwName_ReturnsValidationError()
    {
        var result = await Create("order", new I18nText([KeyValuePair.Create("en-US", "Order")]));

        result.Errors["Name"].Should().Contain("名稱必須有繁體中文");
        (await _db.Context.Set<Module>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ActivateModule_WithTitleField_Activates()
    {
        await Create();

        var result = await Send(new ActivateModuleRequest("order"));

        result.IsSuccess.Should().BeTrue();
        ((await Send(new GetModuleRequest("order"))).Data as ModuleDetailResponse)!.Status.Should().Be("Active");
    }

    [Fact]
    public async Task CreateModule_ProgramInactiveUntilActivated()
    {
        await Create();
        async Task<bool> ProgramActive() => (await _db.Context.Set<DynamicProgram>().AsNoTracking()
            .SingleAsync(p => p.ProgramCode == "DYN00001")).Active;

        (await ProgramActive()).Should().BeFalse();
        await Send(new ActivateModuleRequest("order"));
        (await ProgramActive()).Should().BeTrue();
        await Send(new DeactivateModuleRequest("order"));
        (await ProgramActive()).Should().BeFalse();
    }

    [Fact]
    public async Task ActivateModule_WithoutTitleField_ReturnsValidationError()
    {
        await Create();
        await Send(new UpdateModuleRequest("order", Name(), null, null, null));

        var result = await Send(new ActivateModuleRequest("order"));

        result.Errors["TitleFieldId"].Should().Contain("啟用前必須指定記錄標題欄位");
    }

    [Fact]
    public async Task ActivateModule_UnknownKey_ReturnsNotFound()
    {
        (await Send(new ActivateModuleRequest("nope"))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateModule_BumpsVersion()
    {
        var created = (ModuleResponse)(await Create()).Data!;

        var result = await Send(new UpdateModuleRequest("order", Name("訂單單"), "shop", Name("sales"), created.TitleFieldId));

        var updated = (ModuleResponse)result.Data!;
        updated.Version.Should().Be(2);
        updated.Name.Values["zh-TW"].Should().Be("訂單單");
        updated.Icon.Should().Be("shop");
        var module = await _db.Context.Set<Module>().AsNoTracking().SingleAsync();
        module.ModifiedUserId.Should().Be("admin");
        module.ModifiedTime.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateModule_TitleFieldNotInModule_ReturnsValidationError()
    {
        await Create();

        var result = await Send(new UpdateModuleRequest("order", Name(), null, null, 99999));

        result.Errors["TitleFieldId"].Should().Contain("記錄標題欄位不存在");
    }

    [Fact]
    public async Task CreateModule_NullKey_ReturnsValidationError()
    {
        var result = await Create(null!);

        result.Errors["Key"].Should().Contain("模組代碼只能用小寫英文、數字、連字號，2 到 40 字，英文開頭");
    }

    private async Task<(ModuleResponse Module, Field Title)> CreateWithTitle()
    {
        var m = (ModuleResponse)(await Create()).Data!;
        var title = await _db.Context.Set<Field>().SingleAsync(f => f.Id == m.TitleFieldId);
        return (m, title);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("inactive")]
    public async Task UpdateModule_TitleFieldNotActiveText_ReturnsValidationError(string kind)
    {
        var (m, title) = await CreateWithTitle();
        if (kind == "type") title.Type = FieldType.Integer; else title.Status = FieldStatus.Inactive;
        await _db.Context.SaveChangesAsync();

        var result = await Send(new UpdateModuleRequest("order", Name(), null, null, title.Id));

        result.Errors["TitleFieldId"].Should().Contain("記錄標題欄位必須是啟用中的文字或自動編號欄位");
    }

    [Fact]
    public async Task UpdateModule_TitleToAutoNumber_Allowed()
    {
        var (_, title) = await CreateWithTitle();
        title.Type = FieldType.AutoNumber;
        title.Config = title.Config with { Format = "NO-{000}" };
        await _db.Context.SaveChangesAsync();

        var result = await Send(new UpdateModuleRequest("order", Name(), null, null, title.Id));

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateModule_TitleToInteger_RejectedWithNewMessage()
    {
        var (_, title) = await CreateWithTitle();
        title.Type = FieldType.Integer;
        await _db.Context.SaveChangesAsync();

        var result = await Send(new UpdateModuleRequest("order", Name(), null, null, title.Id));

        result.Errors["TitleFieldId"].Should().Contain("記錄標題欄位必須是啟用中的文字或自動編號欄位");
    }

    [Fact]
    public async Task UpdateModule_TitleToFieldWithHiddenException_Rejected()
    {
        var created = (ModuleResponse)(await Create()).Data!;
        var block = (await _db.Context.Set<Block>().SingleAsync()).Id;
        var other = (FieldResponse)(await Send(new AddFieldRequest("order", block, FieldType.Text, Name("備註"), new FieldConfig()))).Data!;
        var sales = await _db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        _db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = other.Id, GroupId = sales.Id, Access = FieldAccess.Hidden });
        await _db.Context.SaveChangesAsync();

        var result = await Send(new UpdateModuleRequest("order", Name(), null, null, other.Id));

        result.Errors["TitleFieldId"].Should().Contain("記錄標題欄位不能是有隱藏例外的欄位");
        (await _db.Context.Set<Module>().AsNoTracking().SingleAsync()).TitleFieldId.Should().Be(created.TitleFieldId);
    }

    [Fact]
    public async Task UpdateModule_NullTitleOnActiveModule_ReturnsValidationError()
    {
        await Create();
        await Send(new ActivateModuleRequest("order"));

        var result = await Send(new UpdateModuleRequest("order", Name(), null, null, null));

        result.Errors["TitleFieldId"].Should().Contain("啟用中的模組必須有記錄標題欄位");
    }

    [Theory]
    [InlineData("type")]
    [InlineData("inactive")]
    public async Task ActivateModule_TitleFieldNotActiveText_ReturnsValidationError(string kind)
    {
        var (_, title) = await CreateWithTitle();
        if (kind == "type") title.Type = FieldType.Integer; else title.Status = FieldStatus.Inactive;
        await _db.Context.SaveChangesAsync();

        var result = await Send(new ActivateModuleRequest("order"));

        result.Errors["TitleFieldId"].Should().Contain("啟用前必須指定記錄標題欄位");
    }

    [Fact]
    public async Task DeactivateModule_SetsInactiveAndBumpsVersion()
    {
        await Create();
        await Send(new ActivateModuleRequest("order"));

        var result = await Send(new DeactivateModuleRequest("order"));

        result.IsSuccess.Should().BeTrue();
        var detail = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;
        detail.Status.Should().Be("Inactive");
        detail.Version.Should().Be(3);
    }

    [Fact]
    public async Task GetModules_ReturnsAllStatuses()
    {
        await Create("order");
        await Create("contract", Name("合約"));
        await Send(new ActivateModuleRequest("contract"));

        var list = (List<ModuleResponse>)(await Send(new GetModulesRequest())).Data!;

        list.Select(m => m.Key).Should().BeEquivalentTo("order", "contract");
    }

    [Fact]
    public async Task GetModule_ReturnsBlocksFieldsOptions_AndNotFoundForUnknown()
    {
        await Create();
        var detail = (ModuleDetailResponse)(await Send(new GetModuleRequest("order"))).Data!;

        detail.Blocks.Should().ContainSingle();
        var field = detail.Blocks[0].Fields.Should().ContainSingle().Subject;
        field.Key.Should().Be($"f_{field.Id}");
        field.Status.Should().Be("Active");
        field.Options.Should().BeEmpty();
        (await Send(new GetModuleRequest("nope"))).IsNotFound.Should().BeTrue();
    }
}
