using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>Demo 情境共用的簽核步驟：送簽、同意（設計案與採購單都用）。以 <see cref="DemoPlan"/> 的 Run 排進時間軸，由排程的使用者身分執行。</summary>
public static class DemoApprovalSteps
{
    /// <summary>送出簽核到 toOptionId（狀態機的目標選項 Id）；入口是 direct 且沒有流程符合時，送出就直接轉移。</summary>
    public static async Task<ServiceResult> Submit(DemoContext ctx, DemoRecord rec, long toOptionId, string? comment, CancellationToken ct) =>
        await ctx.Sender.Send(new SubmitApprovalRequest(rec.Id, toOptionId, rec.RowVersion, null, comment), ct);

    /// <summary>同意目前實例中排程使用者可處理的任務。</summary>
    public static async Task<ServiceResult> Approve(DemoContext ctx, DemoRecord rec, string comment, CancellationToken ct)
    {
        var current = await ctx.Sender.Send(new GetRecordApprovalRequest(rec.Id), ct);
        if (!current.IsSuccess) return current;
        var instance = ((RecordApprovalResponse)current.Data!).Current;
        var task = instance?.Stages.SelectMany(s => s.Tasks).FirstOrDefault(t => t.CanAct);
        if (instance is null || task is null) return ServiceResult.Conflict("沒有可同意的簽核任務");
        return await ctx.Sender.Send(new ApproveTaskRequest(task.Id, instance.RowVersion, comment, null), ct);
    }

    /// <summary>目前實例中排程使用者可處理的任務與實例 rowVersion；沒有就回 null。</summary>
    private static async Task<(ServiceResult? Failure, long TaskId, long InstanceId, string RowVersion)> ActionableAsync(DemoContext ctx, DemoRecord rec, CancellationToken ct)
    {
        var current = await ctx.Sender.Send(new GetRecordApprovalRequest(rec.Id), ct);
        if (!current.IsSuccess) return (current, 0, 0, "");
        var instance = ((RecordApprovalResponse)current.Data!).Current;
        var task = instance?.Stages.SelectMany(s => s.Tasks).FirstOrDefault(t => t.CanAct);
        if (instance is null || task is null) return (ServiceResult.Conflict("沒有可處理的簽核任務"), 0, 0, "");
        return (null, task.Id, instance.Id, instance.RowVersion);
    }

    /// <summary>把排程使用者的待簽任務轉交給 userId。</summary>
    public static async Task<ServiceResult> Transfer(DemoContext ctx, DemoRecord rec, string userId, string comment, CancellationToken ct)
    {
        var (failure, taskId, _, rowVersion) = await ActionableAsync(ctx, rec, ct);
        return failure ?? await ctx.Sender.Send(new TransferTaskRequest(taskId, rowVersion, userId, comment), ct);
    }

    /// <summary>同意並加簽給 userIds（mode：all 會簽、any 或簽）。</summary>
    public static async Task<ServiceResult> AddSign(DemoContext ctx, DemoRecord rec, string[] userIds, string mode, string comment, CancellationToken ct)
    {
        var (failure, taskId, _, rowVersion) = await ActionableAsync(ctx, rec, ct);
        return failure ?? await ctx.Sender.Send(new AddSignTaskRequest(taskId, rowVersion, comment, null, [.. userIds], mode), ct);
    }

    /// <summary>簽核管理員終止進行中的實例。</summary>
    public static async Task<ServiceResult> Cancel(DemoContext ctx, DemoRecord rec, string comment, CancellationToken ct)
    {
        var current = await ctx.Sender.Send(new GetRecordApprovalRequest(rec.Id), ct);
        if (!current.IsSuccess) return current;
        var instance = ((RecordApprovalResponse)current.Data!).Current;
        if (instance is null) return ServiceResult.Conflict("沒有進行中的簽核");
        return await ctx.Sender.Send(new CancelApprovalRequest(instance.Id, instance.RowVersion, comment), ct);
    }

    /// <summary>排程使用者設定自己的代理人與起訖日。</summary>
    public static async Task<ServiceResult> SetDelegate(DemoContext ctx, string delegateUserId, DateOnly start, DateOnly end, CancellationToken ct) =>
        await ctx.Sender.Send(new SaveMyApprovalDelegateRequest(delegateUserId, start, end), ct);
}
