using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.TaskViewService;

public sealed record TaskViewSummary(long Id, string Kind, string Name, string OwnerUserId, string? OwnerName, bool OwnerActive,
    bool IsOwner, int ShareCount, DateTime? ModifiedTime);

/// <summary>Mine：我建立的；Shared：分享給我的（系統管理員另含所有有分享的）。</summary>
public sealed record TaskViewListResponse(IReadOnlyList<TaskViewSummary> Mine, IReadOnlyList<TaskViewSummary> Shared);

/// <summary>使用者帶 DisplayName，群組與部門帶 NameI18n；對象已停用時 Inactive，已不存在時兩個名稱都是 null。</summary>
public sealed record TaskViewShareResponse(string Kind, string Id, string? DisplayName, I18nText? NameI18n, bool Inactive);

/// <summary>Shares 只給擁有者與系統管理員看，其他人是空清單。Config 是正規化後的看板設定。</summary>
public sealed record TaskViewDetail(long Id, string Kind, string Name, string OwnerUserId, string? OwnerName, bool OwnerActive,
    bool IsOwner, bool CanEdit, bool CanDelete, JsonElement Config, IReadOnlyList<TaskViewShareResponse> Shares, string RowVersion);

public sealed record TaskViewUserTarget(string Id, string Name);

public sealed record TaskViewNamedTarget(long Id, I18nText Name);

public sealed record TaskViewShareTargets(IReadOnlyList<TaskViewUserTarget> Users, IReadOnlyList<TaskViewNamedTarget> Departments,
    IReadOnlyList<TaskViewNamedTarget> Groups);
