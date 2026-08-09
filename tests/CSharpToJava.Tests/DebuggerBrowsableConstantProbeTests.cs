using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Iteration 23: verify the Roslyn constant value surfaced for
/// [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)] — the converter emits
/// @DebuggerBrowsable(3) but RootHidden is 2 in .NET.
/// </summary>
public class DebuggerBrowsableConstantProbeTests
{
    private readonly ITestOutputHelper _out;

    public DebuggerBrowsableConstantProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RootHiddenConstant_IsTwo()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using System.Diagnostics;
class Proxy<T> {
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public T[] Items { get { return null; } }
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        _out.WriteLine(code);
        // RootHidden == 3 in .NET 10 (Never=0, Collapsed=2, RootHidden=3)
        Assert.Contains("@DebuggerBrowsable(3)", code);
    }
}
