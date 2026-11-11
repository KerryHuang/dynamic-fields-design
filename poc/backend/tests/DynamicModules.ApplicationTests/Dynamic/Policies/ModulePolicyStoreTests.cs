using DynamicModules.Application.Dynamic.DeleteService.Commands.DeleteModule;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

public class ModulePolicyStoreTests : DesignerTestBase
{
    [Fact]
    public async Task SystemSettings_ListByPrefix_AndRemove()
    {
        var settings = Svc<ISystemSettingRepository>();
        await settings.SetAsync("policy.a", "1", default);
        await settings.SetAsync("policy.b", "2", default);
        await settings.SetAsync("theme.defaultMode", "dark", default);
        await settings.SetAsync("POLICY.c", "3", default);
        await Db.Context.SaveChangesAsync();

        var listed = await settings.ListByPrefixAsync("policy.", default);
        listed.Should().BeEquivalentTo(new Dictionary<string, string> { ["policy.a"] = "1", ["policy.b"] = "2" });

        await settings.RemoveAsync("policy.a", default);
        await Db.Context.SaveChangesAsync();
        (await settings.ListByPrefixAsync("policy.", default)).Keys.Should().BeEquivalentTo(["policy.b"]);

        await settings.Invoking(s => s.RemoveAsync("policy.none", default)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteModule_RemovesItsOverrides()
    {
        await NewModule("pm");
        var module = await Db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == "pm");
        Svc<IModulePolicyRepository>().Add(new ModulePolicy
        {
            ModuleId = module.Id, Code = "task.maxRulesPerModule", Value = "3", ModifiedUserId = "admin",
        });
        await Db.Context.SaveChangesAsync();
        (await Svc<IModulePolicyRepository>().ListByModuleAsync(module.Id, default)).Should().HaveCount(1);
        Db.Context.ChangeTracker.Clear();

        var r = await Send(new DeleteModuleRequest("pm", 0));

        r.IsSuccess.Should().BeTrue(r.Message);
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<ModulePolicy>().CountAsync()).Should().Be(0);
    }
}
