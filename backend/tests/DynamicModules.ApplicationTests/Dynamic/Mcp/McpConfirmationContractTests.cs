using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.McpService.Confirmations;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>
/// 確認碼的巢狀與「確認提交之後」契約（mcp1a 計畫判斷 17、18；第三輪必修 3、4，第四輪必修 2、3）。
/// 用真的 <see cref="IAuditLog"/> 與資料庫；稽核表寫不進去用 <see cref="AuditInsertFault"/>。
/// </summary>
[Trait("Category", "Postgres")]
public class McpConfirmationContractTests : McpConfirmationTestBase
{
    private int ConfirmedRows() => Db.Context.Set<AuditLogEntry>().AsNoTracking()
        .Count(a => a.ConfirmationOutcome == AuditConfirmationOutcome.Confirmed);

    // ───── 巢狀 ─────

    [Fact]
    public async Task Nested_InTransactionalOuter_Throws()
    {
        var code = await Preview();
        var (target, a) = (ValuesOf(Target), ValuesOf(RecordA));
        Knobs.Inner = sender => sender.Send(new TouchRecordForTestRequest(Target, "v1", Ticket(code)));

        await FluentActions.Awaiting(() => SendDirect(new OuterForTestRequest(RecordA)))
            .Should().ThrowAsync<McpConfirmationNestingException>();

        (ValuesOf(Target), ValuesOf(RecordA)).Should().Be((target, a));
        ConfirmationRow(code).UsedUtc.Should().BeNull();
        ConfirmedRows().Should().Be(0);
    }

    [Fact]
    public async Task Nested_InvalidTicket_StillThrows()
    {
        var a = ValuesOf(RecordA);
        Knobs.Inner = sender => sender.Send(new TouchRecordForTestRequest(Target, "v1", Ticket(McpSecrets.NewConfirmationCode())));

        await FluentActions.Awaiting(() => SendDirect(new OuterForTestRequest(RecordA)))
            .Should().ThrowAsync<McpConfirmationNestingException>("Noop handle 不會替外層回滾，只能靠例外");
        ValuesOf(RecordA).Should().Be(a);
    }

    [Fact]
    public async Task Nested_MissingTicket_StillThrows()
    {
        var a = ValuesOf(RecordA);
        Knobs.Inner = sender => sender.Send(new TouchRecordForTestRequest(Target, "v1", null));

        await FluentActions.Awaiting(() => SendDirect(new OuterForTestRequest(RecordA)))
            .Should().ThrowAsync<McpConfirmationNestingException>();
        ValuesOf(RecordA).Should().Be(a);
    }

    [Fact]
    public async Task Nested_ConfirmedInConfirmed_Throws()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        // 外層確認請求的 handler 先消耗並寫入，再送另一個確認請求
        Knobs.Inner = sender => sender.Send(new TouchRecordForTestRequest(RecordA, "inner", Ticket(code)));

        var r = await Call(Args(code: code));

        CodeOf(r).Should().Be(McpErrorCodes.InternalError);
        ValuesOf(Target).Should().Be(before);
        ConfirmationRow(code).UsedUtc.Should().BeNull("外層的消耗也回滾");
        ConfirmedRows().Should().Be(0);

        Knobs.Inner = null;
        (await Call(Args(code: code))).IsError.Should().BeFalse("外層的碼可以重試成功");
    }

    [Fact]
    public async Task Nested_UiChannel_NoTicket_Allowed()
    {
        Channel.Channel = RequestChannel.Ui;
        Knobs.Inner = sender => sender.Send(new TouchRecordForTestRequest(Target, "ui-inner", null));

        (await SendDirect(new OuterForTestRequest(RecordA))).IsSuccess.Should().BeTrue();
        ValuesOf(Target).Should().Contain("ui-inner");
        ValuesOf(RecordA).Should().Contain("outer");
    }

    // ───── 確認提交之後 ─────

    private void ShouldBeExecuted(McpFormattedResult r, string omitted)
    {
        r.IsError.Should().BeFalse(r.Text);
        r.Structured.TryGetProperty("data", out _).Should().BeFalse();
        var meta = r.Structured.GetProperty("meta");
        meta.GetProperty("confirmation").GetProperty("status").GetString().Should().Be("executed");
        meta.GetProperty("confirmation").GetProperty("id").GetInt64().Should().BePositive();
        meta.GetProperty("dataOmitted").GetString().Should().Be(omitted);
        meta.GetProperty("instruction").GetString().Should().Be(McpTexts.ConfirmedResultOmitted);
    }

    /// <summary>這次確認呼叫在稽核表恰一列（Confirmed），沒有 Failed 列。</summary>
    private async Task OnlyConfirmedRowAfterPreview()
    {
        var rows = await McpRows();
        rows.Should().HaveCount(2, "預演一列、確認一列");
        rows[1].ConfirmationOutcome.Should().Be(AuditConfirmationOutcome.Confirmed);
        rows.Should().NotContain(a => a.Result == AuditResult.Failed);
    }

    [Fact]
    public async Task Confirmed_ResultTooLarge_NotError_OneRow()
    {
        var code = await Preview();
        Knobs.ResultData = () => new { text = new string('x', McpLimits.MaxResultChars) };

        ShouldBeExecuted(await Call(Args(code: code)), McpDataOmitted.ResultTooLarge);

        ValuesOf(Target).Should().Contain("v1");
        ConfirmationRow(code).UsedUtc.Should().NotBeNull();
        await OnlyConfirmedRowAfterPreview();
    }

    public sealed class ThrowsOnSerialize
    {
        public string Boom => throw new InvalidOperationException("serialize");
    }

    [Fact]
    public async Task Confirmed_FormatThrows_NotError_OneRow()
    {
        var code = await Preview();
        Knobs.ResultData = () => new ThrowsOnSerialize();

        ShouldBeExecuted(await Call(Args(code: code)), McpDataOmitted.FormatFailed);

        await OnlyConfirmedRowAfterPreview();
        lock (Logs) Logs.Should().ContainSingle(l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Confirmed_ToolThrowsAfterCommit_NotError_OneRow()
    {
        var code = await Preview();
        Knobs.AfterSend = () => throw new InvalidOperationException("post-commit");

        ShouldBeExecuted(await Call(Args(code: code)), McpDataOmitted.PostCommitError);

        await OnlyConfirmedRowAfterPreview();
        lock (Logs) Logs.Should().ContainSingle(l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Confirmed_CancelledAfterCommit_OneRow()
    {
        var code = await Preview();
        using var cts = new CancellationTokenSource();
        Knobs.AfterSend = cts.Cancel;

        await FluentActions.Awaiting(() => Call(Args(code: code), ct: cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        await OnlyConfirmedRowAfterPreview();
        (await McpRows()).Should().NotContain(a => a.ReasonCode == "cancelled");
    }

    [Fact]
    public async Task Confirmed_RolledBack_InvokerWrites()
    {
        var code = await Preview();
        var before = ValuesOf(Target);
        Fault.On = true;

        var r = await Call(Args(code: code));

        // 確認稽核存不進去 → 回滾、Committed 是 false → 執行器照結束路徑表寫 Failed；同一個攔截器也擋住它 → audit_unavailable
        CodeOf(r).Should().Be(McpErrorCodes.AuditUnavailable);
        r.Structured.TryGetProperty("meta", out _).Should().BeFalse("不回 executed");
        ValuesOf(Target).Should().Be(before);
        Fault.On = false;
        Db.Context.ChangeTracker.Clear();
        ConfirmedRows().Should().Be(0);
    }
}
