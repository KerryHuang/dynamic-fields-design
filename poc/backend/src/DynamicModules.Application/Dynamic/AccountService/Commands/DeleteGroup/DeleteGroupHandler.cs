using DynamicModules.Application.Common;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.DeleteGroup;

public class DeleteGroupHandler(IAccountRepository accounts, IApprovalFlowRepository approvalFlows, IUnitOfWork uow) : IRequestHandler<DeleteGroupRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteGroupRequest request, CancellationToken ct)
    {
        var group = await accounts.GetGroupTrackedAsync(request.GroupId, ct);
        if (group is null) return ServiceResult.NotFound("群組不存在");

        // 選項範圍移除後該選項可能變成所有人可選，等於放寬權限，所以擋下
        var impact = await accounts.GetGroupImpactAsync(group.Id, ct);
        if (impact.BlockingOptions.Count > 0)
            return ServiceResult.ConflictWith("有欄位選項範圍還指定這個群組，請先到模組權限移除", new { impact.BlockingOptions });

        // 簽核流程的目前版本或進行中實例用的版本引用這個群組：刪了關卡就找不到人（簽核設計 §8.6）
        var flowKeys = await approvalFlows.ListFlowKeysReferencingGroupAsync(group.Id, ct);
        if (flowKeys.Count > 0) return ServiceResult.Conflict($"群組被簽核流程使用：{string.Join("、", flowKeys)}");

        await accounts.DeleteGroupAsync(group, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
