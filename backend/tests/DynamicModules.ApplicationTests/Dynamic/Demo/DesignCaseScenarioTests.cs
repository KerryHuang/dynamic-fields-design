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

/// <summary>CRM 設計案的三筆 Demo 資料（Today = 2026-11-05）：結案、第二輪客戶確認中、第一輪設計中。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class DesignCaseScenarioTests(DemoGeneratedFixture fx)
{
    private const string Case = "crm-design-case";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    private async Task<DynamicRecordResponse> CaseNamed(string name)
    {
        fx.Generated();
        var nameKey = await fx.Key(Case, "案名");
        return (await fx.Records(Case)).Single(r => DemoGeneratedFixture.Text(r, nameKey) == name);
    }

    private async Task<string> StatusLabel(DynamicRecordResponse r)
    {
        var statusKey = await fx.Key(Case, "狀態");
        var value = DemoGeneratedFixture.Text(r, statusKey);
        foreach (var label in new[] { "需求確認", "設計中", "客戶確認中", "待主管確認", "結案", "取消" })
            if (await fx.Opt(Case, "狀態", label) == value) return label;
        return value ?? "";
    }

    private Task<List<TaskItem>> TasksOf(long recordId) =>
        fx.Host.Db.Context.Set<TaskItem>().AsNoTracking().Where(t => t.RecordId == recordId).OrderBy(t => t.Id).ToListAsync();

    private Task<List<Notification>> NotificationsOf(string userId) =>
        fx.Host.Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == userId).ToListAsync();

    private async Task<List<(string Id, string Name)>> Drawings(DynamicRecordResponse r)
    {
        var key = await fx.Key(Case, "設計圖檔");
        return r.Values.GetValueOrDefault(key) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(e => (e.GetProperty("id").GetString()!, e.GetProperty("name").GetString()!)).ToList() : [];
    }

    private async Task<long> DrawingsFieldId() =>
        (await fx.Module(Case)).Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == "設計圖檔").Id;

    [Fact]
    public async Task ClosedCase_StatusHistoryAndApproval()
    {
        var rec = await CaseNamed("感測器外殼改版");
        (await StatusLabel(rec)).Should().Be("結案");

        var statusKey = await fx.Key(Case, "狀態");
        var allHistory = ((PagedResponse<RecordHistoryResponse>)(await fx.As("admin", new GetRecordHistoryRequest(rec.Id, 1, 200, "zh-TW"))).Data!).Items;
        var history = allHistory
            .Where(h => h.FieldKey == statusKey && h.Detail is null).OrderBy(h => h.ChangedTime).ThenBy(h => h.Id).ToList();
        history.Select(h => h.NewText).Should().Equal("設計中", "客戶確認中", "設計中", "客戶確認中", "待主管確認", "結案");

        var instance = await fx.Host.Db.Context.Set<ApprovalInstance>().AsNoTracking().SingleAsync(i => i.RecordId == rec.Id);
        instance.Status.Should().Be("approved");
        instance.InitiatorUserId.Should().Be("sales01");
        var approvers = await fx.Host.Db.Context.Set<ApprovalTask>().AsNoTracking().Where(t => t.InstanceId == instance.Id).Select(t => t.AssigneeUserId).Distinct().ToListAsync();
        approvers.Should().Equal("manager01");

        // 時間軸一步接一步：每次「完成繪製」都在接著改客戶確認中之前；「客戶意見 v1」不晚於退回設計中
        var tasks = await TasksOf(rec.Id);
        var draws = tasks.Where(t => t.RuleTaskCode == "draw").OrderBy(t => t.Id).ToList();
        var toReview = history.Where(h => h.NewText == "客戶確認中").ToList();
        var toDesigning = history.Where(h => h.NewText == "設計中").ToList();
        draws.Should().HaveCount(2);
        for (var i = 0; i < 2; i++)
            draws[i].CompletedTime.Should().BeOnOrBefore(toReview[i].ChangedTime, $"第 {i + 1} 輪繪製完成要早於改客戶確認中");
        var confirms = tasks.Where(t => t.RuleTaskCode == "confirm").OrderBy(t => t.Id).ToList();
        confirms[0].CompletedTime.Should().BeOnOrBefore(toDesigning[1].ChangedTime, "第一輪確認完成早於退回設計中");
        var feedbackV1 = allHistory.Single(h => h.Detail is not null && h.NewText == "指示燈窗改到右上角，側面加散熱槽");
        feedbackV1.ChangedTime.Should().BeBefore(toDesigning[1].ChangedTime, "客戶意見 v1 在退回設計中之前填");
        history.Select(h => h.ChangedTime).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ClosedCase_TasksTwoRounds()
    {
        var rec = await CaseNamed("感測器外殼改版");
        var tasks = await TasksOf(rec.Id);

        tasks.GroupBy(t => t.RuleTaskCode).ToDictionary(g => g.Key!, g => g.Count()).Should().BeEquivalentTo(
            new Dictionary<string, int> { ["intake"] = 1, ["draw"] = 2, ["confirm"] = 2 });
        tasks.Should().OnlyContain(t => t.Status == TaskItemStatus.Done, "結案的設計案不留未完成任務");
    }

    [Fact]
    public async Task ClosedCase_FilesDownloadable()
    {
        var rec = await CaseNamed("感測器外殼改版");
        var files = await Drawings(rec);
        files.Select(f => f.Name).Should().Equal("感測器外殼-v1.png", "感測器外殼-v2.png");

        var download = await fx.As("sales01", new DownloadAttachmentRequest(rec.Id, await DrawingsFieldId(), files[1].Id));
        download.IsSuccess.Should().BeTrue(download.Message);
        using var content = ((AttachmentDownload)download.Data!).Content;
        var head = new byte[4];
        content.ReadExactly(head);
        head.Should().Equal(PngSignature);

        var details = (await fx.As("admin", new GetRecordRequest(rec.Id))).Data as DynamicRecordResponse;
        details!.Details!.Values.Single().Count.Should().Be(2);
    }

    [Fact]
    public async Task ClosedCase_CommentAttachmentsAndMention()
    {
        var rec = await CaseNamed("感測器外殼改版");
        var confirm = (await TasksOf(rec.Id)).Where(t => t.RuleTaskCode == "confirm").OrderBy(t => t.Id).First();
        var comments = await fx.Host.Db.Context.Set<TaskComment>().AsNoTracking().Include(c => c.Attachments)
            .Where(c => c.TaskId == confirm.Id && c.Kind == TaskCommentKind.Comment).ToListAsync();

        var comment = comments.Should().ContainSingle().Subject;
        comment.AuthorUserId.Should().Be("sales01");
        comment.Body.Should().Be("@{designer01} 客戶標註如附件");
        comment.Attachments.Select(a => a.FileName).Should().BeEquivalentTo("客戶標註-v1.png", "修改說明.txt");
        (await NotificationsOf("designer01")).Should().Contain(n => n.Kind == NotificationKinds.TaskMentioned);
        foreach (var a in comment.Attachments)
            (await fx.As("designer01", new DownloadTaskAttachmentRequest(confirm.Id, comment.Id, a.Id))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ClosedCase_Notifications()
    {
        await CaseNamed("感測器外殼改版");

        (await NotificationsOf("manager01")).Should().Contain(n => n.Kind == NotificationKinds.ApprovalPending);
        (await NotificationsOf("sales01")).Should().Contain(n => n.Kind == NotificationKinds.ApprovalApproved);
    }

    [Fact]
    public async Task PendingCase_ReadyForLiveSubmit()
    {
        var rec = await CaseNamed("控制箱面板改版");
        (await StatusLabel(rec)).Should().Be("客戶確認中");

        var confirms = (await TasksOf(rec.Id)).Where(t => t.RuleTaskCode == "confirm").OrderBy(t => t.Id).ToList();
        confirms.Should().HaveCount(2);
        confirms[0].Status.Should().Be(TaskItemStatus.Done);
        confirms[1].Status.Should().NotBe(TaskItemStatus.Done);
        confirms[1].AssigneeUserId.Should().Be("sales01");
        fx.Host.Db.Context.Set<ApprovalInstance>().AsNoTracking().Any(i => i.RecordId == rec.Id).Should().BeFalse();

        var review = long.Parse((await fx.Opt(Case, "狀態", "待主管確認"))[4..]);
        var preview = await fx.As("sales01", new PreviewRecordApprovalRequest(rec.Id, review));
        preview.IsSuccess.Should().BeTrue(preview.Message);
        var stages = ((ApprovalPreviewResponse)preview.Data!).Stages;
        stages.Should().ContainSingle("只有一關");
        stages[0].Approvers.Select(a => a.UserId).Should().Equal("manager01");
    }

    [Fact]
    public async Task DesigningCase_DrawOpen_NoFiles()
    {
        var rec = await CaseNamed("展示架結構設計");
        (await StatusLabel(rec)).Should().Be("設計中");

        var draw = (await TasksOf(rec.Id)).Single(t => t.RuleTaskCode == "draw");
        draw.Status.Should().NotBe(TaskItemStatus.Done);
        draw.AssigneeUserId.Should().Be("designer01");
        (await Drawings(rec)).Should().BeEmpty();
    }
}
