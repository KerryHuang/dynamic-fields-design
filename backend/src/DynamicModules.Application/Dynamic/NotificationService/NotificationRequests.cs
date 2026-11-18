using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>最新的通知與未讀數；需要時先送 <see cref="EnsureNotificationUpkeepRequest"/>。不是交易型請求：鈴鐺輪詢不搶寫入鎖。</summary>
public record GetNotificationsRequest(int Take = 20) : IRequest<ServiceResult>;

/// <summary>補寫到期提醒、清掉超過保留天數的通知；各來源在交易內以條件式更新蓋戳記，搶到才寫通知（兩條路徑並行也不重複）。</summary>
public record EnsureNotificationUpkeepRequest() : IRequest<ServiceResult>, ITransactionalRequest;

public record MarkNotificationReadRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;

public record MarkAllNotificationsReadRequest() : IRequest<ServiceResult>, ITransactionalRequest;
