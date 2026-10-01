using System.Text.RegularExpressions;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Unit tests for C17 M0 (complex increment/decrement target normalization),
/// co-located inside the C15 numeric-boundary pass and executed first as the
/// "increment-targets" family rule (Order=10). M0 ships the rule seam and the
/// read-only four-tuple classifier only: every source must pass through with
/// zero rewrite, zero diagnostics and byte-identical output; these tests lock
/// the direct-map whitelist and the (Target, Core, Liftability) classification.
/// </summary>
public class NumericBoundaryIncrementTargetUnitTests
{
    private static NumericBoundaryOptions RuleOptions(bool enabled = true, bool strict = true) =>
        new() { EnableIncrementTargets = enabled, Strict = strict };

    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(
        string src,
        bool fullFrameworkReferences = false)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var references = new List<MetadataReference>();
        if (fullFrameworkReferences)
        {
            var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            if (!string.IsNullOrEmpty(trustedAssemblies))
            {
                foreach (var assembly in trustedAssemblies
                             .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (File.Exists(assembly))
                    {
                        references.Add(MetadataReference.CreateFromFile(assembly));
                    }
                }
            }
        }

        if (references.Count == 0)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        var compilation = CSharpCompilation.Create("c17-increment-target-test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    private static NumericBoundaryFileResult Run(
        string src,
        NumericBoundaryOptions? options = null,
        bool fullFrameworkReferences = false)
    {
        var (tree, model) = ParseWithSemantics(src, fullFrameworkReferences);
        return new NumericBoundaryPreprocessor().Rewrite(src, model, options ?? RuleOptions());
    }

    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");

    private static void AssertOutputCompiles(string code, bool fullFrameworkReferences = false)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var references = new List<MetadataReference>();
        if (fullFrameworkReferences)
        {
            var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            if (!string.IsNullOrEmpty(trustedAssemblies))
            {
                foreach (var assembly in trustedAssemblies
                             .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (File.Exists(assembly))
                    {
                        references.Add(MetadataReference.CreateFromFile(assembly));
                    }
                }
            }
        }

        if (references.Count == 0)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        var compilation = CSharpCompilation.Create("c17-output-compiles",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "C17 output must compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static (IncrementTargetAnalyzer Analyzer, SyntaxNode Root) BuildClassifier(
        string src,
        bool fullFrameworkReferences = false)
    {
        var (tree, model) = ParseWithSemantics(src, fullFrameworkReferences);
        var shared = new NumericBoundaryAnalyzer(model, projectDefaultChecked: false);
        var analyzer = new IncrementTargetAnalyzer(model, RuleOptions(), shared);
        return (analyzer, tree.GetRoot());
    }

    private static IncrementTargetClassification SingleClassification(string src, bool fullFrameworkReferences = false)
    {
        var (analyzer, root) = BuildClassifier(src, fullFrameworkReferences);
        var sites = analyzer.FindAllSites(root);
        Assert.Equal(1, sites.Count);
        return sites[0].C;
    }

    // ── (a) direct-map zero-harm: representative cross-product ──

    [Fact]
    public void DirectMapTargets_AllShapes_PrefixAndPostfix_ByteIdenticalAndUnchanged()
    {
        var src = """
            public class C
            {
                private int _if;
                private static long _sf;
                private static char _sc;
                private sbyte _sb;
                private short _ss;

                public void M(int pi, long pl, sbyte psb, short ps, char pc)
                {
                    int i = 0;
                    long l = 0;
                    sbyte sb = 0;
                    short s = 0;
                    char c = '\0';
                    i++; ++i; i--; --i;
                    l++; --l;
                    sb++; --sb;
                    s++; s--;
                    c++; c--;
                    pi++; pl--; psb++; ps--; pc++;
                    _if++; --_if;
                    _sf++; _sf--;
                    _sc++;
                    _sb++; _ss--;
                    int[] a1 = new int[3];
                    long[] a2 = new long[3];
                    sbyte[] a3 = new sbyte[3];
                    short[] a4 = new short[3];
                    char[] a5 = new char[3];
                    a1[0]++; --a1[1]; a2[0]++; a3[0]--; ++a4[0]; a5[0]++;
                    long[,] md = new long[2, 2];
                    md[0, 1]++; --md[1, 0];
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── (b) skeleton: owned shapes are still untouched in M0 ──

    [Fact]
    public void OwnedSites_SkeletonM0_AllUntouchedWithoutErrors()
    {
        var src = """
            public enum Color { Red, Green, Blue }

            public struct Counting
            {
                public static Counting operator ++(Counting v) => default;
                public static Counting operator --(Counting v) => default;
            }

            public class C
            {
                public int P { get; set; }
                public int this[int i] { get => 0; set { } }

                public void M()
                {
                    byte b = 0;
                    ushort us = 0;
                    uint u = 0;
                    ulong ul = 0;
                    b++; b--; us++; u--; ul++;
                    P++; P--;
                    this[0]++; --this[1];
                    Color e = Color.Red;
                    e++; e--;
                    decimal d = 0m;
                    d++; d--;
                    nint n = 0;
                    nuint un = 0;
                    n++; un--;
                    int? x = 1;
                    x++; x--;
                    Counting k = default;
                    k++; k--;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── (c) family gate ──

    private const string PropertyIncrementSource = """
        public class C
        {
            public int P { get; set; }

            void M()
            {
                P++;
            }
        }
        """;

    [Fact]
    public void Gate_KillSwitchOff_EarlyCleanSkip()
    {
        var result = Run(PropertyIncrementSource, RuleOptions(enabled: false));

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(PropertyIncrementSource, result.OutputCode);
    }

    [Fact]
    public void Gate_Enabled_NoEarlyReturnButStillCleanAndUnchanged()
    {
        var result = Run(PropertyIncrementSource, RuleOptions(enabled: true));

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(PropertyIncrementSource, result.OutputCode);
    }

    // ── (d) four-tuple classification ──

    [Fact]
    public void Classify_IntLocal_Is_NotOwned_DirectMap()
    {
        var c = SingleClassification("public class C { void M() { int x = 0; x++; } }");
        Assert.Equal(TargetKind.NotOwned, c.Target);
        Assert.Equal(ArithmeticCore.DirectMap, c.Core);
    }

    [Fact]
    public void Classify_ByteLocal_Is_SimpleDirect_UnsignedDomain()
    {
        var c = SingleClassification("public class C { void M() { byte x = 0; x++; } }");
        Assert.Equal(TargetKind.SimpleDirect, c.Target);
        Assert.Equal(ArithmeticCore.UnsignedDomain, c.Core);
    }

    [Fact]
    public void Classify_UintLocal_Is_SimpleDirect_UnsignedDomain()
    {
        var c = SingleClassification("public class C { void M() { uint x = 0; x--; } }");
        Assert.Equal(TargetKind.SimpleDirect, c.Target);
        Assert.Equal(ArithmeticCore.UnsignedDomain, c.Core);
    }

    [Fact]
    public void Classify_IntProperty_Is_Property_BuiltinNarrow()
    {
        var src = """
            public class Probe { public int IntProp { get; set; } }
            public class C { Probe p; void M() { p.IntProp++; } }
            """;
        var c = SingleClassification(src);
        Assert.Equal(TargetKind.Property, c.Target);
        Assert.Equal(ArithmeticCore.BuiltinNarrow, c.Core);
        Assert.NotNull(c.Property);
    }

    [Fact]
    public void Classify_IntIndexer_Is_Indexer_BuiltinNarrow()
    {
        var src = """
            public class Probe { public int this[int i] { get => 0; set { } } }
            public class C { Probe p; void M() { p[0]++; } }
            """;
        var c = SingleClassification(src);
        Assert.Equal(TargetKind.Indexer, c.Target);
        Assert.Equal(ArithmeticCore.BuiltinNarrow, c.Core);
        Assert.NotNull(c.Property);
    }

    [Fact]
    public void Classify_EnumLocal_Is_SimpleDirect_EnumCast()
    {
        var src = "public enum E { A, B } public class C { void M() { E e = E.A; e++; } }";
        var c = SingleClassification(src);
        Assert.Equal(TargetKind.SimpleDirect, c.Target);
        Assert.Equal(ArithmeticCore.EnumCast, c.Core);
    }

    [Fact]
    public void Classify_DecimalLocal_Is_SimpleDirect_Decimal()
    {
        var c = SingleClassification("public class C { void M() { decimal d = 0m; d++; } }");
        Assert.Equal(TargetKind.SimpleDirect, c.Target);
        Assert.Equal(ArithmeticCore.Decimal, c.Core);
    }

    [Fact]
    public void Classify_NintLocal_Is_SimpleDirect_NativeInt()
    {
        var c = SingleClassification("public class C { void M() { nint n = 0; n++; } }");
        Assert.Equal(TargetKind.SimpleDirect, c.Target);
        Assert.Equal(ArithmeticCore.NativeInt, c.Core);
    }

    [Fact]
    public void Classify_NullableIntLocal_Is_SimpleDirect_LiftedNullable()
    {
        var c = SingleClassification("public class C { void M() { int? x = 1; x++; } }");
        Assert.Equal(TargetKind.SimpleDirect, c.Target);
        Assert.Equal(ArithmeticCore.LiftedNullable, c.Core);
    }

    [Fact]
    public void Classify_CustomOperatorLocal_Is_SimpleDirect_CustomOp()
    {
        var src = """
            public struct K { public static K operator ++(K v) => default; }
            public class C { void M() { K k = default; k++; } }
            """;
        var c = SingleClassification(src);
        Assert.Equal(TargetKind.SimpleDirect, c.Target);
        Assert.Equal(ArithmeticCore.CustomOp, c.Core);
    }

    [Fact]
    public void Classify_IntArrayElement_Is_NotOwned_DirectMap()
    {
        var src = "public class C { void M(int[] a, int i) { a[i]++; } }";
        var c = SingleClassification(src);
        Assert.Equal(TargetKind.NotOwned, c.Target);
        Assert.Equal(ArithmeticCore.DirectMap, c.Core);
    }

    [Fact]
    public void Classify_EnumArrayElement_Is_ArrayElementSpecial_EnumCast()
    {
        var src = "public enum E { A, B } public class C { void M(E[] a, int i) { a[i]++; } }";
        var c = SingleClassification(src);
        Assert.Equal(TargetKind.ArrayElementSpecial, c.Target);
        Assert.Equal(ArithmeticCore.EnumCast, c.Core);
    }

    [Fact]
    public void Classify_FieldLikeEventInsideClass_Is_Event()
    {
        // Baseline-illegal input (CS0079): the event branch is defensive; the
        // semantic model may surface the event either as the bound symbol or as
        // a candidate symbol, and the classifier covers both paths.
        var src = """
            public class C
            {
                public event System.Action E;

                void M()
                {
                    E++;
                }
            }
            """;
        var c = SingleClassification(src);
        Assert.Equal(TargetKind.Event, c.Target);
    }

    [Fact]
    public void Classify_Liftability_ConditionSite_Is_NoAnchor()
    {
        var src = """
            public class Probe { public int IntProp { get; set; } }
            public class C { Probe p; void M() { if (p.IntProp++ > 0) { } } }
            """;
        var c = SingleClassification(src);
        Assert.Equal(Liftability.NoAnchor, c.Lift);
    }

    [Fact]
    public void Classify_Liftability_StandaloneStatement_Is_Standalone()
    {
        var src = """
            public class Probe { public int IntProp { get; set; } }
            public class C { Probe p; void M() { p.IntProp++; } }
            """;
        var c = SingleClassification(src);
        Assert.Equal(Liftability.Standalone, c.Lift);
    }

    [Fact]
    public void Classify_Liftability_ReturnValue_Is_LiftablePostfix()
    {
        var src = """
            public class Probe { public int IntProp { get; set; } }
            public class C { Probe p; int M() { return p.IntProp++; } }
            """;
        var c = SingleClassification(src);
        Assert.Equal(Liftability.LiftablePostfix, c.Lift);
    }

    [Fact]
    public void Classify_Liftability_PrefixReturnValue_Is_InlinePrefix()
    {
        var src = """
            public class Probe { public int IntProp { get; set; } }
            public class C { Probe p; int M() { return ++p.IntProp; } }
            """;
        var c = SingleClassification(src);
        Assert.Equal(Liftability.InlinePrefix, c.Lift);
    }

    [Fact]
    public void Classify_ExpressionTreeLambdaSite_Is_ExpressionTree()
    {
        var src = """
            using System;
            using System.Linq.Expressions;
            public class C
            {
                int x;

                void M()
                {
                    Expression<Func<int>> e = () => x++;
                }
            }
            """;
        var c = SingleClassification(src, fullFrameworkReferences: true);
        Assert.Equal(ArithmeticCore.ExpressionTree, c.Core);
    }

    [Fact]
    public void Classify_DynamicLocal_Is_Dynamic()
    {
        var src = "public class C { void M() { dynamic d = 1; d++; } }";
        var c = SingleClassification(src, fullFrameworkReferences: true);
        Assert.Equal(ArithmeticCore.Dynamic, c.Core);
    }

    // ── (e) M0 idempotency (no rewrite, but the rule must be stable) ──

    [Fact]
    public void SecondRun_IsAlsoByteIdentical()
    {
        var src = """
            public enum Color { Red, Green, Blue }
            public class C
            {
                public int P { get; set; }

                public void M()
                {
                    byte b = 0;
                    b++;
                    P++;
                    Color e = Color.Red;
                    e++;
                    decimal d = 0m;
                    d++;
                }
            }
            """;

        var first = Run(src);
        var second = Run(first.OutputCode);

        Assert.False(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(src, first.OutputCode);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void Statistics_FreshBuckets_AreZeroAndDictionariesAlive()
    {
        var stats = new IncrementTargetStatistics();

        Assert.Equal(0, stats.TotalSites);
        Assert.Equal(0, stats.EventSites);
        Assert.Empty(stats.TempsByRole);
        Assert.Empty(stats.DeferredByReason);

        var result = Run(PropertyIncrementSource);
        Assert.Equal(0, result.Statistics.IncrementTargets.TotalSites);
    }
}
