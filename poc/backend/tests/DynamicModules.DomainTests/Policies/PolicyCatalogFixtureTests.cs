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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "poc", "shared", "policy-catalog.json")))
            dir = dir.Parent;
        return dir is null
            ? throw new FileNotFoundException("找不到 poc/shared/policy-catalog.json")
            : Path.Combine(dir.FullName, "poc", "shared", "policy-catalog.json");
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
        PolicyCatalog.NotificationRetentionDays.Factory.Should().Be("90");
        PolicyCatalog.NotificationRetentionDays.Scope.Should().Be(PolicyScope.System);
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
