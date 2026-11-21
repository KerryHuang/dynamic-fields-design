using System.Reflection;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 測試用的 IApprovalFlowRepository 替身：在 TryReplaceDraftAsync 之前先跑一次 <see cref="BeforeReplace"/>（模擬別人在讀取之後、
/// 條件更新之前把草稿改掉），其餘呼叫原樣轉給真正的實作。
/// </summary>
public class RacingFlowRepository : DispatchProxy
{
    public IApprovalFlowRepository Inner { get; set; } = null!;
    public Func<Task>? BeforeReplace { get; set; }
    /// <summary>要在哪個方法之前跑 <see cref="BeforeReplace"/>；預設 TryReplaceDraftAsync。</summary>
    public string Method { get; set; } = nameof(IApprovalFlowRepository.TryReplaceDraftAsync);

    /// <summary>以 <paramref name="race"/> 取得這次要跑的動作（每次請求取一次，跑完就清掉）。</summary>
    public static void Register(IServiceCollection services, Func<Func<Task>?> race, Func<string?>? method = null) =>
        services.AddScoped(sp =>
        {
            var proxy = Create<IApprovalFlowRepository, RacingFlowRepository>();
            var racing = (RacingFlowRepository)(object)proxy;
            racing.Inner = new ApprovalFlowRepository(sp.GetRequiredService<DynamicDbContext>());
            racing.BeforeReplace = race();
            if (method?.Invoke() is { } name) racing.Method = name;
            return proxy;
        });

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == Method && BeforeReplace is { } hook)
        {
            BeforeReplace = null;
            // Task.Run：PostgreSQL 是真的非同步 I/O，同步等待不能占著 xUnit 的同步內容，否則平行跑時整個測試行程卡死
            Task.Run(hook).GetAwaiter().GetResult();
        }
        try { return targetMethod.Invoke(Inner, args); }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }
}
