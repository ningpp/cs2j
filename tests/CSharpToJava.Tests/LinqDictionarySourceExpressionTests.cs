using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue: LINQ query over Dictionary property produces the C# type name
/// "System.Collections.Generic.Dictionary" instead of the property reference in the
/// generated Java call site.
///
/// Root cause: The LINQ rewriter appends `.entrySet()` (a Java method) to the C#
/// syntax tree for Dictionary sources.  After the compilation is rebuilt, Roslyn
/// cannot resolve the artificial `.entrySet()` member, which poisons semantic
/// resolution of the receiver expression.  The main converter then falls back to
/// emitting the raw type display string instead of the property/variable name.
///
/// Expected Java output: the property/variable name (e.g. `multiedges`) should
/// appear at the call site, NOT the C# type name.
/// </summary>
public class LinqDictionarySourceExpressionTests
{

    [Fact]
    public void LinqMethodChainOverDictionaryVariable_ShouldUseVariableName()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Linq;

class MyClass
{
    void Test()
    {
        var dict = new Dictionary<string, int>();
        var keys = dict.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
    }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("System.Collections", result.GeneratedCode, StringComparison.Ordinal);
        // The variable name 'dict' should appear in the generated helper call
        Assert.Contains("dict", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
    }
}
