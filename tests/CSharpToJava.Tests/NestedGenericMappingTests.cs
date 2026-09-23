using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Issue #2: Nested generic type mapping should recursively resolve.
/// </summary>
public class NestedGenericMappingTests
{

    [Fact]
    public void TripleNestedGeneric_MapsAllLevels()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System;
using System.Collections.Generic;

class Sample
{
    Dictionary<string, List<KeyValuePair<int, double>>> deep;
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        // All primitives should be boxed at every level
        Assert.Contains("Integer", result.GeneratedCode);
        Assert.Contains("Double", result.GeneratedCode);
    }

    [Fact]
    public void TupleGenericArgs_AreBoxedRecursively()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System;
using System.Collections.Generic;

class Sample
{
    List<Tuple<int, string>> tuples;
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        // int should be boxed to Integer inside Tuple
        Assert.Contains("Integer", result.GeneratedCode);
    }

    [Fact]
    public void Tuple2Field_MapsToMapEntry()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System;
using System.Collections.Generic;

class Sample
{
    Tuple<int, int> pair;
    List<Tuple<int, int>> pairs;
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        // Tuple<int,int> should map to Map.Entry in type references
        Assert.DoesNotContain("AbstractMap.SimpleEntry", result.GeneratedCode);
    }
}
