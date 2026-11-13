using System.IO;
using CSharpToJava.Core.EnumDeclaration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.EnumDeclaration;

/// <summary>
/// Repro for the real-project build error SYSLIB1051: a source-generated P/Invoke
/// (<c>[LibraryImport]</c>) rejects an enum normalized to an enum-like class in its
/// signature ("源生成的 P/Invoke 不支持 ... 类型"). Runtime <c>[DllImport]</c>
/// marshalling does not accept plain reference types either. Enums carried by such
/// signatures — directly, through pointers/arrays, or as fields of a by-value
/// marshalled struct — must stay real enums (ED020).
/// </summary>
public class EnumDeclarationInteropSignatureTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string DllImportParameterSource = """
        namespace Repro
        {
            internal enum OidKeyType
            {
                OidKey = 1,
                NameKey = 2,
                AlgIdKey = 3
            }

            internal static class Native
            {
                [System.Runtime.InteropServices.DllImport("crypt32.dll")]
                internal static extern int CryptFindOIDInfo(OidKeyType dwKeyType, System.IntPtr pvKey);
            }
        }
        """;

    private const string LibraryImportReturnSource = """
        namespace Repro
        {
            internal enum LookupStatus
            {
                Ok = 0,
                NotFound = 1
            }

            internal static partial class Native
            {
                [System.Runtime.InteropServices.LibraryImport("native")]
                internal static partial LookupStatus TryLookup(System.IntPtr key);
            }
        }
        """;

    private const string StructFieldSource = """
        namespace Repro
        {
            internal enum KeyKind
            {
                None = 0,
                Oid = 1
            }

            [System.Runtime.InteropServices.StructLayout(
                System.Runtime.InteropServices.LayoutKind.Sequential)]
            internal struct LookupRequest
            {
                public KeyKind Kind;
                public System.IntPtr Key;
            }

            internal static partial class Native
            {
                [System.Runtime.InteropServices.LibraryImport("native")]
                internal static partial int Lookup(LookupRequest request);
            }
        }
        """;

    private const string NonInteropSource = """
        namespace Repro
        {
            internal enum LocalState
            {
                None = 0,
                Active = 1
            }

            internal static class Worker
            {
                private static LocalState _state;

                internal static bool IsActive() => _state == LocalState.Active;
            }
        }
        """;

    [Fact]
    public void DllImportParameterEnum_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(DllImportParameterSource);

        Assert.False(result.Changed,
            "an enum used as a [DllImport] parameter type cannot be marshalled as a class.\n"
            + "--- output ---\n" + result.OutputCode);
        Assert.Contains("enum OidKeyType", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("P/Invoke"));
    }

    [Fact]
    public void LibraryImportReturnEnum_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(LibraryImportReturnSource);

        Assert.False(result.Changed,
            "an enum return type of a source-generated [LibraryImport] has no marshalling "
            + "support for the class form.\n--- output ---\n" + result.OutputCode);
        Assert.Contains("enum LookupStatus", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("P/Invoke"));
    }

    [Fact]
    public void InteropStructEnumField_KeepsEnumReal_ED020()
    {
        var (result, diags) = Rewrite(StructFieldSource);

        Assert.False(result.Changed,
            "an enum carried by a struct field of a by-value P/Invoke parameter must stay "
            + "real; the generated marshalling code cannot handle a class field.\n"
            + "--- output ---\n" + result.OutputCode);
        Assert.Contains("enum KeyKind", result.OutputCode);
        Assert.Contains(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("P/Invoke"));
    }

    [Fact]
    public void EnumOutsideInteropSignatures_StillConverts()
    {
        var (result, diags) = Rewrite(NonInteropSource);

        Assert.True(result.Changed,
            "an enum that never appears in a P/Invoke signature must still be normalized.\n"
            + "--- output ---\n" + result.OutputCode);
        Assert.Contains("sealed class LocalState", result.OutputCode);
        Assert.DoesNotContain(diags, d =>
            d.Code == EnumDeclarationDiagnosticCode.ED020
            && d.Message.Contains("P/Invoke"));
        AssertOutputCompiles(result.OutputCode);
    }

    private static (EnumDeclarationFileResult Result, List<EnumDeclarationDiagnostic> Diags)
        Rewrite(string source, EnumDeclarationOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "ed-interop-repro",
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
                "ed-interop-repro-verify",
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
