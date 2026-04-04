using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Issue #4: Cross-inheritance type erasure conflict detection.
/// </summary>
public class CrossInheritanceErasureTests
{
    [Fact]
    public void DerivedMethod_SameErasedSig_DifferentGenericArgs_EmitsCS2J1004()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Base
{
    public virtual void Process(List<string> items) { }
}

class Derived : Base
{
    public void Process(List<int> items) { }
}",
            FileName = "Derived.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "CS2J1004" && d.Category == "TypeErasure"
            && d.Message.Contains("Process"));
    }

    [Fact]
    public void DerivedMethod_SameExactSignature_NoCS2J1004()
    {
        // Override with same types — not a conflict
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Base
{
    public virtual void Process(List<string> items) { }
}

class Derived : Base
{
    public override void Process(List<string> items) { }
}",
            FileName = "Derived.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1004");
    }

    [Fact]
    public void SameType_ErasureConflict_UsesExistingRename()
    {
        // Same-type erasure conflicts should use the existing rename mechanism,
        // not the cross-inheritance diagnostic
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    void M(object x) { }
    void M<T>(T x) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        // Same-type erasure should NOT produce CS2J1004
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1004");
    }

    [Fact]
    public void NonGenericDerivedMethod_DifferentParamCount_NoConflict()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Base
{
    public void Process(List<string> a, List<string> b) { }
}

class Derived : Base
{
    public void Process(List<int> a) { }
}",
            FileName = "Derived.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        // Different param count — no erasure conflict
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1004");
    }
}
