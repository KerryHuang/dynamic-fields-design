using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.NotificationService;

/// <summary>最新的通知與未讀數；需要時先送 <see cref="EnsureNotificationUpkeepRequest"/>。不是交易型請求：鈴鐺輪詢不搶寫入鎖。</summary>
public record GetNotificationsRequest(int Take = 20) : IRequest<ServiceResult>;

/// <summary>補寫到期提醒、清掉 90 天前的通知；交易內由各來源再判斷一次才寫。</summary>
public record EnsureNotificationUpkeepRequest() : IRequest<ServiceResult>, ITransactionalRequest;

public record MarkNotificationReadRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest;

public record MarkAllNotificationsReadRequest() : IRequest<ServiceResult>, ITransactionalRequest;
