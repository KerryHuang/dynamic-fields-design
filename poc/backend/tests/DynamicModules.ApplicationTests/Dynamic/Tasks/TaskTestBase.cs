using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>任務測試共用：一個有標題、人員、日期欄位的模組；sales 群組 Read＋Edit、資料範圍 Own；sales-manager 只有 Read、範圍 All。</summary>
public abstract class TaskTestBase : DesignerTestBase
{
    protected sealed record Target(string Key, long ModuleId, long TitleFieldId, long UserFieldId, long DateFieldId, long SalesGroupId);

    protected static readonly DateTime SeedTime = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);

    protected async Task<Target> BuildTarget(string key = "tk")
    {
        await NewModule(key);
        var userField = await MustAdd(key, FieldType.User, label: "人員");
        var dateField = await MustAdd(key, FieldType.Date, label: "日期");
        (await Send(new ActivateModuleRequest(key))).IsSuccess.Should().BeTrue();
        var groups = await Db.Context.Set<PermissionGroup>().AsNoTracking().ToListAsync();
        var sales = groups.Single(g => g.Key == "sales").Id;
        var manager = groups.Single(g => g.Key == "sales-manager").Id;
        var r = await Send(new SaveModulePermissionsRequest(key, new ModulePermissionsDto(
            [
                new GroupPermissionDto(sales, new PrivilegesDto(true, false, true, false, false, false, false), DataScope.Own),
                new GroupPermissionDto(manager, new PrivilegesDto(true, false, false, false, false, false, false), DataScope.All)
            ], [], [], [])));
        r.IsSuccess.Should().BeTrue(r.Message);
        var m = await Detail(key);
        return new Target(key, m.Id, m.TitleFieldId!.Value, userField.Id, dateField.Id, sales);
    }

    /// <summary>以 admin 建一筆記錄，再把擁有者改成 owner（資料範圍依擁有者判斷）。</summary>
    protected async Task<long> NewRecord(Target t, string owner, string title = "案件甲")
    {
        As("admin");
        var r = await Send(new CreateRecordRequest(t.Key,
            new Dictionary<string, System.Text.Json.JsonElement?> { [$"f_{t.TitleFieldId}"] = J($"\"{title}\"") }));
        r.IsCreated.Should().BeTrue(r.Message);
        var id = ((DynamicRecordResponse)r.Data!).Id;
        await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerUserId, owner));
        Db.Context.ChangeTracker.Clear();
        return id;
    }

    /// <summary>直接在 Db 建任務；createdBy 與 assignee 自動成為參與者，mentioned 是額外的被 @ 參與者。</summary>
    protected async Task<TaskItem> SeedTask(string title = "準備資料", string? createdBy = "admin", string? assignee = null,
        Target? target = null, long? recordId = null, DateOnly? due = null, TaskItemStatus status = TaskItemStatus.Todo,
        long? ruleId = null, string[]? mentioned = null)
    {
        var task = new TaskItem
        {
            Title = title, CreatedBy = createdBy, AssigneeUserId = assignee, DueDate = due, Status = status,
            ModuleId = recordId is null ? null : target!.ModuleId, RecordId = recordId,
            RecordTitleSnapshot = recordId is null ? null : "案件甲", RuleId = ruleId, RuleOccurrence = ruleId is null ? null : 1,
            RuleTaskCode = ruleId is null ? null : "t1", CreatedTime = SeedTime
        };
        if (createdBy is not null) task.Participants.Add(new TaskParticipant { UserId = createdBy, Reason = TaskParticipantReason.Creator });
        if (assignee is not null) task.Participants.Add(new TaskParticipant { UserId = assignee, Reason = TaskParticipantReason.Assignee });
        foreach (var u in mentioned ?? []) task.Participants.Add(new TaskParticipant { UserId = u, Reason = TaskParticipantReason.Mentioned });
        Db.Context.Add(task);
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return task;
    }

    protected async Task<TaskItem> Load(long id)
    {
        Db.Context.ChangeTracker.Clear();
        return await Db.Context.Set<TaskItem>().AsNoTracking().Include(t => t.Participants).SingleAsync(t => t.Id == id);
    }
}
