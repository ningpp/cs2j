using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# ushort variable ++/-- operations correctly wrap around
/// in Java, matching C# unchecked overflow semantics.
/// In C#, ushort wraps: --i when i=0 gives 65535.
/// In Java, int doesn't wrap: --i when i=0 gives -1.
/// The converter must add &amp; 0xFFFF masking to simulate wrap-around.
/// </summary>
public class UShortIncrementDecrementTests
{

    [Fact]
    public void Int_Decrement_NotAffected()
    {
        var result = Convert(@"
class Test {
    void M() {
        int i = 5;
        --i;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // --i for int should remain as --i (no masking)
        Assert.Contains("--i", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression for Uri.cs:2813 / UriExt.cs:435 CS0029 in the converted
    /// dotnet-uri tree: the unsigned pass lowers prefix ushort ++/-- to an
    /// ASSIGNMENT expression (assignment has the lowest precedence). Embedded
    /// in a larger expression without grouping parens, raw printing let the
    /// enclosing operator bind as the RHS of the assignment
    /// (`x = ToUInt16Int32(x + 1) < bound` → bool→ushort CS0029). The lowered
    /// assignment must carry parens in every value-expression slot.
    /// </summary>
    [Fact]
    public void Prefix_Increment_In_If_Condition_Groups_Assignment_And_Keeps_Semantics()
    {
        const string source = """
            public static class Demo
            {
                public static int Run()
                {
                    ushort x = 0;
                    int n = 0;
                    if (++x < 1) n += 100;
                    if (++x < 5) n += 7;
                    ushort y = 65535;
                    if (++y < 10) n += 3000;
                    return n + x + y;
                }
            }
            """;

        var expected = Invoke(source);
        var output = Rewrite(source);

        var compact = Compact(output);
        Assert.Contains(
            "if((x=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(x+1))<1)",
            compact);
        Assert.Contains(
            "if((y=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(y+1))<10)",
            compact);

        AssertCompiles(output);
        Assert.Equal(expected, Invoke(output));
        // n=3007 (7 from the x compare, 3000 from the wrapped-y compare),
        // x ends at 2, y wraps 65535→0.
        Assert.Equal(3009, Invoke(output));
    }

    [Fact]
    public void Prefix_Increment_In_While_Condition_With_Logical_And_Groups_Only_The_Assignment()
    {
        const string source = """
            public static class Demo
            {
                public static int Run()
                {
                    ushort x = 0;
                    int count = 0;
                    bool flag = true;
                    while (++x < 5 && flag)
                    {
                        count++;
                    }
                    return count * 10 + x;
                }
            }
            """;

        var expected = Invoke(source);
        var output = Rewrite(source);

        var compact = Compact(output);
        // The grouping parens close right after the assignment; the whole
        // `&& flag` clause stays OUTSIDE them.
        Assert.Contains(
            "while((x=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(x+1))<5&&flag)",
            compact);
        // The pre-fix shape let the assignment swallow the whole condition:
        // `(x = ToUInt16Int32(x + 1) < 5 && flag)` — assert it is absent.
        Assert.DoesNotContain("ToUInt16Int32(x+1)<5&&flag)", compact);

        AssertCompiles(output);
        Assert.Equal(expected, Invoke(output));
        // Four loop iterations, x ends at 5.
        Assert.Equal(45, Invoke(output));
    }

    [Fact]
    public void Standalone_Prefix_Increment_Stays_A_Bare_Assignment_Statement()
    {
        const string source = """
            public static class Demo
            {
                public static int Run()
                {
                    ushort x = 65535;
                    ++x;
                    ++x;
                    return x;
                }
            }
            """;

        var expected = Invoke(source);
        var output = Rewrite(source);

        var compact = Compact(output);
        Assert.Contains(
            "x=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(x+1);",
            compact);
        // No redundant grouping parens around the statement-position assignment.
        Assert.DoesNotContain("(x=Cs2jUnsigned", compact);

        AssertCompiles(output);
        Assert.Equal(expected, Invoke(output));
        Assert.Equal(1, Invoke(output));
    }

    [Fact]
    public void Prefix_Decrement_In_Condition_Groups_Assignment_And_Wraps_The_Same_Way()
    {
        const string source = """
            public static class Demo
            {
                public static int Run()
                {
                    ushort x = 0;
                    int n = 0;
                    if (--x > 100) n += 5;
                    return n + x;
                }
            }
            """;

        var expected = Invoke(source);
        var output = Rewrite(source);

        var compact = Compact(output);
        Assert.Contains(
            "if((x=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(x-1))>100)",
            compact);

        AssertCompiles(output);
        Assert.Equal(expected, Invoke(output));
        // 0 wraps to 65535, comparison true once.
        Assert.Equal(5 + 65535, Invoke(output));
    }

    [Fact]
    public void Prefix_Increment_In_For_Initializer_And_Incrementor_Stays_A_Bare_Assignment()
    {
        // A for-loop initializer/incrementor is itself a statement-expression
        // slot: a parenthesized assignment there is rejected with CS0201.
        const string source = """
            public static class Demo
            {
                public static int Run()
                {
                    int sum = 0;
                    for (ushort i = 0; i < 5; ++i)
                    {
                        sum += i;
                    }

                    ushort x = 0;
                    int n = 0;
                    for (++x; x < 4; ++x)
                    {
                        n++;
                    }
                    return sum * 100 + n * 10 + x;
                }
            }
            """;

        var expected = Invoke(source);
        var output = Rewrite(source);

        var compact = Compact(output);
        Assert.Contains(
            "i<5;i=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(i+1))",
            compact);
        Assert.Contains(
            "for(x=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(x+1);x<4;x=Cs2jUnsigned.UnsignedMath.ToUInt16Int32(x+1))",
            compact);
        // No GROUPING paren around the assignment in either for slot (the
        // for-keyword's own opening paren is unavoidable in the compact text).
        Assert.DoesNotContain("for((", compact);
        Assert.DoesNotContain(";(i=Cs2jUnsigned", compact);
        Assert.DoesNotContain(";(x=Cs2jUnsigned", compact);

        AssertCompiles(output);
        Assert.Equal(expected, Invoke(output));
        // 1+2+3+4=10, second loop iterates x=1,2,3 three times ending at 4.
        Assert.Equal(1034, Invoke(output));
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    // Minimal semantic-matching shim of the Cs2jUnsigned support library call
    // the lowering emits (real implementation: unchecked((ushort)(v & 0xFFFF))).
    private const string UnsignedMathShim = """
        namespace Cs2jUnsigned
        {
            public static class UnsignedMath
            {
                public static ushort ToUInt16Int32(int v) => unchecked((ushort)(v & 0xFFFF));
            }
        }
        """;

    private static string Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "ushort-inc-rewrite-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var inputErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.True(inputErrors.Count == 0,
            "test input failed to compile:\n" + string.Join("\n", inputErrors));

        var result = new UnsignedTypeRewriter().Rewrite(source, compilation.GetSemanticModel(tree));
        Assert.True(result.Changed, "the unsigned pass did not rewrite the prefix site");
        return result.OutputCode;
    }

    private static void AssertCompiles(string code)
    {
        var compilation = CSharpCompilation.Create(
            "ushort-inc-verify-" + Guid.NewGuid().ToString("N"),
            new[]
            {
                CSharpSyntaxTree.ParseText(code),
                CSharpSyntaxTree.ParseText(UnsignedMathShim),
            },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "unsigned rewrite output failed to compile:\n" + string.Join("\n", errors));
    }

    private static object? Invoke(string source)
    {
        var compilation = CSharpCompilation.Create(
            "ushort-inc-emit-" + Guid.NewGuid().ToString("N"),
            new[]
            {
                CSharpSyntaxTree.ParseText(source),
                CSharpSyntaxTree.ParseText(UnsignedMathShim),
            },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var demo = asm.GetType("Demo")!;
        return demo.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
    }

    private static string Compact(string code) =>
        new(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
