using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;

namespace DynamicModules.Application.Dynamic.DashboardService.Data;

/// <summary>指派給我的儀表板一筆；Adjusted 表示我有個人版，HasUpdate 表示管理員在我的個人版之後更新過。</summary>
public record DashboardListItem(long Id, string Name, int Version, bool Adjusted, bool HasUpdate);

public record MyDashboardItem(long Id, string Name);

public record SharedReportItem(long Id, string Name, string OwnerName);

public record DashboardList(List<DashboardListItem> Assigned, List<MyDashboardItem> Mine, List<SharedReportItem> SharedReports);

/// <summary>Kind 是 assigned|mine；Layout 是對這位觀看者生效的版面（有個人版就是個人版）。</summary>
public record DashboardDetail(long Id, string Name, string Kind, int Version, bool Adjusted, bool HasUpdate, DashboardLayout Layout);

public record AdminDashboardItem(long Id, string Name, int Version, int Sequence, List<long> GroupIds, int CardCount);

public record AdminDashboardDetail(long Id, string Name, int Version, int Sequence, List<long> GroupIds, DashboardLayout Layout);

public static class DashboardMapper
{
    public static AdminDashboardDetail ToAdminDetail(this Dashboard d) =>
        new(d.Id, d.Name, d.Version, d.Sequence, d.Assignments.Select(a => a.GroupId).Order().ToList(), DashboardLayout.Parse(d.LayoutJson));

    public static AdminDashboardItem ToAdminItem(this Dashboard d) =>
        new(d.Id, d.Name, d.Version, d.Sequence, d.Assignments.Select(a => a.GroupId).Order().ToList(), DashboardLayout.Parse(d.LayoutJson).Cards.Count);
}
