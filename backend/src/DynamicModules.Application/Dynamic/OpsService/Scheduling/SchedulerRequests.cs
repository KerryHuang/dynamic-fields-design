using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Scheduling;

// 排程器的租約操作都是交易型請求：SQLite 上經 WriteGate 排隊，不和使用者寫入搶鎖（O1 計畫判斷 J16）。
// 正確性只靠條件式更新，不靠排隊；時間由呼叫端（JobRunner）算好帶進來。

/// <summary>補上工作列（維運設計 §10.4、主控判斷 B7）：已存在回 Success；同時插入撞鍵回 Conflict（交易回滾，呼叫端當成別人已補）。</summary>
public sealed record EnsureScheduledJobRequest(string JobKey, DateTime NextDueUtc) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>搶租約；Data 是 bool（搶到與否）。</summary>
public sealed record AcquireJobLeaseRequest(string JobKey, string Owner, DateTime NowUtc, DateTime LeaseUntilUtc)
    : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>續租；Data 是 bool（仍是持有者與否）。</summary>
public sealed record RenewJobLeaseRequest(string JobKey, string Owner, DateTime LeaseUntilUtc) : IRequest<ServiceResult>, ITransactionalRequest;

/// <summary>寫結果並清租約；Data 是 bool（仍是持有者與否，不是就什麼都沒寫）。</summary>
public sealed record FinishJobRunRequest(string JobKey, string Owner, JobRunResult Result) : IRequest<ServiceResult>, ITransactionalRequest;
