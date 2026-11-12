using System.Diagnostics.Metrics;
using DynamicModules.Application.Common.Observability;
using FluentAssertions;
using Xunit;

namespace DynamicModules.ApplicationTests.Common;

public class RuleFailureTagTests
{
    [Fact]
    public void Mark_ReturnsFalse_InnermostWins()
    {
        Exception? caught = null;
        try
        {
            try
            {
                try { throw new InvalidOperationException("x"); }
                catch (Exception e) when (RuleFailureTag.Mark(e, RuleFailureTag.Check, 2)) { }
            }
            catch (Exception e) when (RuleFailureTag.Mark(e, RuleFailureTag.Event, 1)) { }
        }
        catch (Exception e) { caught = e; }

        caught.Should().BeOfType<InvalidOperationException>();
        RuleFailureTag.Read(caught!).Should().Be(("check", 2L));
    }

    [Fact]
    public void Mark_DoesNotCount()
    {
        var count = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == OpsMetrics.MeterName && inst.Name == "formwright.rule.failures") l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref count));
        listener.Start();

        RuleFailureTag.Mark(new Exception("x"), RuleFailureTag.TaskRule, 9).Should().BeFalse();

        count.Should().Be(0);
    }

    [Fact]
    public void Read_Unmarked_ReturnsNulls() =>
        RuleFailureTag.Read(new Exception("x")).Should().Be(((string?)null, (long?)null));
}
