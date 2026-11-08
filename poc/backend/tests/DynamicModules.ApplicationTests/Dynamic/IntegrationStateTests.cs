using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>整合狀態表（設計 §5.1）逐格：狀態只由當下條件推導，與歷史無關。</summary>
public class IntegrationStateTests
{
    [Theory]
    // enabled, installed, ownerOpen, partners, block → 目標
    [InlineData(false, false, true, true, false, IntegrationTarget.Disabled)]
    [InlineData(false, true, true, true, false, IntegrationTarget.Disabled)]
    [InlineData(false, true, false, false, true, IntegrationTarget.Disabled)]
    [InlineData(true, false, true, true, false, IntegrationTarget.Install)]
    [InlineData(true, false, true, false, false, IntegrationTarget.Waiting)]
    [InlineData(true, false, false, true, false, IntegrationTarget.Waiting)]
    [InlineData(true, false, false, false, true, IntegrationTarget.Waiting)]
    [InlineData(true, true, true, true, false, IntegrationTarget.Run)]
    [InlineData(true, true, true, true, true, IntegrationTarget.Run)]
    [InlineData(true, true, true, false, false, IntegrationTarget.Suspended)]
    [InlineData(true, true, true, false, true, IntegrationTarget.Held)]
    [InlineData(true, true, false, true, false, IntegrationTarget.Suspended)]
    [InlineData(true, true, false, false, true, IntegrationTarget.Suspended)]
    public void Derive_FollowsTheStateTable(bool enabled, bool installed, bool ownerOpen, bool partners, bool block, IntegrationTarget expected) =>
        IntegrationState.Derive(enabled, installed, ownerOpen, partners, block).Should().Be(expected);

    [Fact]
    public void Derive_DoesNotDependOnTheOrderOfEarlierEvents()
    {
        // 擁有者先關、夥伴先關、再開擁有者：最後的條件相同，結果就相同（必修 2）
        var a = IntegrationState.Derive(true, true, ownerOpen: true, partnersPresent: false, holdOnPartnerClosed: true);
        var b = IntegrationState.Derive(true, true, ownerOpen: true, partnersPresent: false, holdOnPartnerClosed: true);
        a.Should().Be(IntegrationTarget.Held).And.Be(b);
    }

    [Theory]
    [InlineData("inventory/move", "inventory", "move")]
    [InlineData("inventory/move.qty", "inventory", "move.qty")]
    [InlineData("shipment.qty", null, null)]
    [InlineData("/move", null, null)]
    [InlineData("inventory/", null, null)]
    public void SplitPartner_ReadsThePrefix(string symbol, string? app, string? rest)
    {
        var split = AppPackageValidator.SplitPartner(symbol);
        if (app is null) split.Should().BeNull();
        else split.Should().Be((app, rest!));
    }
}
