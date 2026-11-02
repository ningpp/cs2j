using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C34 DC-12 (const-decimal downgrade) must cover every const declaration whose
/// initializer becomes a runtime BCL call, not only <c>const decimal</c>.
/// <code>
/// const decimal Threshold = 1.0m;
/// const bool Below = Threshold &lt; 2.0m;
/// </code>
/// rewrites the comparison to <c>decimal.LessThan(Threshold, 2.0m)</c>; leaving
/// the declarator <c>const</c> produces CS0134 (a const initializer must be a
/// compile-time constant). The declaration and every const it drags must be
/// downgraded so the emitted C# still compiles.
/// </summary>
public class NumericBoundaryDecimalConstContextTests
{
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

    private static (string Output, List<NumericBoundaryDiagnostic> Diagnostics, bool Changed) RunRule(string src)
    {
        var tree = (SyntaxTree)CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "c34-const-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);

        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0,
            "test input failed to compile:\n" + string.Join("\n", compileErrors));

        var diagnostics = new List<NumericBoundaryDiagnostic>();
        var context = new NumericBoundaryRuleContext(
            (CompilationUnitSyntax)tree.GetRoot(),
            model,
            projectDefaultChecked: false,
            new NumericBoundaryOptions
            {
                EnableDecimalOps = true,
                EnableIncrementTargets = false,
                EnableNullable = false,
            },
            new NumericBoundaryStatistics(),
            diagnostics);
        var result = new DecimalOpsRule().Execute(context);
        return (result.Root.ToFullString(), diagnostics, result.MadeChanges);
    }

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
            "c34-const-out-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "rewritten output must compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + source);
    }

    private static string Invoke(string source)
    {
        var compilation = CSharpCompilation.Create(
            "c34-const-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var demo = asm.GetType("Demo")!;
        return (string)demo.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
    }

    private static string Compact(string code) =>
        new(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private const string ConstComparisonSource = """
        public static class Demo
        {
            const decimal Threshold = 1.0m;
            const bool Below = Threshold < 2.0m;
            const bool Same = 1.0m == Threshold;
            const string Label = Below ? "below" : "above";

            public static string Run() => Below + "," + Same + "," + Label;
        }
        """;

    [Fact]
    public void Decimal_Operator_In_NonDecimal_Const_Initializer_Downgrades_Declaration()
    {
        var expected = Invoke(ConstComparisonSource);
        var (output, diagnostics, changed) = RunRule(ConstComparisonSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        AssertCompiles(output);

        var compact = Compact(output);
        // Every const that ends up holding a runtime decimal call (directly or
        // through a reference) must become static readonly; no const may remain.
        Assert.DoesNotContain("const", compact);
        Assert.Contains("staticreadonlydecimalThreshold", compact);
        Assert.Contains("staticreadonlyboolBelow", compact);
        Assert.Contains("staticreadonlyboolSame", compact);
        Assert.Contains("staticreadonlystringLabel", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("True,True,below", actual);
    }
}
