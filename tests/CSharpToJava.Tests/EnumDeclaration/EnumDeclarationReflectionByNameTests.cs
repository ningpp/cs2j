using System.IO;
using CSharpToJava.Core.EnumDeclaration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.EnumDeclaration;

/// <summary>
/// Repro for the Pkcs TimestampRequestTests runtime failure:
/// <c>System.ArgumentException : The value passed in must be an enum base or an
/// underlying type for an enum</c> thrown from <c>Enum.ToObject</c>. The library
/// method is reached through <c>typeof(T).GetMethod("M", ...).Invoke(...)</c>;
/// runtime reflection boxes the enum out-value, so the caller receives an
/// enum-like class instance ("object") instead of an enum base value. An enum
/// carried by the signature of a method looked up by name through reflection
/// must stay a real enum (ED020), while ordinary methods keep converting.
/// </summary>
public class EnumDeclarationReflectionByNameTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string ReflectionLookupSource = """
        namespace Repro
        {
            [System.Flags]
            internal enum LookupStatus : byte
            {
                None = 0,
                Ok = 1,
                Failed = 2
            }

            internal static class NativeApi
            {
                internal static bool TryProcess(out LookupStatus status)
                {
                    status = LookupStatus.Ok;
                    return true;
                }
            }

            internal static class Caller
            {
                internal static System.Reflection.MethodInfo? Lookup()
                    => typeof(NativeApi).GetMethod(
                        "TryProcess",
                        System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Static);
            }
        }
        """;

    private const string NameofLookupSource = """
        namespace Repro
        {
            [System.Flags]
            internal enum LookupStatus : byte
            {
                None = 0,
                Ok = 1,
                Failed = 2
            }

            internal static class NativeApi
            {
                internal static bool TryProcess(out LookupStatus status)
                {
                    status = LookupStatus.Ok;
                    return true;
                }
            }

            internal static class Caller
            {
                internal static System.Reflection.MethodInfo? Lookup()
                    => typeof(NativeApi).GetMethod(nameof(NativeApi.TryProcess));
            }
        }
        """;

    private const string NoReflectionSource = """
        namespace Repro
        {
            [System.Flags]
            internal enum LookupStatus : byte
            {
                None = 0,
                Ok = 1,
                Failed = 2
            }

            internal static class NativeApi
            {
                internal static bool TryProcess(out LookupStatus status)
                {
                    status = LookupStatus.Ok;
                    return true;
                }
            }
        }
        """;

    [Fact]
    public void MethodLookedUpByName_SignatureEnum_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(ReflectionLookupSource);

        Assert.False(result.Changed,
            "an enum in the signature of a method reached via Type.GetMethod is boxed by "
            + "runtime reflection and must stay real.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum LookupStatus", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("Type.GetMethod"));
    }

    [Fact]
    public void NameofLookup_SignatureEnum_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(NameofLookupSource);

        Assert.False(result.Changed,
            "nameof(M) is a compile-time constant and resolves the same by-name reflection "
            + "lookup; the enum must stay real.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum LookupStatus", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("Type.GetMethod"));
    }

    [Fact]
    public void OrdinaryOutEnumWithoutReflection_StillConverts()
    {
        var (result, diags) = Rewrite(NoReflectionSource);

        Assert.True(result.Changed,
            "an out enum parameter of an ordinary method never exposes a boxed enum base "
            + "value; the plan must still land.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("sealed class LookupStatus", result.OutputCode);
        Assert.DoesNotContain(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("Type.GetMethod"));
        AssertOutputCompiles(result.OutputCode);
    }

    private static (EnumDeclarationFileResult Result, List<EnumDeclarationDiagnostic> Diags)
        Rewrite(string source, EnumDeclarationOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "ed-reflection-by-name-repro",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new EnumDeclarationPreprocessor(options);
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
                "ed-reflection-by-name-repro-verify",
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
