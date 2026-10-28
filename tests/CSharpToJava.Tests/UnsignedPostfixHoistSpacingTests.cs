using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0103 in the unsigned output tree (MSAGL Solver.cs
/// ~L145 and ConstraintGenerator.cs ~L151): a value-used `x++` nested inside a
/// larger expression is hoisted into a synthetic temp declaration. That
/// declaration was assembled from freshly created tokens without any trivia,
/// so the text came out as `var__cs2j_unsigned_0=...` and the later
/// `__cs2j_unsigned_0` reference was undeclared.
/// </summary>
public class UnsignedPostfixHoistSpacingTests
{
    [Fact]
    public void ValueUsedPostfix_InArgument_AndInitializer_Hoists_Spaced_Temp_Declaration()
    {
        const string source = """
            public class Solver
            {
                private uint nextVariableOrdinal;

                public static int Variable(uint id) => (int)id;

                public int Construct()
                {
                    uint local = 7;
                    int first = Variable(nextVariableOrdinal++);
                    int second = Variable(local++);
                    return first + second;
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "unsigned-postfix-hoist-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = new UnsignedTypeRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.True(result.Changed, "the unsigned pass did not rewrite the hoisted postfix sites");
        Assert.True(result.OutputCode.Contains("var __cs2j_unsigned_0 = ", StringComparison.Ordinal), result.OutputCode);
        Assert.True(result.OutputCode.Contains("var __cs2j_unsigned_1 = ", StringComparison.Ordinal), result.OutputCode);
        Assert.DoesNotContain("var__cs2j_unsigned_", result.OutputCode, StringComparison.Ordinal);

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

    private static string HelperSource { get; } = File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Cs2jUnsignedMath", "UnsignedMath.cs")));

    private static void AssertOutputCompiles(string code)
    {
        var compilation = CSharpCompilation.Create(
            "unsigned-postfix-hoist-verify-" + Guid.NewGuid().ToString("N"),
            new[]
            {
                CSharpSyntaxTree.ParseText(code),
                CSharpSyntaxTree.ParseText(HelperSource),
            },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "unsigned rewrite output failed to compile:\n" + string.Join("\n", errors) +
            "\n---Generated---\n" + code);
    }
}
