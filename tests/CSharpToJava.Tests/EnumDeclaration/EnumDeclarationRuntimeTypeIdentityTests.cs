using System.IO;
using CSharpToJava.Core.EnumDeclaration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.EnumDeclaration;

/// <summary>
/// Repro for SDK json tests (Serialization/TestClasses): a <c>typeof(E)</c> whose
/// runtime <see cref="System.Type"/> value escapes into arbitrary code (attribute
/// payload, object[] test data, string concat, type comparison…) cannot be
/// normalized. After enum-as-class lowering <c>Type.IsEnum</c> is false, so
/// reflection-driven consumers (System.Text.Json's EnumConverterFactory,
/// Enum.GetValues/GetNames on a Type variable…) silently change behavior.
/// Such enums must stay real (ED020), while typeof(E) that is only the argument
/// of a rewritable System.Enum API call must keep converting.
/// </summary>
public class EnumDeclarationRuntimeTypeIdentityTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string EscapingSource = """
        namespace Repro
        {
            public enum RuntimeTypeEnum : byte
            {
                A = 1,
                B = 2
            }

            public static class RuntimeTypeConsumer
            {
                // typeof(E) escapes into an attribute payload: the runtime Type is
                // observed by reflection-driven test data consumers.
                [System.ComponentModel.Description(typeof(RuntimeTypeEnum))]
                public static readonly object[] Data = new object[] { typeof(RuntimeTypeEnum), 1 };
            }
        }
        """;

    private const string RewritableApiSource = """
        namespace Repro
        {
            public enum RewritableEnum : byte
            {
                A = 1,
                B = 2
            }

            public static class ApiConsumer
            {
                public static System.Array Values()
                {
                    return System.Enum.GetValues(typeof(RewritableEnum));
                }
            }
        }
        """;

    [Fact]
    public void TypeofEscapingEnumApi_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(EscapingSource);

        Assert.False(result.Changed,
            "typeof(E) escaping into arbitrary code must keep the enum real.\n--- output ---\n"
            + result.OutputCode);
        Assert.Contains("enum RuntimeTypeEnum", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("runtime System.Type"));
    }

    [Fact]
    public void TypeofInsideRewritableEnumApi_StillConverts()
    {
        var (result, diags) = Rewrite(RewritableApiSource);

        Assert.True(result.Changed,
            "typeof(E) as System.Enum API argument is rewritable; the plan must still land.\n"
            + "--- output ---\n" + result.OutputCode);
        Assert.Contains("sealed class RewritableEnum", result.OutputCode);
        Assert.DoesNotContain(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("runtime System.Type"));
        AssertOutputCompiles(result.OutputCode);
    }

    private static (EnumDeclarationFileResult Result, List<EnumDeclarationDiagnostic> Diags)
        Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "ed-typeof-repro",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new EnumDeclarationPreprocessor();
        var stats = new EnumDeclarationStatistics();
        var diags = new List<EnumDeclarationDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), stats, diags);
        return (result, diags);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions, path: "Rewritten.cs");
        var errors = CSharpCompilation.Create(
                "ed-typeof-repro-verify",
                new[] { tree },
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
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
}
