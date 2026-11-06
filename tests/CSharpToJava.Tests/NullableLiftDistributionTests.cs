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

    // ── B-1: if / else-if conditions are hoistable anchors ──

    private const string IfConditionSource = """
        public enum Mode { Off = 0, On = 1, Always = 2 }

        public sealed class Spec
        {
            private readonly Mode? _mode;

            public Spec(Mode? mode) { _mode = mode; }

            public int ModeReads { get; private set; }

            public Mode? Mode
            {
                get { ModeReads++; return _mode; }
            }
        }

        public static class Demo
        {
            private static string Probe(Spec spec, bool isFirst)
            {
                if (isFirst)
                {
                    return "f";
                }
                else if (spec.Mode != Mode.Always)
                {
                    return "n";
                }

                return "a";
            }

            public static string Run()
            {
                var sb = new System.Text.StringBuilder();
                foreach (var spec in new[] { new Spec(Mode.On), new Spec(Mode.Always), new Spec(null) })
                {
                    sb.Append(Probe(spec, false)).Append(spec.ModeReads).Append(';');
                }

                sb.Append(Probe(new Spec(Mode.On), true));
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void UnstableNullableProperty_In_ElseIf_Condition_Is_Hoisted_Once()
    {
        var expected = Invoke(IfConditionSource);
        var (output, diagnostics, changed) = RunRule(IfConditionSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        // The property getter is read exactly once into a hoisted temp before
        // the else-if; the lifted enum comparison becomes the HasValue guard.
        var compact = Compact(output);
        Assert.Contains("var__cs2j_n_a1=spec.Mode", compact);
        Assert.Contains("__cs2j_n_a1.HasValue", compact);
        Assert.DoesNotContain("spec.Mode!=Mode.Always", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("n1;a1;n1;f", actual);
    }

    [Fact]
    public void UnstableNullableProperty_In_ShortCircuit_If_Condition_Still_Reports_CS2J5355()
    {
        const string src = """
            public enum Mode { Off = 0, On = 1, Always = 2 }

            public sealed class Spec
            {
                public Spec(Mode? mode) { Mode = mode; }

                public Mode? Mode { get; }
            }

            public static class Demo
            {
                private static string Probe(Spec spec, bool isFirst)
                {
                    if (isFirst && spec.Mode != Mode.Always)
                    {
                        return "n";
                    }

                    return "a";
                }

                public static string Run() => Probe(new Spec(Mode.On), true);
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }

    // ── B-2: the whole conditional left operand is hoisted as one temp ──

    private const string WholeOperandSource = """
        public sealed class Options
        {
            private readonly bool? _flag;

            public Options(bool? flag) { _flag = flag; }

            public int FlagReads { get; private set; }

            public bool? UseFlag
            {
                get { FlagReads++; return _flag; }
            }
        }

        public static class Demo
        {
            private static string Probe(Options options, bool skip)
            {
                if (skip)
                {
                    return "s";
                }

                if ((options != null ? options.UseFlag : null) == true)
                {
                    return "Y";
                }

                return "N";
            }

            public static string Run()
            {
                var sb = new System.Text.StringBuilder();
                foreach (var options in new Options[] { null, new Options(false), new Options(true) })
                {
                    var result = Probe(options, false);
                    sb.Append(result).Append(options == null ? 0 : options.FlagReads).Append(';');
                }

                sb.Append(Probe(null, true));
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void Conditional_Left_Operand_Is_Hoisted_Whole_And_Reads_Once()
    {
        var expected = Invoke(WholeOperandSource);
        var (output, diagnostics, changed) = RunRule(WholeOperandSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        // The temp holds the ENTIRE source conditional (null-arm protection
        // included), never a bare arm read.
        var compact = Compact(output);
        Assert.Contains(
            "var__cs2j_n_a1=(options!=null?options.UseFlag:null)", compact);
        Assert.Contains("__cs2j_n_a1.HasValue", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("N0;N1;Y1;s", actual);
    }

    [Fact]
    public void Conditional_Left_Operand_In_ShortCircuit_Right_Still_Reports_CS2J5355()
    {
        const string src = """
            public sealed class Options
            {
                public Options(bool? flag) { UseFlag = flag; }

                public bool? UseFlag { get; }
            }

            public static class Demo
            {
                private static string Probe(Options options, bool skip)
                {
                    if (skip || (options != null ? options.UseFlag : null) == true)
                    {
                        return "Y";
                    }

                    return "N";
                }

                public static string Run() => Probe(new Options(true), false);
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }

    // ── implicit T? → T unwrap in return position (CS2J5357) ──

    private const string ImplicitUnwrapReturnSource = """
        public enum JsonKind { None = 0, String = 1 }

        public sealed class Cache
        {
            private bool? _flag;
            private JsonKind? _kind;

            // `return _flag ??= Compute();` after the null-coalesce pass
            // lowers the compound into an if + a plain nullable return.
            public bool Get()
            {
                if (_flag == null)
                {
                    _flag = Compute();
                }

                return _flag;
            }

            private bool Compute() => true;

            public JsonKind GetKind()
            {
                if (_kind == null)
                {
                    _kind = JsonKind.String;
                }

                return _kind;
            }
        }

        public static class Demo
        {
            public static string Run()
            {
                var cache = new Cache();
                return cache.Get() + "|" + (int)cache.GetKind();
            }
        }
        """;

    /// <summary>
    /// The post-null-coalesce shape is deliberately not compilable in C#
    /// (a <c>T?</c> value cannot be returned from a <c>T</c> member without a
    /// cast), so this runner skips the input-compile assertion and verifies the
    /// REWRITTEN output instead.
    /// </summary>
    private static (string Output, List<NumericBoundaryDiagnostic> Diagnostics, bool Changed)
        RunRuleAllowErrors(string src)
    {
        var tree = (SyntaxTree)CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "nl-unwrap-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);

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

    [Fact]
    public void Implicit_Unwrap_In_Return_Position_Becomes_Value_And_Clears_Residual()
    {
        var (output, diagnostics, changed) = RunRuleAllowErrors(ImplicitUnwrapReturnSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.DoesNotContain(diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);

        var compact = Compact(output);
        Assert.Contains("return_flag.Value;", compact);
        Assert.Contains("return_kind.Value;", compact);
        Assert.DoesNotContain("_flag.Value.Value", compact);

        var actual = Invoke(output);
        Assert.Equal("True|1", actual);
    }

    [Fact]
    public void Explicit_Cast_Return_Is_Rewritten_Once_And_Semantics_Hold()
    {
        // Adjacent negative: the explicit-cast lane is untouched by the new
        // implicit-return lane (no double `.Value`).
        const string src = """
            public sealed class Cache
            {
                private bool? _flag;

                public Cache(bool? flag) { _flag = flag; }

                public bool Get() { return (bool)_flag; }
            }

            public static class Demo
            {
                public static string Run() => new Cache(true).Get().ToString();
            }
            """;
        var expected = Invoke(src);
        var (output, diagnostics, changed) = RunRule(src);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Contains("return_flag.Value;", Compact(output));
        Assert.DoesNotContain("_flag.Value.Value", Compact(output));
        Assert.Equal(expected, Invoke(output));
        Assert.Equal("True", Invoke(output));
    }
}
