using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0133 in the generated regular-expressions/uri tree
/// (IPv4AddressHelper.MaxIPv4Value): the unsigned rewriter widened
/// <c>const long F = uint.MaxValue;</c> into a ToInt64UInt32(...) CALL, which
/// is not a constant expression. The constant-context guard never fired for
/// the declarator itself because Initializer.Span.Contains(declarator.Span)
/// is false (the declarator span covers the initializer).
/// </summary>
public class UnsignedConstInitializerTests
{
    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "unsigned-const-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "unsigned rewrite output failed to compile:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Const_Field_And_Local_UInt_MaxValue_Are_Not_Rewritten_To_Helper_Calls()
    {
        const string source = """
            public class C
            {
                private const long MaxIPv4Value = uint.MaxValue;

                public static long Go()
                {
                    const long localMax = uint.MaxValue;
                    return MaxIPv4Value + localMax;
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "unsigned-const-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = new UnsignedTypeRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.DoesNotContain("ToInt64UInt32", result.OutputCode, StringComparison.Ordinal);
        // The constant field access is preserved (only qualified) — a constant
        // expression, so the declaration still compiles (CS0133 regression).
        Assert.Contains("UInt32.MaxValue", result.OutputCode, StringComparison.Ordinal);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Non_Const_Widening_Still_Uses_The_Helper()
    {
        // Guard against over-suppression: an ordinary (non-const) long local
        // initialized from a uint value still needs the zero-extension helper.
        const string source = """
            public class C
            {
                public static long Go(uint u)
                {
                    long v = u;
                    return v;
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "unsigned-wide-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = new UnsignedTypeRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.Contains("ToInt64UInt32", result.OutputCode, StringComparison.Ordinal);
    }
}
