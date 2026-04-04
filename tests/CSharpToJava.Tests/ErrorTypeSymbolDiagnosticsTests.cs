using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Issue #1: IErrorTypeSymbol diagnostic reporting and improved resolution.
/// Verifies that unresolved types emit CS2J1001 diagnostics instead of silently
/// degrading to Object or short names.
/// </summary>
public class ErrorTypeSymbolDiagnosticsTests
{
    [Fact]
    public void UnresolvedType_EmitsCS2J1001Diagnostic()
    {
        // UnknownType is not referenced — Roslyn produces IErrorTypeSymbol.
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    UnknownType field;
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        // The unresolved type should produce a CS2J1001 diagnostic
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "CS2J1001" && d.Category == "TypeResolution");
    }

    [Fact]
    public void UnresolvedType_PreservesShortName()
    {
        // Even when a type can't be resolved, the short name should be kept
        // rather than silently degrading to Object.
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    MyCustomType field;
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.Contains("MyCustomType", result.GeneratedCode);
    }

    [Fact]
    public void MultipleUnresolvedTypes_EmitMultipleDiagnostics()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    TypeA fieldA;
    TypeB fieldB;
    TypeC Method() { return null; }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        var typeResolutionDiags = result.Diagnostics
            .Where(d => d.Code == "CS2J1001" && d.Category == "TypeResolution")
            .ToList();
        // Should have at least one diagnostic per unresolved type
        Assert.True(typeResolutionDiags.Count >= 3,
            $"Expected >=3 CS2J1001 diagnostics, got {typeResolutionDiags.Count}: " +
            string.Join("; ", typeResolutionDiags.Select(d => d.Message)));
    }

    [Fact]
    public void ResolvedType_NoCS2J1001Diagnostic()
    {
        // When types resolve properly, no CS2J1001 should be emitted.
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    List<string> items = new List<string>();
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1001");
    }
}
