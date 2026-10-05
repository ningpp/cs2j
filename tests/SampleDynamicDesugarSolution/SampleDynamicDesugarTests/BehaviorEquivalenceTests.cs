using SampleDynamicDesugarApp;
using SampleDynamicDesugarLib;
using Xunit;

namespace SampleDynamicDesugarTests;

/// <summary>
/// C25 .NET-side equivalence oracle (design §7.2/§7.3). These assertions hold on
/// the original solution AND on the Rewrite intermediate tree — the adapter
/// verify gate runs dotnet build/test there — proving P1 concretizations preserve
/// the original dynamic runtime behavior. This file contains no dynamic tokens.
/// </summary>
public class BehaviorEquivalenceTests
{
    [Fact]
    public void P1_SealedReceiverChain_KeepsBehavior()
        => Assert.Equal("builder:2", P1Concretizations.SealedReceiverChain());

    [Fact]
    public void P1_IntArithmetic_KeepsBehavior()
        => Assert.Equal(247, P1Concretizations.IntArithmetic());

    [Fact]
    public void P1_StringArithmetic_AndForeach_KeepBehavior()
        => Assert.Equal("ABabK1K2", P1Concretizations.StringArithmetic());

    [Fact]
    public void UnprovableOpenType_KeepsRuntimeDispatch()
        => Assert.Equal("round-widget", OpenTypeUnprovable.DescribeWidget());

    [Fact]
    public void DynamicSignatureBoundary_KeepsBehavior()
        => Assert.Equal(42, DynamicSignatures.EchoAndAddTwo(40));

    [Fact]
    public void DynamicPropertyStorage_KeepsBehavior()
        => Assert.Equal(43, DynamicStorage.StoreAndRead(42));

    [Fact]
    public void ExpandoObject_KeepsDynamicDispatch()
        => Assert.Equal("expando-ada", ExpandoInterop.ExpandoName());

    [Fact]
    public void MetadataDynamicAttributeBoundary_KeepsBehavior()
    {
        Assert.Equal(44, MetadataBoundary.EchoThroughMetadata());
        Assert.Equal(string.Empty, MetadataBoundary.FormatThroughMetadata());
        Assert.Equal(88, MetadataBoundary.PropertyThroughMetadata());
    }

    [Fact]
    public void App_CrossFileDynamicBoundary_KeepsBehavior()
    {
        Assert.Equal(42, CrossFileDynamic.CrossFileCompute(40));
        Assert.Equal(42, CrossFileDynamic.JsonBoundaryNumber());
    }
}
