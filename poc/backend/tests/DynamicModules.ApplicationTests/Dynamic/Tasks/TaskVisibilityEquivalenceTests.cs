using System.Text.Json;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>批次可見條件（看板設計 §4.4）與舊的逐筆判斷必須同結論：Visible(v) 撈出的 Id＝EvaluateMany 看得到的 Id。</summary>
public class TaskVisibilityEquivalenceTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private async Task SetPolicy(PolicyDefinition def, string value)
    {
        Db.Context.Add(new SystemSetting { Key = def.SettingKey, Value = value });
        await Db.Context.SaveChangesAsync();
    }

    /// <summary>tk2：只有業務群組 Own，業務主管（manager01）沒有任何權限。</summary>
    private async Task<Target> BuildRestrictedTarget(string key)
    {
        var t = await BuildTarget(key);
        var r = await Send(new SaveModulePermissionsRequest(key, new ModulePermissionsDto(
            [new GroupPermissionDto(t.SalesGroupId, new PrivilegesDto(true, false, true, false, false, false, false), DataScope.Own)],
            [], [], [])));
        r.IsSuccess.Should().BeTrue(r.Message);
        return t;
    }

    private async Task<HashSet<long>> ViaPredicate(string user)
    {
        As(user);
        using var scope = NewScope();
        var v = await scope.ServiceProvider.GetRequiredService<TaskVisibilityService>().ForCurrentUserAsync(default);
        var ids = await scope.ServiceProvider.GetRequiredService<ITaskRepository>().ListVisibleIdsAsync(v, default);
        As("admin");
        return ids.ToHashSet();
    }

    private async Task<HashSet<long>> ViaEvaluate(string user)
    {
        Db.Context.ChangeTracker.Clear();
        var all = await Db.Context.Set<TaskItem>().AsNoTracking().Include(t => t.Participants).OrderBy(t => t.Id).ToListAsync();
        As(user);
        using var scope = NewScope();
        var perms = await scope.ServiceProvider.GetRequiredService<TaskAccess>().EvaluateManyAsync(all, default);
        As("admin");
        return all.Where((_, i) => perms[i].CanSee).Select(t => t.Id).ToHashSet();
    }

    [Fact]
    public async Task Matrix_PredicateMatchesEvaluateMany()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        Db.Context.Add(new AppUser { UserId = "admin2", DisplayName = "停用的管理員", IsSystemAdmin = true, IsActive = false });
        await Db.Context.SaveChangesAsync();
        var t = await BuildTarget("tk");
        var frozen = await BuildTarget("tk3");
        var restricted = await BuildRestrictedTarget("tk2");

        var rec1 = await NewRecord(t, "sales01");
        var rec2 = await NewRecord(t, "sales01", "已刪除記錄");
        await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Where(r => r.Id == rec2).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeleteFlag, true));
        var rec3 = await NewRecord(t, "sales02");
        var recFrozen = await NewRecord(frozen, "sales01");
        await Db.Context.Set<Module>().Where(m => m.Key == "tk3").ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, ModuleStatus.Closed));
        var recRestricted = await NewRecord(restricted, "sales01");
        // 明細模組：它的記錄不該讓任何人因「記錄可讀」看到任務
        await NewModule("visit");
        var link = await MustAdd("visit", FieldType.Reference, RefTo("tk", ReferenceRelation.Detail), "所屬案件");
        link.Should().NotBeNull();
        (await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("visit"))).IsSuccess.Should().BeTrue();
        var visitModule = await Detail("visit");
        Db.Context.Add(new DynamicModules.Domain.Entities.Dynamic.Record
        {
            ModuleId = visitModule.Id, OwnerUserId = "sales01", Title = "明細列", RowVersion = "v", CreatedUserId = "admin", CreatedTime = SeedTime
        });
        await Db.Context.SaveChangesAsync();
        var detailRecId = await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Where(r => r.ModuleId == visitModule.Id).Select(r => r.Id).SingleAsync();

        await SeedTask("個人", createdBy: "sales01", assignee: "sales01");
        await SeedTask("記錄任務", createdBy: "floor01", target: t, recordId: rec1);
        await SeedTask("已刪除記錄上的任務", createdBy: "floor01", target: t, recordId: rec2);
        await SeedTask("別人的記錄", createdBy: "floor01", target: t, recordId: rec3);
        await SeedTask("凍結模組", createdBy: "floor01", target: frozen, recordId: recFrozen);
        await SeedTask("受限模組", createdBy: "floor01", assignee: "sales01", target: restricted, recordId: recRestricted);
        await SeedTask("明細記錄上的任務", createdBy: "floor01", moduleId: visitModule.Id, recordId: detailRecId);
        await SeedTask("規則任務", createdBy: null, assignee: "keeper01", target: t, recordId: rec1, ruleId: 7);
        await SeedTask("未指派", createdBy: "keeper01");
        await SeedTask("只被提到", createdBy: "sales02", mentioned: ["buyer01"]);
        await SeedTask("記錄不存在", createdBy: "floor01", moduleId: t.ModuleId, recordId: 99999);

        foreach (var who in new[] { "admin", "admin2", "floor01", "sales01", "sales02", "manager01", "ops01", "buyer01", "keeper01", "nobody" })
            (await ViaPredicate(who)).Should().BeEquivalentTo(await ViaEvaluate(who), $"身分 {who}");

        (await ViaPredicate("admin")).Count.Should().Be(11);
        (await ViaPredicate("sales01")).Count.Should().BeGreaterThan(3).And.BeLessThan(11);
        (await ViaPredicate("manager01")).Should().Contain(await ViaPredicate("sales01"));

        await SetPolicy(PolicyCatalog.TaskSupervisorScope, "false");
        foreach (var who in new[] { "manager01", "ops01" })
            (await ViaPredicate(who)).Should().BeEquivalentTo(await ViaEvaluate(who), $"A32 關閉，身分 {who}");
    }

    [Fact]
    public async Task Evaluate_QueryCount_DoesNotGrowWithTaskCount()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var targets = new[] { await BuildTarget("tk"), await BuildTarget("tk2"), await BuildTarget("tk3") };
        var records = new List<(Target T, long Id)>();
        for (var i = 0; i < 10; i++)   // 交錯排列：只有 10 筆任務時也會碰到三個模組
            foreach (var target in targets) records.Add((target, await NewRecord(target, "sales01", $"案件{i}")));

        async Task<int> CountFor(int taskCount)
        {
            Db.Context.Set<TaskItem>().RemoveRange(Db.Context.Set<TaskItem>());
            await Db.Context.SaveChangesAsync();
            for (var i = 0; i < taskCount; i++)
            {
                var (target, id) = records[i % records.Count];
                await SeedTask($"任務{i}", createdBy: "floor01", assignee: i % 2 == 0 ? "sales02" : null, target: target, recordId: id);
            }
            Db.Context.ChangeTracker.Clear();
            var all = await Db.Context.Set<TaskItem>().AsNoTracking().Include(t => t.Participants).ToListAsync();
            As("manager01");
            using var scope = NewScope();
            var access = scope.ServiceProvider.GetRequiredService<TaskAccess>();
            var before = Db.Counter.Count;
            var perms = await access.EvaluateManyAsync(all, default);
            var used = Db.Counter.Count - before;
            As("admin");
            perms.Should().OnlyContain(p => p.CanSee);
            return used;
        }

        var small = await CountFor(10);
        var large = await CountFor(300);

        large.Should().Be(small, "查詢次數不隨任務筆數成長");
    }

    [Fact]
    public async Task Supervisor_RecordOutOfScope_TaskVisible_RecordHidden()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var restricted = await BuildRestrictedTarget("tk2");
        var rec = await NewRecord(restricted, "sales01", "機密案件甲");
        var task = await SeedTask("準備資料", createdBy: "sales01", assignee: "sales01", target: restricted, recordId: rec);

        As("manager01");
        var r = await Send(new GetTaskRequest(task.Id));

        r.IsSuccess.Should().BeTrue(r.Message);
        var detail = (TaskDetailResponse)r.Data!;
        detail.Record.Should().Be(new TaskRecordRef(false, null, null, null, false));
        var json = JsonSerializer.Serialize(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("tk2").And.NotContain("機密案件甲").And.NotContain($"\"recordId\":{rec}");
        detail.CanEdit.Should().BeFalse();
        detail.CanReassign.Should().BeTrue();
        detail.Supervised.Should().BeTrue();
    }

    [Fact]
    public async Task Supervisor_PolicyOff_NotVisible()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var task = await SeedTask("個人", createdBy: "sales01", assignee: "sales01");
        await SetPolicy(PolicyCatalog.TaskSupervisorScope, "false");

        As("manager01");
        var r = await Send(new GetTaskRequest(task.Id));

        r.IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Supervisor_NonRecursive_OnlyDirect()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var sales = await SeedTask("業務的", createdBy: "sales01", assignee: "sales01");
        var keeper = await SeedTask("倉管的", createdBy: "keeper01", assignee: "keeper01");
        await SetPolicy(PolicyCatalog.TaskSupervisorScopeRecursive, "false");

        As("manager01");
        (await Send(new GetTaskRequest(sales.Id))).IsSuccess.Should().BeTrue();
        (await Send(new GetTaskRequest(keeper.Id))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Supervisor_IsNotParticipant_NoCommentNotification()
    {
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
        var task = await SeedTask("個人", createdBy: "sales01", assignee: "sales01");

        As("manager01");
        (await Send(new GetTaskRequest(task.Id))).IsSuccess.Should().BeTrue();
        var comment = await Send(new AddTaskCommentRequest(task.Id, "請多留意進度", []));
        comment.IsCreated.Should().BeTrue(comment.Message);

        var after = await Load(task.Id);
        after.Participants.Select(p => p.UserId).Should().NotContain("manager01");
        (await Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == "manager01").CountAsync()).Should().Be(0);
    }
}
