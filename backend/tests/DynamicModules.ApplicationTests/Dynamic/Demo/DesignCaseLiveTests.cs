using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Files;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>設計案寫入型情境（各自產生新庫、會寫入）共用的輔助。每個測試各自一個類別、各產生一次，才能和共用產生結果的類別平行。</summary>
internal static class DesignCasePlaybook
{
    internal const string Case = "crm-design-case";

    internal static async Task<T> As<T>(DemoTestHost host, string user, MediatR.IRequest<T> request)
    {
        // 整個測試共用同一個 scope：每次請求前把「每個請求一份」的狀態歸零（同 DemoStructure 的 BeginStep）
        var sp = host.Services;
        DemoStructure.ResetPerRequestState(sp.GetRequiredService<IWriteJournal>(),
            sp.GetRequiredService<DynamicModules.Application.Dynamic.EventService.EffectBudget>(),
            sp.GetRequiredService<DynamicModules.Application.Dynamic.ApprovalService.ApprovalWriteContext>(),
            sp.GetRequiredService<DynamicModules.Application.Dynamic.ApprovalService.ApprovalLockService>(),
            sp.GetRequiredService<DynamicModules.Application.Dynamic.AttachmentService.AttachmentUploadTracker>(),
            sp.GetRequiredService<DynamicModules.Application.Dynamic.TaskService.TaskVisibilityService>(),
            sp.GetRequiredService<DynamicModules.Application.Dynamic.ReportService.TaskReportSource>());
        using var _ = ActingScope.Begin(user);
        return await host.Send(request);
    }

    internal static async Task<DynamicRecordResponse> Find(DemoTestHost host, string name)
    {
        var detail = (DynamicModules.Application.Dynamic.ModuleService.Data.ModuleDetailResponse)(await As(host, "admin", new DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule.GetModuleRequest(Case))).Data!;
        var nameKey = detail.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "案名").Key;
        var page = await As(host, "admin", new DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords.GetPagedRecordsRequest(Case, 1, 200, null, [], [], null));
        return ((PagedResponse<DynamicRecordResponse>)page.Data!).Items.Single(r => DemoGeneratedFixture.Text(r, nameKey) == name);
    }

    internal static async Task<(DemoTestHost Host, DynamicRecordResponse Rec, long Review)> PlaybookStart()
    {
        var host = await DemoTestHost.CreateAsync();
        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));
        r.IsSuccess.Should().BeTrue(r.Message);
        var rec = await Find(host, "控制箱面板改版");
        var detail = (DynamicModules.Application.Dynamic.ModuleService.Data.ModuleDetailResponse)(await As(host, "admin", new DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule.GetModuleRequest(Case))).Data!;
        var status = detail.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "狀態");
        var review = long.Parse(status.Options.Single(o => o.Label.Resolve("zh-TW") == "待主管確認").Value[4..]);
        return (host, rec, review);
    }

    internal static async Task<DynamicRecordResponse> SalesConfirmAndSubmit(DemoTestHost host, DynamicRecordResponse rec, long review)
    {
        var detail = (DynamicModules.Application.Dynamic.ModuleService.Data.ModuleDetailResponse)(await As(host, "admin", new DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule.GetModuleRequest(Case))).Data!;
        rec = (DynamicRecordResponse)(await As(host, "sales01", new GetRecordRequest(rec.Id))).Data!;
        var drawingKey = rec.Details!.Keys.Single();
        var row = rec.Details[drawingKey].Single(x => x.Values.Values.Any(v => v is { ValueKind: JsonValueKind.Number } n && n.GetInt32() == 2));
        var feedbackKey = (await As(host, "admin", new DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule.GetModuleRequest(drawingKey))).Data is DynamicModules.Application.Dynamic.ModuleService.Data.ModuleDetailResponse d
            ? d.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "客戶意見").Key : throw new InvalidOperationException();
        var edit = await As(host, "sales01", new UpdateRecordRequest(rec.Id, [], rec.RowVersion, new()
        {
            [drawingKey] = [new DetailRowInput(row.Id, new() { [feedbackKey] = JsonSerializer.SerializeToElement("客戶確認此版") }, row.RowVersion)]
        }));
        edit.IsSuccess.Should().BeTrue(edit.Message + string.Join(";", edit.Errors.Select(e => string.Join(",", e.Value))));
        var tasks = (RecordTasksResponse)(await As(host, "sales01", new GetRecordTasksRequest(rec.Id))).Data!;
        var open = tasks.Items.Single(t => t.Title == "將設計圖交客戶確認" && t.Status != TaskItemStatus.Done);
        (await As(host, "sales01", new UpdateTaskRequest(open.Id, JsonSerializer.SerializeToElement(new { status = "done" })))).IsSuccess.Should().BeTrue();
        var fresh = (DynamicRecordResponse)(await As(host, "sales01", new GetRecordRequest(rec.Id))).Data!;
        var submit = await As(host, "sales01", new SubmitApprovalRequest(rec.Id, review, fresh.RowVersion, null, "客戶已確認第二版"));
        submit.IsSuccess.Should().BeTrue(submit.Message + string.Join(";", submit.Errors.Select(e => string.Join(",", e.Value))));
        return fresh;
    }

    internal static async Task<ApprovalInboxItem> ManagerTodo(DemoTestHost host)
    {
        var inbox = await As(host, "manager01", new GetApprovalTasksRequest("todo", null, null, null, 1, 50));
        inbox.IsSuccess.Should().BeTrue(inbox.Message);
        return ((PagedResponse<ApprovalInboxItem>)inbox.Data!).Items.Single(i => i.TitleSnapshot.Contains("控制箱面板改版"));
    }

    internal static async Task<string> Status(DemoTestHost host, long recordId)
    {
        var rec = (DynamicRecordResponse)(await As(host, "admin", new GetRecordRequest(recordId))).Data!;
        var detail = (DynamicModules.Application.Dynamic.ModuleService.Data.ModuleDetailResponse)(await As(host, "admin", new DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule.GetModuleRequest(Case))).Data!;
        var f = detail.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "狀態");
        return f.Options.Single(o => o.Value == DemoGeneratedFixture.Text(rec, f.Key)).Label.Resolve("zh-TW");
    }
}

/// <summary>設計圖上傳停用時，Demo 產生仍會寫入設計圖檔（自己一個類別、一次產生）。</summary>
public class DesignCaseUploadDisabledTests
{
    [Fact]
    public async Task Generate_UploadDisabled_StillWritesDesignFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dm-design-off-{Guid.NewGuid():N}");
        try
        {
            var host = await DemoTestHost.CreateAsync(configure: s =>
            {
                s.AddSingleton(new AttachmentSettings(root, false, "停用"));
                s.AddSingleton<IAttachmentStore>(new LocalAttachmentStore(root));
            });
            await using var _ = host;
            var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));
            r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => string.Join(",", e.Value))));

            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Should().HaveCount(8, "前兩案各 v1、v2、標註圖、修改說明，第三案沒有檔");
            ServerUploadScopeProbe.Active.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static class ServerUploadScopeProbe
    {
        public static bool Active => DynamicModules.Application.Common.Uploads.ServerUploadScope.Active;
    }
}

/// <summary>設計案實跑：業務確認、送簽、主管核准、結案（自己一個類別、一次產生）。</summary>
public class DesignCaseLiveApproveTests
{
    private const string Case = DesignCasePlaybook.Case;

    [Fact]
    public async Task LivePlaybook_SubmitApproveClose()
    {
        var (host, rec, review) = await DesignCasePlaybook.PlaybookStart();
        await using var _ = host;
        await DesignCasePlaybook.SalesConfirmAndSubmit(host, rec, review);

        var todo = await DesignCasePlaybook.ManagerTodo(host);
        var current = (RecordApprovalResponse)(await DesignCasePlaybook.As(host, "manager01", new GetRecordApprovalRequest(rec.Id))).Data!;
        var task = current.Current!.Stages.SelectMany(s => s.Tasks).Single(t => t.CanAct);
        todo.RecordId.Should().Be(rec.Id);
        (await DesignCasePlaybook.As(host, "manager01", new ApproveTaskRequest(task.Id, current.Current.RowVersion, "同意", null))).IsSuccess.Should().BeTrue();
        (await DesignCasePlaybook.Status(host, rec.Id)).Should().Be("待主管確認");

        var fresh = (DynamicRecordResponse)(await DesignCasePlaybook.As(host, "manager01", new GetRecordRequest(rec.Id))).Data!;
        var detail = (DynamicModules.Application.Dynamic.ModuleService.Data.ModuleDetailResponse)(await DesignCasePlaybook.As(host, "admin", new DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule.GetModuleRequest(Case))).Data!;
        var f = detail.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "狀態");
        var closed = await DesignCasePlaybook.As(host, "manager01", new UpdateRecordRequest(rec.Id,
            new() { [f.Key] = JsonSerializer.SerializeToElement(f.Options.Single(o => o.Label.Resolve("zh-TW") == "結案").Value) }, fresh.RowVersion));
        closed.IsSuccess.Should().BeTrue(closed.Message);
        (await DesignCasePlaybook.Status(host, rec.Id)).Should().Be("結案");
        host.Db.Context.Set<Notification>().AsNoTracking().Any(n => n.UserId == "sales01" && n.Kind == NotificationKinds.ApprovalApproved).Should().BeTrue();
    }
}

/// <summary>設計案實跑：主管退回（自己一個類別、一次產生）。</summary>
public class DesignCaseLiveRejectTests
{
    private const string Case = DesignCasePlaybook.Case;

    [Fact]
    public async Task LivePlaybook_RejectBranch()
    {
        var (host, rec, review) = await DesignCasePlaybook.PlaybookStart();
        await using var _ = host;
        await DesignCasePlaybook.SalesConfirmAndSubmit(host, rec, review);

        var current = (RecordApprovalResponse)(await DesignCasePlaybook.As(host, "manager01", new GetRecordApprovalRequest(rec.Id))).Data!;
        var task = current.Current!.Stages.SelectMany(s => s.Tasks).Single(t => t.CanAct);
        var rejected = await DesignCasePlaybook.As(host, "manager01", new RejectTaskRequest(task.Id, current.Current.RowVersion, "按鍵間距再確認", "initiator"));
        rejected.IsSuccess.Should().BeTrue(rejected.Message);

        (await DesignCasePlaybook.Status(host, rec.Id)).Should().Be("設計中");
        var draws = host.Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == rec.Id && t.RuleTaskCode == "draw").OrderBy(t => t.Id).ToList();
        draws.Should().HaveCount(3);
        draws[2].AssigneeUserId.Should().Be("designer01");
        draws[2].Status.Should().NotBe(TaskItemStatus.Done);
        host.Db.Context.Set<Notification>().AsNoTracking().Any(n => n.UserId == "sales01" && n.Kind == NotificationKinds.ApprovalRejected).Should().BeTrue();
    }
}
