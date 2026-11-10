using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ApprovalFlowRepository(DynamicDbContext db) : IApprovalFlowRepository
{
    public async Task<List<FlowState>> ListCurrentByModuleAsync(long moduleId, CancellationToken ct)
    {
        var rows = await (
            from f in db.Set<ApprovalFlow>().AsNoTracking()
            join v in db.Set<ApprovalFlowVersion>().AsNoTracking() on f.CurrentVersionId equals v.Id
            where f.ModuleId == moduleId
            orderby f.ToOptionId, f.Priority
            select new { f, v.Definition }).ToListAsync(ct);
        return rows.Select(x => new FlowState(x.f, x.Definition)).ToList();
    }

    public async Task<List<ApprovalDefinition>> ListAllVersionDefinitionsAsync(long moduleId, CancellationToken ct,
        IReadOnlyCollection<long>? toOptionIds = null)
    {
        var flows = db.Set<ApprovalFlow>().AsNoTracking().Where(f => f.ModuleId == moduleId);
        if (toOptionIds is not null) flows = flows.Where(f => toOptionIds.Contains(f.ToOptionId));
        return await db.Set<ApprovalFlowVersion>().AsNoTracking()
            .Where(v => flows.Any(f => f.Id == v.FlowId))
            .OrderBy(v => v.FlowId).ThenBy(v => v.Version)
            .Select(v => v.Definition).ToListAsync(ct);
    }
}
