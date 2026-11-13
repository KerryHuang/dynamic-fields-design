using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>
/// Demo 的進銷存採購單（簽核 a5c）：草稿 → 已確認 由套件宣告成需簽核，9 張採購單一律經送簽確認；
/// 含稅總計 10,000 以上一關（ops01）、100,000 以上兩關（ops01、manager01）、不到門檻送出即確認，另有一張大額停在第二關。
/// </summary>
[Collection(DemoGeneratedCollection.Name)]
public class InventoryPoApprovalScenarioTests(DemoGeneratedFixture fx)
{
    private const string Po = "inv-po";
    private const decimal MediumFrom = 10000, LargeFrom = 100000;

    private sealed record PoRow(DynamicRecordResponse Record, string Status, decimal Total);

    private async Task<List<PoRow>> Pos()
    {
        fx.Generated();
        var status = await fx.Key(Po, "狀態");
        var total = await fx.Key(Po, "含稅總計");
        var labels = (await fx.Module(Po)).Blocks.SelectMany(b => b.Fields).Single(f => f.Key == status).Options
            .ToDictionary(o => o.Value, o => o.Label.Resolve("zh-TW"));
        return [.. (await fx.Records(Po)).Select(r => new PoRow(r, labels[DemoGeneratedFixture.Text(r, status)!],
            r.Values[total]!.Value.GetDecimal()))];
    }

    private async Task<List<(ApprovalInstance Instance, string FlowKey)>> Instances()
    {
        var db = fx.Host.Db.Context;
        var flows = await db.Set<ApprovalFlow>().AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Key);
        return [.. (await db.Set<ApprovalInstance>().AsNoTracking().ToListAsync()).Select(i => (i, i.FlowId is { } id ? flows[id] : ""))];
    }

    private async Task<List<(string Stage, string User, string Status)>> TasksOf(long instanceId)
    {
        var db = fx.Host.Db.Context;
        var runs = await db.Set<ApprovalStageRun>().AsNoTracking().Where(r => r.InstanceId == instanceId).ToDictionaryAsync(r => r.Id);
        return [.. (await db.Set<ApprovalTask>().AsNoTracking().Where(t => t.InstanceId == instanceId).ToListAsync())
            .OrderBy(t => runs[t.StageRunId].Seq).Select(t => (runs[t.StageRunId].StageCode ?? "", t.AssigneeUserId, t.Status))];
    }

    [Fact]
    public async Task InventoryPos_NineRecords_WithEveryOutcome()
    {
        var pos = await Pos();
        pos.Should().HaveCount(9);
        pos.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count())
            .Should().Equal(new Dictionary<string, int> { ["草稿"] = 2, ["已確認"] = 4, ["已結案"] = 3 }, "草稿含一張大額簽核中的單");
        pos.Should().Contain(p => p.Total < MediumFrom, "小額一張");
        pos.Count(p => p.Total >= LargeFrom).Should().Be(2, "大額兩張");
    }

    [Fact]
    public async Task InventoryPos_ConfirmedOnlyThroughSubmit()
    {
        var pos = await Pos();
        var instances = await Instances();
        foreach (var p in pos.Where(p => p.Status is "已確認" or "已結案"))
        {
            var mine = instances.Where(i => i.Instance.RecordId == p.Record.Id).ToList();
            if (p.Total >= MediumFrom)
            {
                var only = mine.Should().ContainSingle($"總額 {p.Total} 達門檻要有一個簽核實例").Subject;
                only.Instance.Status.Should().Be(ApprovalInstanceStatus.Approved);
                only.Instance.InitiatorUserId.Should().Be("buyer01");
                only.FlowKey.Should().Be(p.Total >= LargeFrom ? "inv-po-large" : "inv-po-medium");
            }
            else mine.Should().BeEmpty($"總額 {p.Total} 不到門檻，送出即確認，沒有簽核實例");
        }
    }

    [Fact]
    public async Task LargePo_ApprovedByLevel1ThenLevel2()
    {
        var pos = await Pos();
        var large = pos.Single(p => p.Total >= LargeFrom && p.Status == "已確認");
        var instance = (await Instances()).Single(i => i.Instance.RecordId == large.Record.Id).Instance;
        (await TasksOf(instance.Id)).Should().Equal(
            [("buyer-lead", "ops01", ApprovalTaskStatus.Approved), ("gm", "manager01", ApprovalTaskStatus.Approved)],
            "第 1 關營運部主管、第 2 關總經理室主管，依序同意");
    }

    [Fact]
    public async Task LargePo_PendingAtSecondStage_InManager01Todo()
    {
        var pos = await Pos();
        var pending = pos.Single(p => p.Total >= LargeFrom && p.Status == "草稿");
        var instance = (await Instances()).Single(i => i.Instance.RecordId == pending.Record.Id).Instance;
        instance.Status.Should().Be(ApprovalInstanceStatus.Pending);
        (await TasksOf(instance.Id)).Should().Equal(
            [("buyer-lead", "ops01", ApprovalTaskStatus.Approved), ("gm", "manager01", ApprovalTaskStatus.Open)]);

        var inbox = await fx.As("manager01", new GetApprovalTasksRequest("todo", null, null, null, 1, 50));
        inbox.IsSuccess.Should().BeTrue(inbox.Message);
        ((PagedResponse<ApprovalInboxItem>)inbox.Data!).Items.Should().Contain(i => i.RecordId == pending.Record.Id, "manager01 的簽核中心有這一張待簽");
    }
}
