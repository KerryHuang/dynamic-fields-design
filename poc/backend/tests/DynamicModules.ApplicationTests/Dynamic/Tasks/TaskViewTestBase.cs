using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.TaskViewService;
using DynamicModules.Persistence.Seed;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>看板存檔與分享測試共用：示範組織（manager01 管總經理室與業務部、ops01 管營運部）、以某人身分送請求。</summary>
public abstract class TaskViewTestBase : TaskTestBase
{
    protected const string StatusConfig = """{"version":1,"groupBy":"status","scope":"assigned"}""";

    protected override bool ScopePerRequest => true;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await DemoPeople.EnsureOrganizationAsync(Db.Context, SeedTime);
    }

    protected async Task<ServiceResult> SendAs(string user, MediatR.IRequest<ServiceResult> request)
    {
        As(user);
        try { return await Send(request); }
        finally { As("admin"); }
    }

    protected async Task<TaskViewDetail> CreateView(string owner, string name = "業務部任務", string config = StatusConfig)
    {
        var r = await SendAs(owner, new CreateTaskViewRequest("board", name, J(config)));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.SelectMany(e => e.Value)));
        return (TaskViewDetail)r.Data!;
    }

    protected async Task<TaskViewListResponse> ListViews(string user)
    {
        var r = await SendAs(user, new GetTaskViewsRequest("board"));
        r.IsSuccess.Should().BeTrue(r.Message);
        return (TaskViewListResponse)r.Data!;
    }

    protected async Task<HashSet<long>> SharedIds(string user) => (await ListViews(user)).Shared.Select(v => v.Id).ToHashSet();

    protected Task<ServiceResult> Share(string owner, long id, params (string Kind, string Id)[] targets) =>
        SendAs(owner, new SetTaskViewSharesRequest(id, targets.Select(t => new TaskViewShareInput(t.Kind, t.Id)).ToList()));
}
