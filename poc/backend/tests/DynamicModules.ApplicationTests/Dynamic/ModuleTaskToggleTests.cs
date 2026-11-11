using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>模組層任務開關（任務設計 §4.4）：預設開、false 存得進去、null 不改、Closed 也能改、schema 帶出、主檔要系統管理員。</summary>
public class ModuleTaskToggleTests : DesignerTestBase
{
    private Task<Application.Common.ServiceResult> Toggle(ModuleDetailResponse m, bool? on, string? icon = null) =>
        Send(new UpdateModuleRequest(m.Key, m.Name, icon ?? m.Icon, m.Section, m.TitleFieldId, m.TitleTemplate, on));

    /// <summary>用另一個 DbContext（同一條連線）讀，避開目前 context 的追蹤快取。</summary>
    private async Task<Module> Fresh(string key)
    {
        await using var ctx = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(Db.Connection, b => b.MigrationsAssembly("DynamicModules.Persistence")).Options);
        return await ctx.Set<Module>().AsNoTracking().SingleAsync(x => x.Key == key);
    }

    [Fact]
    public async Task NewModule_TasksEnabledByDefault()
    {
        await NewModule("tg");

        (await Detail("tg")).TasksEnabled.Should().BeTrue();
        (await Fresh("tg")).TasksEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Update_TasksEnabledFalse_PersistsAcrossContexts()
    {
        await NewModule("tg");
        var before = await Detail("tg");

        var r = await Toggle(before, false);

        r.IsSuccess.Should().BeTrue(r.Message);
        ((ModuleResponse)r.Data!).TasksEnabled.Should().BeFalse();
        var stored = await Fresh("tg");
        stored.TasksEnabled.Should().BeFalse("HasSentinel(true) 才不會被資料庫預設值翻回 true");
        stored.Version.Should().Be(before.Version + 1);
    }

    [Fact]
    public async Task Update_TasksEnabledNull_LeavesUnchanged()
    {
        await NewModule("tg");
        (await Toggle(await Detail("tg"), false)).IsSuccess.Should().BeTrue();

        var r = await Toggle(await Detail("tg"), null, icon: "star");

        r.IsSuccess.Should().BeTrue(r.Message);
        var stored = await Fresh("tg");
        stored.Icon.Should().Be("star");
        stored.TasksEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Update_ClosedModule_CanToggle()
    {
        await NewModule("tg", activate: true);
        await Db.Context.Set<Module>().Where(x => x.Key == "tg").ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ModuleStatus.Closed));
        Db.Context.ChangeTracker.Clear();

        var r = await Toggle(await Detail("tg"), false);

        r.IsSuccess.Should().BeTrue(r.Message);
        (await Fresh("tg")).TasksEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Schema_ExposesTasksEnabled()
    {
        await NewModule("tg", activate: true);
        ((ModuleSchemaResponse)(await Send(new GetModuleSchemaRequest("tg"))).Data!).TasksEnabled.Should().BeTrue();

        (await Toggle(await Detail("tg"), false)).IsSuccess.Should().BeTrue();
        Db.Context.ChangeTracker.Clear();

        ((ModuleSchemaResponse)(await Send(new GetModuleSchemaRequest("tg"))).Data!).TasksEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task MasterModule_ToggleNeedsSystemAdmin_403()
    {
        await NewModule("tg");
        var m = await Detail("tg");
        var app = new App { Key = "master-x", Kind = AppKind.Master, Status = AppStatus.Open, Source = "builtIn", RowVersion = "x" };
        Db.Context.Add(app);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new AppModule { AppId = app.Id, Code = "m", ModuleId = m.Id });
        Db.Context.Add(new UserProgramPrivilege { UserId = "sales01", ProgramCode = "DYN00000", CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        As("sales01");
        (await Toggle(m, false)).IsForbidden.Should().BeTrue("設計器權限不夠，主檔還要系統管理員");
        (await Fresh("tg")).TasksEnabled.Should().BeTrue();
        As("admin");
        (await Toggle(m, false)).IsSuccess.Should().BeTrue();
    }
}
