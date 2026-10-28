using CSharpToJava.Core.DefaultParameterStripper;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests.StripDfault;

/// <summary>
/// Regression tests for GraphLayout <c>Test/AglNingppTests/SeriesParallelLayoutTests.cs</c>.
///
/// The conditional pass runs first and removes every <c>#if</c>, but it keeps
/// <c>#region</c>/<c>#endregion</c>. strip-default runs much later (after
/// tuple → foreach-deconstruction → pattern-desugar) and synthesises forwarding
/// overloads by REUSING the original declaration's first token. That token
/// carries the declaration's leading trivia, including the <c>#region Helpers</c>
/// directive, so every generated overload appended a second/third
/// <c>#region Helpers</c> with no matching <c>#endregion</c> → CS1038
/// (“应输入 #endregion 指令”) and the preprocessed project no longer builds.
/// </summary>
public sealed class StripDefaultRegionDirectiveTests
{
    [Fact]
    public void MethodPrecededByRegion_GeneratedOverloadsDoNotDuplicateRegion()
    {
        const string src = """
            class C
            {
                private int Tolerance;

            #region Helpers
                private int CreateNode(int x = 0, int y = 0) { return x + y; }

                private int Other() { return Tolerance; }
            #endregion
            }
            """;

        var output = Strip(src);

        Assert.Equal(1, CountOccurrences(output, "#region"));
        Assert.Equal(1, CountOccurrences(output, "#endregion"));
        AssertCompiles(output);
    }

    [Fact]
    public void InterfaceMethodWithoutModifiersPrecededByRegion_GeneratedOverloadDoesNotDuplicateRegion()
    {
        const string src = """
            interface I
            {
            #region Helpers
                void M(int a, int b = 0);
            #endregion
            }
            """;

        var output = Strip(src);

        Assert.Equal(1, CountOccurrences(output, "#region"));
        Assert.Equal(1, CountOccurrences(output, "#endregion"));
        AssertCompiles(output);
    }

    [Fact]
    public void ConstructorPrecededByRegion_GeneratedOverloadsDoNotDuplicateRegion()
    {
        const string src = """
            class C
            {
            #region Helpers
                public C(int a, int b = 0) { }
            #endregion
            }
            """;

        var output = Strip(src);

        Assert.Equal(1, CountOccurrences(output, "#region"));
        Assert.Equal(1, CountOccurrences(output, "#endregion"));
        AssertCompiles(output);
    }

    [Fact]
    public void LocalFunctionPrecededByRegion_GeneratedOverloadsDoNotDuplicateRegion()
    {
        const string src = """
            class C
            {
                public int M()
                {
                    int a = 1;

                #region Helpers
                    int Add(int x = 0, int y = 0) { return x + y; }

                    int Other() { return a; }
                #endregion

                    return Add();
                }
            }
            """;

        var output = Strip(src);

        // Local-function overloads are rejected by the C# compiler itself
        // (CS0128: 局部函数不能重载) — that is a separate, pre-existing
        // limitation. This test only locks the trivia contract: the generated
        // overload must not copy the original declaration's #region.
        Assert.Equal(1, CountOccurrences(output, "#region"));
        Assert.Equal(1, CountOccurrences(output, "#endregion"));
    }

    private static string Strip(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("strip-region-test",
            new[] { tree },
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var stripper = new DefaultParameterStripper();
        stripper.Collect(tree, model);
        var result = stripper.Strip(src, model);
        Assert.True(result.Changed);
        return result.OutputCode;
    }

    private static void AssertCompiles(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("strip-region-output",
            new[] { tree },
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Output should compile. Errors:\n" + string.Join("\n", errors) + "\n--- output ---\n" + source);
    }

    private static IEnumerable<MetadataReference> BuildMetadataReferences()
    {
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
