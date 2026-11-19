using System.Text.Json;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Policies;

public class PolicyCatalogFixtureTests
{
    private sealed record Fixture(string[] Codes, string[] Categories, string[] Choices);

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "policy-catalog.json")))
            dir = dir.Parent;
        return dir is null
            ? throw new FileNotFoundException("找不到 shared/policy-catalog.json")
            : Path.Combine(dir.FullName, "shared", "policy-catalog.json");
    }

    [Fact]
    public void Codes_AreUnique_AndFactoriesAreNormal()
    {
        PolicyCatalog.All.Select(d => d.Code).Should().OnlyHaveUniqueItems();
        foreach (var d in PolicyCatalog.All)
        {
            d.Normalize(d.Factory).Should().Be(d.Factory, d.Code);
            d.SettingKey.Length.Should().BeLessOrEqualTo(100);
            PolicyCatalog.Find(d.Code).Should().BeSameAs(d);
        }
        PolicyCatalog.Find("nope").Should().BeNull();
        PolicyCatalog.Find("TASK.maxRulesPerModule").Should().BeNull();
    }

    [Fact]
    public void Factories_EqualCurrentBehavior()
    {
        PolicyCatalog.TaskMaxRulesPerModule.Factory.Should().Be("20");
        PolicyCatalog.TaskMaxRulesPerModule.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.TaskMaxTasksPerRule.Factory.Should().Be("20");
        PolicyCatalog.TaskMaxTasksPerRule.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.TaskMaxRulesPerPackage.Factory.Should().Be("50");
        PolicyCatalog.TaskMaxRulesPerPackage.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.TaskReminderLeadDays.Factory.Should().Be("1");
        PolicyCatalog.TaskRemindOverdue.Factory.Should().Be("true");
        PolicyCatalog.TaskNotifyCommented.Factory.Should().Be("assignee,creator");
        PolicyCatalog.TaskNotifyCompleted.Factory.Should().Be("creator");
        PolicyCatalog.TaskNotifyCancelled.Factory.Should().Be("creator");
        PolicyCatalog.TaskNotifySubtasksDone.Factory.Should().Be("assignee");
        PolicyCatalog.TaskNotifySubtasksDone.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.TaskSupervisorScope.Factory.Should().Be("true");
        PolicyCatalog.TaskSupervisorScope.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.TaskSupervisorScopeRecursive.Factory.Should().Be("true");
        PolicyCatalog.TaskSupervisorScopeRecursive.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.NotificationRetentionDays.Factory.Should().Be("90");
        PolicyCatalog.NotificationRetentionDays.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.NotificationReminderScanMinutes.Factory.Should().Be("60");
        PolicyCatalog.NotificationReminderScanMinutes.Min.Should().Be(5);
        PolicyCatalog.NotificationReminderScanMinutes.Max.Should().Be(1440);
        PolicyCatalog.NotificationReminderScanMinutes.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.CalendarWeekStart.Factory.Should().Be("monday");
        PolicyCatalog.CalendarWeekStart.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.AttachmentMaxFileBytes.Factory.Should().Be("10485760");
        PolicyCatalog.AttachmentMaxFileBytes.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.AttachmentMaxFileBytes.Max.Should().Be(52428800);
        PolicyCatalog.AttachmentMaxFilesPerField.Factory.Should().Be("20");
        PolicyCatalog.AttachmentMaxFilesPerField.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.AttachmentAllowedExtensions.Factory.Should().Be("pdf,png,jpg,jpeg,webp,txt,csv,docx,xlsx,pptx");
        PolicyCatalog.AttachmentAllowedExtensions.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.AttachmentMaxFilesPerComment.Factory.Should().Be("5");
        PolicyCatalog.AttachmentMaxFilesPerComment.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.AttachmentMaxFilesPerComment.Max.Should().Be(10);
        PolicyCatalog.OpsSlowRequestMs.Factory.Should().Be("2000");
        PolicyCatalog.OpsSlowRequestMs.Min.Should().Be(200);
        PolicyCatalog.OpsSlowRequestMs.Max.Should().Be(60000);
        PolicyCatalog.OpsSlowRequestMs.Scope.Should().Be(PolicyScope.System);
        PolicyCatalog.OpsClientErrorCollection.Factory.Should().Be("true");
        PolicyCatalog.OpsClientErrorCollection.Scope.Should().Be(PolicyScope.System);
        // p2a：設計上限（A16）、新模組名稱欄位（A19）、表單欄數（A25）
        PolicyCatalog.DesignMaxWorkflowStates.Factory.Should().Be("30");
        PolicyCatalog.DesignMaxWorkflowTransitions.Factory.Should().Be("200");
        PolicyCatalog.DesignMaxRulesPerModule.Factory.Should().Be("100");
        PolicyCatalog.DesignMaxGroupsPerPackage.Factory.Should().Be("20");
        PolicyCatalog.DesignMaxModulesPerPackage.Factory.Should().Be("30");
        PolicyCatalog.DesignMaxFieldsPerModule.Factory.Should().Be("300");
        PolicyCatalog.DesignMaxOptionsPerField.Factory.Should().Be("200");
        PolicyCatalog.DesignMaxViewsPerModule.Factory.Should().Be("20");
        PolicyCatalog.DesignMaxIntegrationsPerPackage.Factory.Should().Be("20");
        PolicyCatalog.ModuleCreateTitleField.Factory.Should().Be("true");
        PolicyCatalog.FormDefaultColumns.Factory.Should().Be("4");
        PolicyCatalog.FormDefaultColumns.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.FormDefaultColumns.Target.Should().Be(PolicyModuleTarget.Any);
        PolicyCatalog.FormMaxColumns.Factory.Should().Be("4");
        PolicyCatalog.FormMaxColumns.Scope.Should().Be(PolicyScope.System);
        // 前端 form-layout.ts 的 fieldColClass 斷點只排到 4 欄，兩邊一起改
        PolicyCatalog.FormMaxColumns.Max.Should().Be(4);
        PolicyCatalog.All.Where(d => d.Code.StartsWith("design.", StringComparison.Ordinal) || d.Code == "module.createTitleField")
            .Should().OnlyContain(d => d.Scope == PolicyScope.System && d.Category == "design");
        // p2b：匯入（A21）、連動事件（A22）、明細列（A23）、報表與儀表板（A24）
        PolicyCatalog.ImportMaxRows.Factory.Should().Be("1000");
        PolicyCatalog.ImportMaxFileBytes.Factory.Should().Be("1048576");
        PolicyCatalog.ImportMaxFileBytes.Min.Should().Be(1024);
        PolicyCatalog.ImportMaxFileBytes.Max.Should().Be(10485760);
        PolicyCatalog.EventMaxEventsPerModule.Factory.Should().Be("20");
        PolicyCatalog.EventMaxEffects.Factory.Should().Be("10");
        PolicyCatalog.EventMaxEffects.Max.Should().Be(50);
        PolicyCatalog.EventMaxDepth.Factory.Should().Be("5");
        PolicyCatalog.EventMaxWritesPerRequest.Factory.Should().Be("500");
        PolicyCatalog.EventMaxEachRows.Factory.Should().Be("200");
        PolicyCatalog.RecordMaxDetailRows.Factory.Should().Be("200");
        PolicyCatalog.RecordMaxDetailRows.Scope.Should().Be(PolicyScope.Module);
        PolicyCatalog.RecordMaxDetailRows.Target.Should().Be(PolicyModuleTarget.Detail);
        PolicyCatalog.ReportDrillLimit.Factory.Should().Be("500");
        PolicyCatalog.ReportDetailLimit.Factory.Should().Be("1000");
        PolicyCatalog.ReportMaxRangeDays.Factory.Should().Be("3650");
        // 等於 Domain ConditionParser.MaxDays（列表與規則條件的「近 N 天」），只能調低（計畫判斷 13）
        PolicyCatalog.ReportMaxRangeDays.Max.Should().Be(3650);
        PolicyCatalog.DashboardMaxCards.Factory.Should().Be("40");
        new[] { PolicyCatalog.ImportMaxRows, PolicyCatalog.ImportMaxFileBytes, PolicyCatalog.EventMaxEventsPerModule,
                PolicyCatalog.EventMaxEffects, PolicyCatalog.EventMaxDepth, PolicyCatalog.EventMaxWritesPerRequest,
                PolicyCatalog.EventMaxEachRows, PolicyCatalog.ReportDrillLimit, PolicyCatalog.ReportDetailLimit,
                PolicyCatalog.ReportMaxRangeDays, PolicyCatalog.DashboardMaxCards }
            .Should().OnlyContain(d => d.Scope == PolicyScope.System);
        // p2c：維運上限（A26、A27 進 O1 的 ops 分類，代碼仍是 maintenance.*）與跨記錄檢查（A28–A30）
        PolicyCatalog.MaintenanceIdempotencyRetentionDays.Factory.Should().Be("7");
        PolicyCatalog.MaintenanceIdempotencyRetentionDays.Max.Should().Be(90);
        PolicyCatalog.MaintenanceOrphanSweepGraceMinutes.Factory.Should().Be("60");
        PolicyCatalog.MaintenanceOrphanSweepGraceMinutes.Max.Should().Be(1440);
        PolicyCatalog.CheckScanMaxViolations.Factory.Should().Be("50");
        PolicyCatalog.CheckScanMaxGroups.Factory.Should().Be("20000");
        PolicyCatalog.CheckScanMaxGroups.Min.Should().Be(1000);
        PolicyCatalog.CheckMaxGroupsPerWrite.Factory.Should().Be("500");
        PolicyCatalog.CheckMaxBackfillRecords.Factory.Should().Be("200000");
        PolicyCatalog.CheckMaxBackfillRecords.Min.Should().Be(1000);
        PolicyCatalog.CheckMaxBackfillRecords.Max.Should().Be(1000000);
        new[] { PolicyCatalog.MaintenanceIdempotencyRetentionDays, PolicyCatalog.MaintenanceOrphanSweepGraceMinutes }
            .Should().OnlyContain(d => d.Scope == PolicyScope.System && d.Category == "ops");
        new[] { PolicyCatalog.CheckScanMaxViolations, PolicyCatalog.CheckScanMaxGroups, PolicyCatalog.CheckMaxGroupsPerWrite,
                PolicyCatalog.CheckMaxBackfillRecords }
            .Should().OnlyContain(d => d.Scope == PolicyScope.System && d.Category == "check");
        PolicyCatalog.WeekStartOf("sunday").Should().Be(DayOfWeek.Sunday);
        PolicyCatalog.WeekStartOf("monday").Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void Catalog_MatchesSharedFixture()
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FixturePath()),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        fixture.Codes.Should().BeEquivalentTo(PolicyCatalog.All.Select(d => d.Code));
        fixture.Categories.Should().BeEquivalentTo(PolicyCatalog.All.Select(d => d.Category).Distinct());
        fixture.Choices.Should().BeEquivalentTo(PolicyCatalog.All.SelectMany(d => d.Options ?? []).Distinct());
    }
}
