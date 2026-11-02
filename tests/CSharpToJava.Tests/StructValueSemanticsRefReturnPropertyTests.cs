using System.IO;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>JsonDocument.PushRows</c>:
/// <c>StackRow row = stack.Value.Pop();</c> where <c>stack.Value</c> is a writable
/// ref-return property (<c>public ref StackRowStack Value =&gt; ref _value;</c>).
/// A writable ref return designates the storage location itself, so the mutating
/// call happens in place and needs no defensive copy (previously SV043).
/// A plain get-only (non-ref) property still yields a copy and must keep SV043.
/// </summary>
public class StructValueSemanticsRefReturnPropertyTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string RefReturnSource = """
        namespace Lib;

        public struct Counter
        {
            private int _value;

            public int Mutate()
            {
                _value++;
                return _value;
            }
        }

        public class Holder
        {
            private Counter _counter;

            public ref Counter Value => ref _counter;

            public int Run()
            {
                return Value.Mutate();
            }
        }
        """;

    private const string GetOnlySource = """
        namespace Lib;

        public struct Counter
        {
            private int _value;

            public int Mutate()
            {
                _value++;
                return _value;
            }
        }

        public class Holder
        {
            private Counter _counter;

            public Counter Value => _counter;

            public int Run()
            {
                return Value.Mutate();
            }
        }
        """;

    [Fact]
    public void WritableRefReturnProperty_InPlaceMutation_NoSV043()
    {
        var (diagnostics, result) = Rewrite(RefReturnSource);

        Assert.DoesNotContain(diagnostics,
            d => d.Severity == StructValueSemanticsSeverity.Error);
        Assert.DoesNotContain("__cs2jVc", result.OutputCode, StringComparison.Ordinal);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void GetOnlyProperty_StillReportsSV043()
    {
        var (diagnostics, result) = Rewrite(GetOnlySource);

        Assert.Contains(diagnostics, d =>
            d.Severity == StructValueSemanticsSeverity.Error
            && d.Code == StructValueSemanticsDiagnosticCode.SV043);
        AssertCompiles(result.OutputCode);
    }

    private static (List<StructValueSemanticsDiagnostic> Diagnostics,
        StructValueSemanticsFileResult Result) Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "svs-ref-return-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new StructValueSemanticsPreprocessor(
            new StructValueSemanticsOptions());
        preprocessor.BindCompilation(compilation);

        var statistics = new StructValueSemanticsStatistics();
        var diagnostics = new List<StructValueSemanticsDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), statistics, diagnostics);
        return (diagnostics, result);
    }

    private static void AssertCompiles(string outputCode)
    {
        var verify = CSharpCompilation.Create(
            "svs-ref-return-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(outputCode, ParseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = verify.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + outputCode);
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
