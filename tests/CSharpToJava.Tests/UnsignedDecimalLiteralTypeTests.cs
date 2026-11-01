using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0266 in the generated json/tests tree
/// (Utf8JsonWriterTests.cs:5581): a suffix-less decimal literal whose value is
/// above int.MaxValue but within uint.MaxValue has C# type uint (the first of
/// int, uint, long, ulong that can represent it). The literal normalizer
/// classified it as long and appended an L suffix, turning `uint[] uints;
/// uints[2] = 3294967295;` into `uints[2] = 3294967295L;` — long is not
/// implicitly convertible to uint.
/// </summary>
public class UnsignedDecimalLiteralTypeTests
{
    [Fact]
    public void DecimalLiteral_In_UInt_Range_Keeps_UInt_Type()
    {
        const string source = """
            public class C
            {
                public static uint[] Values()
                {
                    var uints = new uint[2];
                    uints[0] = 4294967295;
                    uints[1] = 3294967295;
                    return uints;
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "unsigned-decimal-literal-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = new UnsignedTypeRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.True(result.Changed, "the unsigned pass did not rewrite the uint literals");
        // Both literals are uint in C#; appending L would make them long and
        // break the uint assignments (CS0266).
        Assert.DoesNotContain("3294967295L", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("4294967295L", result.OutputCode, StringComparison.Ordinal);
        // The uint values above int.MaxValue become hex so Java int can carry
        // their bit pattern (0xFFFFFFFF / 0xC4...).
        Assert.Contains("0x", result.OutputCode, StringComparison.OrdinalIgnoreCase);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void DecimalLiteral_Above_UInt_Range_Stays_Long()
    {
        // Guard against over-correction: 2^32 does not fit uint, so the C#
        // literal is long and must keep the L suffix Java requires.
        const string source = """
            public class C
            {
                public static long Value()
                {
                    return 4294967296;
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "unsigned-decimal-literal-wide-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = new UnsignedTypeRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.Contains("4294967296L", result.OutputCode, StringComparison.Ordinal);
        AssertOutputCompiles(result.OutputCode);
    }

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
        var compilation = CSharpCompilation.Create(
            "unsigned-decimal-literal-verify-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(code) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "unsigned rewrite output failed to compile:\n" + string.Join("\n", errors) +
            "\n---Generated---\n" + code);
    }
}
