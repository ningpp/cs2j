using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that non-generic System.Collections.IList is mapped to a Java type
/// that allows adding concrete elements (CSharpGenericIList&lt;Object&gt;).
/// </summary>
public class NonGenericIListMappingTests
{
    [Fact]
    public void NonGenericIList_Parameter_MapsToObjectElementList()
    {
        var result = Convert(@"
using System.Collections;

public class SchemaHelper
{
    internal void CollectSchemas(IList extList, object schema)
    {
        if (extList.Contains(schema))
        {
            return;
        }
        extList.Add(schema);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpGenericIList<Object>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpGenericIList<?>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("extList.add(schema)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonGenericIList_Argument_FromGenericList_WrapsWithAdapter()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class Processor
{
    public void ProcessItems(IList list)
    {
        list.Add(null);
    }
}

public class Worker
{
    public void Work()
    {
        List<string> items = new List<string>();
        new Processor().ProcessItems(items);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpGenericIList<Object>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("CSharpGenericIList.from(items)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
