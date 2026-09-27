using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Test-utility reflection batch:
/// 1. (DebuggerBrowsableState?)(int?)value — Nullable&lt;Enum&gt; casts must emit
///    EnumType.fromValue(...) (the target symbol is Nullable&lt;T&gt;, TypeKind.Struct,
///    which previously bypassed the enum-cast rules and emitted an invalid Java cast).
/// 2. GetCustomAttribute&lt;T&gt;() must keep the generic type argument so the returned
///    attribute's members resolve.
/// Regression: tests/TestUtilities DebuggerAttributes.cs + collection tests.
/// </summary>
public class ReflectionAttributeCompatTests
{
    private readonly ITestOutputHelper _out;

    public ReflectionAttributeCompatTests(ITestOutputHelper output) => _out = output;

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
