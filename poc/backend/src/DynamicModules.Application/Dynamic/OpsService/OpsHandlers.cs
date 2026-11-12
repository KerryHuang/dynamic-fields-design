using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService;

public class GetOpsInfoHandler(IOpsEnvironment env, OpsInstance instance, IDatabaseInfo database, ISystemSettingRepository settings)
    : IRequestHandler<GetOpsInfoRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetOpsInfoRequest request, CancellationToken ct) =>
        ServiceResult.Success(new OpsInfoResponse(
            env.Version, env.Commit, env.BuiltAt, env.Edition, instance.StartedAtUtc, instance.InstanceId,
            database.Provider, await database.LatestMigrationAsync(ct),
            await settings.GetValueAsync(SystemSetting.IndexBackfillVersion, ct),
            Jobs: []));   // 排程工作狀態由 o1b 填
}
