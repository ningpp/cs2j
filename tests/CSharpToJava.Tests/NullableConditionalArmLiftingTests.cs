using System;
using System.Collections.Generic;
using System.IO;
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
/// C22 conditional-arm lanes (rt10 <c>numeric-boundary</c> CS2J5355):
/// <list type="bullet">
/// <item><c>bool? == true</c> / <c>!= true</c> is two-valued (a null operand
/// compares exactly like <c>false</c>) and distributes into the arms of an
/// anchor-free source conditional, so a field initializer or a short-circuit
/// right arm needs no hoisted temporary;</item>
/// <item><c>if (A &amp;&amp; B)</c> with a lifted site in <c>B</c> is
/// linearized into a nested if (a <c>__cs2j_n_scN</c> flag plus a guarded
/// assignment when an else clause must stay on the outer statement), keeping
/// the source short-circuit order and frequency.</item>
/// </list>
/// </summary>
public class NullableConditionalArmLiftingTests
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
            "nl-arm-" + Guid.NewGuid().ToString("N"),
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
            "nl-arm-emit-" + Guid.NewGuid().ToString("N"),
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

    // ── S1 + S3: bool? == true in a `||` right arm ──

    private const string ShortCircuitRightSource = """
        using System.Text;

        public sealed class Options
        {
            private readonly bool? _flag;

            public Options(bool? flag) { _flag = flag; }

            public int Reads { get; private set; }

            public bool? UseFlag
            {
                get { Reads++; return _flag; }
            }
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

            public static string Run()
            {
                var sb = new StringBuilder();
                foreach (var options in new Options[] { null, new Options(false), new Options(true) })
                {
                    sb.Append(Probe(options, false)).Append(';');
                }

                var skipped = new Options(false);
                sb.Append(Probe(skipped, true)).Append(skipped.Reads);
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void BooleanTrue_In_ShortCircuit_Right_Is_TwoValued_And_ShortCircuits()
    {
        var expected = Invoke(ShortCircuitRightSource);
        var (output, diagnostics, changed) = RunRule(ShortCircuitRightSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.DoesNotContain(diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);

        // `null == true` is false exactly like `false == true`; the RHS needs
        // no temporary and stays unevaluated when the left arm short-circuits.
        Assert.Contains("HasValue", output);
        Assert.DoesNotContain("__cs2j_n_", output);
        Assert.DoesNotContain("==true", Compact(output));
        Assert.DoesNotContain("!=true", Compact(output));

        Assert.Equal(expected, Invoke(output));
        // The trailing 0 is the skipped probe: `skip` short-circuits before
        // the UseFlag getter runs.
        Assert.Equal("N;N;Y;Y0", Invoke(output));
    }

    // ── S1: bool? == true where the live arm is a non-nullable bool ──

    private const string CastLeafSource = """
        public sealed class Box
        {
            public int Reads;

            public bool Compute()
            {
                Reads++;
                return true;
            }
        }

        public static class Demo
        {
            private static readonly Box box = new Box();

            private static string Probe(bool skip, bool flag)
            {
                // The outer distribution cannot consume the nested conditional
                // arm directly; the innermost `(bool?)e` leaf folds to `e`, so
                // Compute() still runs exactly once.
                return skip || (box != null ? (flag ? (bool?)box.Compute() : null) : null) == true
                    ? "Y"
                    : "N";
            }

            public static string Run() => Probe(false, true) + box.Reads;
        }
        """;

    [Fact]
    public void BooleanTrue_Over_Non_Nullable_Bool_Arm_Reads_Once()
    {
        var expected = Invoke(CastLeafSource);
        var (output, diagnostics, changed) = RunRule(CastLeafSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        // `(bool?)e` is never null, so `(bool?)e == true` is exactly `e`.
        var compact = Compact(output);
        Assert.Contains(
            "skip||(box!=null?(flag?box.Compute():false):false)?\"Y\":\"N\"", compact);
        Assert.DoesNotContain("HasValue", compact);

        Assert.Equal(expected, Invoke(output));
        Assert.Equal("Y1", Invoke(output));
    }

    // ── S1: bool? == true in a field initializer (no statement anchor) ──

    private const string FieldInitializerSource = """
        public sealed class Options
        {
            public bool? CaseInsensitive { get; init; }
        }

        public static class Config
        {
            public static Options? Current { get; set; }

            public static bool Enabled =
                (Current != null ? Current.CaseInsensitive : null) == true;
        }

        public static class Demo
        {
            public static string Run() => Config.Enabled.ToString();
        }
        """;

    [Fact]
    public void BooleanTrue_In_Field_Initializer_Distributes_Without_Anchor()
    {
        var (output, diagnostics, changed) = RunRule(FieldInitializerSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.DoesNotContain(diagnostics,
            d => d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);

        var compact = Compact(output);
        Assert.Contains("Current!=null?", compact);
        Assert.Contains("CaseInsensitive.HasValue", compact);
        Assert.DoesNotContain("__cs2j_n_", output);

        Assert.Equal("False", Invoke(output));
    }

    // ── S1 negative: == false is NOT two-valued (null flips the result) ──

    private const string EqualsFalseSource = """
        public sealed class Options
        {
            public Options(bool? flag) { UseFlag = flag; }

            public bool? UseFlag { get; }
        }

        public static class Demo
        {
            private static string Probe(Options options, bool skip)
            {
                if (skip || (options != null ? options.UseFlag : null) == false)
                {
                    return "Y";
                }

                return "N";
            }

            public static string Run() => Probe(new Options(true), false);
        }
        """;

    [Fact]
    public void BooleanFalse_Literal_Stays_On_The_Hoisting_Path()
    {
        var (_, diagnostics, changed) = RunRule(EqualsFalseSource);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }

    // ── S2: `else if (A && B)` with an else clause ──

    private const string ElseIfConjunctionSource = """
        using System.Text;

        public enum Mode { Off = 0, On = 1 }

        public sealed class Spec
        {
            private readonly Mode? _mode;

            public Spec(Mode? mode) { _mode = mode; }

            public int Reads { get; private set; }

            public Mode? Mode
            {
                get { Reads++; return _mode; }
            }
        }

        public static class Demo
        {
            private static string Probe(Spec? spec, bool first)
            {
                if (first)
                {
                    return "f";
                }
                else if (spec != null && spec.Mode != Mode.On)
                {
                    return "n";
                }
                else
                {
                    return "o";
                }
            }

            public static string Run()
            {
                var sb = new StringBuilder();
                var specs = new Spec?[] { null, new Spec(Mode.On), new Spec(Mode.Off) };
                foreach (var spec in specs)
                {
                    var result = Probe(spec, false);
                    sb.Append(result).Append(spec == null ? 0 : spec.Reads).Append(';');
                }

                sb.Append(Probe(new Spec(Mode.Off), true));
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void Conjunction_In_ElseIf_With_Else_Keeps_Order_Frequency_And_Else()
    {
        var expected = Invoke(ElseIfConjunctionSource);
        var (output, diagnostics, changed) = RunRule(ElseIfConjunctionSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        // The flagged linearization keeps the else clause on the outer
        // statement and hoists the property read inside the A-guarded block.
        var compact = Compact(output);
        Assert.Contains("__cs2j_n_sc", compact);
        Assert.Contains("__cs2j_n_a", compact);
        Assert.DoesNotContain("spec.Mode!=Mode.On", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        // null → A false → else; On → B false → else; Off → B true; first
        // short-circuits before reading Mode.
        Assert.Equal("o0;o1;n1;f", actual);
    }

    // ── S2: plain `if (A && B)` without else, multi-component prefix ──

    private const string IfConjunctionSource = """
        using System.Text;

        public enum Mode { Off = 0, On = 1, Always = 2 }

        public sealed class Spec
        {
            private readonly Mode? _mode;

            public Spec(Mode? mode) { _mode = mode; }

            public int Reads { get; private set; }

            public Mode? Mode
            {
                get { Reads++; return _mode; }
            }
        }

        public static class Demo
        {
            private static string Probe(Spec spec, bool first, bool second)
            {
                if (first && second && spec.Mode != Mode.Always)
                {
                    return "n";
                }

                return "a";
            }

            public static string Run()
            {
                var sb = new StringBuilder();
                var spec = new Spec(Mode.On);
                sb.Append(Probe(spec, true, true)).Append(spec.Reads).Append(';');
                sb.Append(Probe(spec, true, false)).Append(spec.Reads).Append(';');
                sb.Append(Probe(spec, false, true)).Append(spec.Reads).Append(';');
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void Conjunction_If_Without_Else_Nests_And_Preserves_Frequency()
    {
        var expected = Invoke(IfConjunctionSource);
        var (output, diagnostics, changed) = RunRule(IfConjunctionSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        Assert.Contains("var__cs2j_n_a", compact);
        Assert.DoesNotContain("spec.Mode!=Mode.Always", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        // Mode is read only when both prefix conditions hold (1 then 1 then 0
        // cumulative reads across the three probes).
        Assert.Equal("n1;a1;a1;", actual);
    }
}
