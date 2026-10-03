using SampleVarianceAuditLib;
using Xunit;

namespace SampleVarianceAuditTests;

/// <summary>
/// .NET-side behavior baseline for the fixture. The variance-audit pass is read-only
/// (it never rewrites source), so these tests only pin that the fixture stays
/// compilable and semantically well-defined over time.
/// </summary>
public class RuntimeBehaviorTests
{
    [Fact]
    public void Producer_And_ReadOnlyBox_ReturnTheirValues()
    {
        Assert.Equal("hello", new StringProducer("hello").Produce());
        Assert.Equal(42, new Box<int>(42).Value);
    }

    [Fact]
    public void Consumer_CollectsItems()
    {
        var consumer = new StringConsumer();
        consumer.Consume("a");
        consumer.Consume("b");
        Assert.Equal(2, consumer.Count);
    }

    [Fact]
    public void NestedComposition_AppliesActionsWithDefaultValue()
    {
        var received = new List<int>();
        INested<int> nested = new NestedImpl<int>();
        nested.Apply(new Action<int>[] { received.Add });
        Assert.Equal(new[] { 0 }, received);
    }

    [Fact]
    public void Delegate_IsContravariantInInput_AndCovariantInResult()
    {
        Transformer<object, string> wideInput = o => o.ToString() ?? string.Empty;
        Transformer<string, string> narrowInput = wideInput;
        Transformer<string, object> wideResult = narrowInput;
        Assert.Equal("x", wideResult("x"));
    }

    [Fact]
    public void ConstraintHelpers_BehavePerDotNetSemantics()
    {
        Assert.Equal(0, ConstraintSamples.Zero<int>());
        Assert.Equal(0d, ConstraintSamples.Zero<double>());
        Assert.NotNull(ConstraintSamples.CreateWithNew<object>());
        ConstraintSamples.TouchUnmanaged<int>();
        ConstraintSamples.RequireNotNull("value");
        Assert.Equal("fallback", ConstraintSamples.Coalesce<string>(null, "fallback"));
    }

    [Fact]
    public void DefaultConstraint_OverrideIsCallable()
    {
        ConstraintBase instance = new ConstraintDerived();
        instance.Describe<int>();
    }
}