using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C17 must leave the Cs2jByRef holder family's <c>Value</c> increments to the
/// converter's direct map. The ref-out-in pass turns <c>out int x</c> into
/// <c>ByRef&lt;int&gt; x</c> and then emits <c>x.Value++</c>; the property is
/// ref-returning (so C17 would otherwise report strict CS2J5340 and roll the
/// whole file back), but the Java half already lowers these to
/// <c>getValue()/setValue(...)</c> in <c>UnaryExpressionTransformer</c>.
/// Only the C01 holder family on directly mappable signed value types is
/// exempt; user-defined ref properties and decimal/unsigned holders keep the
/// original strict behaviour.
/// </summary>
public class IncrementC01HolderValueTests
{
    private const string HolderStubs = """
        namespace Cs2jByRef
        {
            public sealed class ByRef<T>
            {
                private T _slot;
                public ref T Value => ref _slot;
            }

            public ref struct ScopedByRef<T>
            {
                private ref T _slot;
                private ScopedByRef(ref T value) { _slot = ref value; }
                public T Value { get => _slot; set => _slot = value; }
                public static ScopedByRef<T> Create(ref T value) => new ScopedByRef<T>(ref value);
            }
        }
        """;

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
            "inc-c01-" + Guid.NewGuid().ToString("N"),
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
            new NumericBoundaryOptions(),
            new NumericBoundaryStatistics(),
            diagnostics);
        var result = new IncrementTargetRule().Execute(context);
        return (result.Root.ToFullString(), diagnostics, result.MadeChanges);
    }

    [Fact]
    public void ByRef_Int_Value_Increment_Stays_Native_And_Leaves_No_Error()
    {
        var src = HolderStubs + """

            public static class Demo
            {
                public static void Run()
                {
                    var h = new Cs2jByRef.ByRef<int>();
                    h.Value++;
                    h.Value--;
                }
            }
            """;

        var (output, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.DoesNotContain(diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.IncrementTargetRefReturn);
        Assert.Equal(src, output);
    }

    [Fact]
    public void ScopedByRef_Int_Value_Increment_Stays_Native_And_Leaves_No_Error()
    {
        var src = HolderStubs + """

            public static class Demo
            {
                static void Bump(ref int slot)
                {
                    var h = Cs2jByRef.ScopedByRef<int>.Create(ref slot);
                    h.Value++;
                    h.Value--;
                }
            }
            """;

        var (output, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Equal(src, output);
    }

    [Fact]
    public void User_Defined_Ref_Property_Increment_Still_Reports_CS2J5340()
    {
        const string src = """
            public sealed class UserBox
            {
                private int _slot;
                public ref int P => ref _slot;
            }
            public static class Demo
            {
                public static void Run()
                {
                    var b = new UserBox();
                    b.P++;
                }
            }
            """;

        var (output, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetRefReturn);
        Assert.Equal(src, output);
    }

    [Theory]
    [InlineData("decimal")]
    [InlineData("uint")]
    public void Non_Direct_Map_Holder_Value_Increment_Is_Not_Exempt(string valueType)
    {
        var src = HolderStubs + $$"""

            public static class Demo
            {
                public static void Run()
                {
                    var h = new Cs2jByRef.ByRef<{{valueType}}>();
                    h.Value++;
                }
            }
            """;

        var (output, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetRefReturn);
        Assert.Equal(src, output);
    }
}
