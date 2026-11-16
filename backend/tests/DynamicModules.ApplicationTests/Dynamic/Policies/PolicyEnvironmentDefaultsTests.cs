using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Xunit;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>部署設定檔的值當環境出廠值：合法直接用，超出範圍夾值並記 Warning，無法使用退回程式出廠值。</summary>
public class PolicyEnvironmentDefaultsTests
{
    private static (PolicyEnvironmentDefaults Defaults, List<string> Warnings) Build(PolicyDefinition def, string raw)
    {
        var warnings = new List<string>();
        return (PolicyEnvironmentDefaults.Build([(def, raw)], warnings), warnings);
    }

    [Fact]
    public void OutOfRangeInteger_ClampedWithWarning()
    {
        var (defaults, warnings) = Build(PolicyCatalog.AttachmentMaxFileBytes, "0");
        defaults.FactoryOf(PolicyCatalog.AttachmentMaxFileBytes).Should().Be("1");
        warnings.Should().ContainSingle().Which.Should().Contain("attachment.maxFileBytes").And.Contain("「0」").And.Contain("改用 1");

        var (over, _) = Build(PolicyCatalog.AttachmentMaxFileBytes, "999999999");
        over.FactoryOf(PolicyCatalog.AttachmentMaxFileBytes).Should().Be("52428800");
    }

    [Fact]
    public void UnparsableValue_FallsBackToProgramFactory_WithWarning()
    {
        var (defaults, warnings) = Build(PolicyCatalog.AttachmentMaxFileBytes, "abc");
        defaults.FactoryOf(PolicyCatalog.AttachmentMaxFileBytes).Should().Be(PolicyCatalog.AttachmentMaxFileBytes.Factory);
        warnings.Should().ContainSingle().Which.Should().Contain("程式出廠值");
    }

    [Fact]
    public void ExtensionList_IsNormalizedWithoutWarning()
    {
        var (defaults, warnings) = Build(PolicyCatalog.AttachmentAllowedExtensions, ".PDF,png");
        defaults.FactoryOf(PolicyCatalog.AttachmentAllowedExtensions).Should().Be("pdf,png");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidValueBelowOldDefaults_IsKept()
    {
        var (defaults, warnings) = Build(PolicyCatalog.AttachmentMaxFileBytes, "10");
        defaults.FactoryOf(PolicyCatalog.AttachmentMaxFileBytes).Should().Be("10");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Clamp_ConfigForbiddenExtension_DroppedWithWarning()
    {
        var (defaults, warnings) = Build(PolicyCatalog.AttachmentAllowedExtensions, "pdf,exe,svg,txt");
        defaults.FactoryOf(PolicyCatalog.AttachmentAllowedExtensions).Should().Be("pdf,txt");
        warnings.Should().ContainSingle().Which.Should().Contain("attachment.allowedExtensions").And.Contain("pdf,exe,svg,txt");

        var (allBad, badWarnings) = Build(PolicyCatalog.AttachmentAllowedExtensions, "exe,html");
        allBad.FactoryOf(PolicyCatalog.AttachmentAllowedExtensions).Should().Be(PolicyCatalog.AttachmentAllowedExtensions.Factory);
        badWarnings.Should().ContainSingle();
    }

    [Fact]
    public void NothingConfigured_UsesProgramFactories()
    {
        var defaults = PolicyEnvironmentDefaults.Build([], []);
        defaults.FactoryOf(PolicyCatalog.AttachmentMaxFilesPerField).Should().Be("20");
    }
}
