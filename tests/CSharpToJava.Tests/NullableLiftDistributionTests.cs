using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.Nullable;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C22 anchor-free conditional distribution and condition-slot hoisting
/// (RegexNode.cs:402 / RegexCompiler.cs:2527 shapes):
/// <code>
/// (c ? (T?)v : (T?)null) == k  →  (c ? v == k : false)
/// (c ? (T?)v : (T?)null) != k  →  (c ? v != k : true)
/// relational                   →  (c ? v &lt;op&gt; k : false)
/// </code>
/// The distribution needs no hoist (the live value is read once by the
/// source conditional itself); the non-nullable operand is read only on the
/// live branch, which is the same conditional-read discipline as the normal
/// one-sided HasValue guard. A site nested in an ALWAYS-evaluated slot
/// (ternary condition / left of &amp;&amp; || ??) is hoistable with unchanged
/// evaluation frequency; value arms stay blocked (CS2J5355).
/// </summary>
public class NullableLiftDistributionTests
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
            "nl-dist-" + Guid.NewGuid().ToString("N"),
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
        var result = new NullableLiftRule().Execute(context);
        return (context.Root.ToFullString(), diagnostics, result.MadeChanges);
    }

    private static string Invoke(string source)
    {
        var compilation = CSharpCompilation.Create(
            "nl-dist-emit-" + Guid.NewGuid().ToString("N"),
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

    private const string ComparisonSource = """
        using System.Text;
        public sealed class Holder
        {
            public bool Flag;
            public int Num;
            public Holder(bool f, int n) { Flag = f; Num = n; }
        }
        public static class Demo
        {
            static string Eq(Holder h)
                => (h != null ? (bool?)h.Flag : (bool?)null) == true ? "Y" : "N";
            static string Ne(Holder h)
                => (h != null ? (bool?)h.Flag : (bool?)null) != true ? "Y" : "N";
            static string Lt(Holder h, int k)
                => (h != null ? (int?)h.Num : (int?)null) < k ? "Y" : "N";

            public static string Run()
            {
                var sb = new StringBuilder();
                Holder[] holders = { null, new Holder(false, 1), new Holder(true, 5) };
                foreach (var h in holders)
                {
                    sb.Append(Eq(h)).Append(Ne(h)).Append(Lt(h, 3)).Append(';');
                }
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void Conditional_Nullable_Comparison_Distributes_Into_Arms_And_Behaves_Identically()
    {
        var expected = Invoke(ComparisonSource);
        var (output, diagnostics, changed) = RunRule(ComparisonSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        Assert.Contains("h!=null?h.Flag==true:false", compact);
        Assert.Contains("h!=null?h.Flag!=true:true", compact);
        Assert.Contains("h!=null?h.Num<k:false", compact);
        // No hoisted temps and no surviving lifted HasValue guard.
        Assert.DoesNotContain("__cs2j_n_", output);
        Assert.DoesNotContain("HasValue", output);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("NYN;NYY;YNN;", actual);
    }

    private const string ConditionSlotSource = """
        public sealed class Box
        {
            private readonly int _limit;
            public Box(int limit) { _limit = limit; }
            public int Limit
            {
                get { Demo.LimitReads++; return _limit; }
            }
            public override string ToString() => "T" + _limit;
        }
        public static class Demo
        {
            public static int LimitReads;

            static string Choose(string s, Box k)
            {
                // The lifted comparison is the CONDITION of the outer ternary:
                // both operands (the int? conditional and the unstable Limit
                // property) are evaluated on every execution, so hoisting them
                // before the return keeps frequency and ordering.
                return (s != null ? (int?)s.Length : (int?)null) == k.Limit ? s : k.ToString();
            }

            public static string Run()
            {
                string[] inputs = { null, "ab", "abcd" };
                var limits = new[] { new Box(5), new Box(2), new Box(2) };
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < inputs.Length; i++)
                {
                    LimitReads = 0;
                    var result = Choose(inputs[i], limits[i]);
                    sb.Append(result).Append('/').Append(LimitReads).Append(';');
                }
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void Site_In_Ternary_Condition_Slot_Is_Hoisted_And_Reads_Other_Operand_Always()
    {
        var expected = Invoke(ConditionSlotSource);
        var (output, diagnostics, changed) = RunRule(ConditionSlotSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        // Both operands materialized once before the anchor; the lifted
        // equality became the HasValue one-sided guard.
        Assert.Contains("__cs2j_n_a", output);
        Assert.Contains("__cs2j_n_b", output);
        Assert.Contains("HasValue", output);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        // null/2eq/2ne: every case takes the other operand exactly once,
        // including the null case where the comparison result is false.
        Assert.Equal("T5/1;ab/1;T2/1;", actual);
    }

    [Fact]
    public void Site_In_Ternary_Value_Arm_Still_Reports_CS2J5355()
    {
        const string src = """
            public static class Demo
            {
                static int Get() => 1;

                public static string Run()
                {
                    int? x = 2;
                    var y = true ? (x == Get()).ToString() : "n";
                    return y ?? "";
                }
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }
}
