using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RefOutHolderConflictTests
{
    [Fact]
    public void RefOutObjectHolder_WithUserDefinedNestedObjectHolder_UsesFullyQualifiedCompatHolder()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
            public class Sample
            {
                internal class ObjectHolder
                {
                    public object Object;
                }

                private void Write(ref object o)
                {
                    o = new object();
                }

                public void Run()
                {
                    object x = null;
                    Write(ref x);
                }
            }
            """,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The user-defined nested class must be preserved (non-generic).
        Assert.Contains("public static class ObjectHolder", code, StringComparison.Ordinal);

        // Ref/out holder references must use the fully-qualified compat holder
        // so they do not collide with the user-defined nested ObjectHolder.
        Assert.Contains("io.github.ningpp.compat.ObjectHolder<Object>", code, StringComparison.Ordinal);

        // The unqualified generic form would resolve to the nested non-generic
        // ObjectHolder and is therefore invalid.
        var unqualifiedPattern = new Regex(@"(?<!\.)\bObjectHolder<");
        Assert.DoesNotMatch(unqualifiedPattern, code);
    }
}
