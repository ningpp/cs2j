using System.Text.RegularExpressions;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;
using CSharpToJava.Core.Preprocessing;
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
    // Family isolation: these tests assert the C17 expanded shape (e.g. decimal
    // sites become 'old ± 1M'); C34 reduction is a separate rule exercised by
    // NumericBoundaryDecimalOpUnitTests (including the C17→C34 composition case).
    private static NumericBoundaryOptions RuleOptions(bool enabled = true, bool strict = true) =>
        new() { EnableIncrementTargets = enabled, EnableDecimalOps = false, Strict = strict };

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

    private static void AssertOutputCompiles(string code, bool fullFrameworkReferences = true)
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

        // C17 emits Cs2jUnsigned.UnsignedMath.Add/Subtract calls for uint/ulong
        // residuals, so the rewritten output always resolves against the support
        // library (the test project has a P2P reference to it).
        references.Add(MetadataReference.CreateFromFile(typeof(Cs2jUnsigned.UnsignedMath).Assembly.Location));

        var compilation = CSharpCompilation.Create("c17-output-compiles",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0,
            "C17 output must compile:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors)
            + Environment.NewLine + "── output ──" + Environment.NewLine + code);
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

    // ── (b) M1: owned shapes expand to read-modify-write ──

    [Fact]
    public void OwnedSites_M1_ExpandRmw()
    {
        // M2 (C17 milestone 2) owns decimal/nint/nullable/custom-op sites and
        // reports/rolls them back, so this M1 cross-product keeps only M1
        // shapes; M2 categories have dedicated tests below.
        var src = """
            public enum Color { Red, Green, Blue }

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
                }
            }
            """;

        var result = Run(src);
        var stats = result.Statistics.IncrementTargets;

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        // 5 unsigned residuals + 2 properties + 2 indexers + 2 enum simples.
        Assert.Equal(5, stats.UnsignedResidualSites);
        Assert.Equal(2, stats.PropertySites);
        Assert.Equal(2, stats.IndexerSites);
        Assert.Equal(2, stats.EnumCastSites);
        Assert.Equal(11, stats.TotalSites);
        Assert.Equal(2, stats.UnsignedMathCallSites);

        var compact = Compact(result.OutputCode);
        Assert.Contains("__cs2j_inc_", compact);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.SubtractUInt32", compact);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt64", compact);
        Assert.Contains("(byte)(b+1)", compact);
        Assert.Contains("(ushort)(us+1)", compact);
        Assert.Contains("(global::Color)(int)((int)e+1)", compact);
        Assert.Contains("(global::Color)(int)((int)e-1)", compact);
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
    public void Gate_Enabled_PropertyStandalone_IsRewritten()
    {
        var result = Run(PropertyIncrementSource, RuleOptions(enabled: true));

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Equal(1, result.Statistics.IncrementTargets.PropertySites);
        Assert.Contains("P=P+1", Compact(result.OutputCode));
        AssertOutputCompiles(result.OutputCode);
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

    // ── (e) M1 idempotency: the expanded form is a fixed point ──

    [Fact]
    public void SecondRun_OnExpandedOutput_IsStableFixedPoint()
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

        // All owned ++/-- (including decimal, owned since M2) are gone after
        // run one; the expanded assignment form is itself a fixed point.
        Assert.True(first.Changed);
        // All sites here are standalone statements, so no temps are needed;
        // the fixed-point signature is the expanded assignment form instead.
        Assert.Contains("P=P+1", Compact(first.OutputCode));
        Assert.Contains("d=d+1M", Compact(first.OutputCode));
        Assert.False(second.Changed);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.DoesNotContain(second.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
    }

    [Fact]
    public void Statistics_FreshBuckets_AreZeroAndDictionariesAlive()
    {
        var stats = new IncrementTargetStatistics();

        Assert.Equal(0, stats.TotalSites);
        Assert.Equal(0, stats.EventSites);
        Assert.Equal(0, stats.UnsignedMathCallSites);
        Assert.Empty(stats.TempsByRole);
        Assert.Empty(stats.DeferredByReason);

        var result = Run(PropertyIncrementSource);
        Assert.Equal(1, result.Statistics.IncrementTargets.TotalSites);
    }

    // ── (f) M1 IT-1~6 / IT-10 / IT-11 expansion shapes ──

    private static string WrapInMethod(string body, string returnType = "void") => $$"""
        public enum Color : byte { Red, Green, Max = 255 }
        public class Probe
        {
            public int IntProp { get; set; }
            public uint UIntProp { get; set; }
            public Color ColorProp { get; set; }
            public byte ByteProp { get; set; }
            public int _field;
            public Probe p;
            public Probe q;
            public int this[int i] { get => 0; set { } }
            public int this[int i, int j] { get => 0; set { } }
            public int Next() => 1;
            public int G() => 1;
            public Probe GetObj() => this;
            public void Use(int x, int y) { }

            {{returnType}} M(Color[] colors, int i)
            {
                {{body}}
            }
        }
        """;

    [Fact]
    public void IT1_PropertyStandalone_ImplicitAndQualified_ExpandsBareArithmetic()
    {
        var result = Run(WrapInMethod("IntProp++; --p.IntProp;"));

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.IncrementTargets.PropertySites);
        var compact = Compact(result.OutputCode);
        // Implicit access stays implicit; qualified simple-name receiver is
        // not hoisted (it cannot observe a side effect).
        Assert.Contains("IntProp=IntProp+1", compact);
        Assert.Contains("p.IntProp=p.IntProp-1", compact);
        Assert.DoesNotContain(compact, "++");
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT2_EffectfulReceiver_FieldAndProperty_EvaluatedOnce()
    {
        var result = Run(WrapInMethod("GetObj()._field++; GetObj().IntProp++;"));

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_r1=GetObj();", compact);
        Assert.Contains("__cs2j_inc_r1._field=__cs2j_inc_r1._field+1", compact);
        Assert.Contains("var__cs2j_inc_r2=GetObj();", compact);
        Assert.Contains("__cs2j_inc_r2.IntProp=__cs2j_inc_r2.IntProp+1", compact);
        // The factory is called once per site: two hoisted call occurrences
        // plus its one declaration in the fixture (never re-read in the write).
        Assert.Equal(3, Regex.Matches(result.OutputCode, "GetObj\\(\\)").Count);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT2_NestedPropertyReceiver_IsAlsoHoistedOnce()
    {
        // a.B shape is hoisted even though syntactically a pure member access:
        // B may itself be a property with an observable getter.
        var result = Run(WrapInMethod("p.GetObj().IntProp++;"));

        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_r1=p.GetObj();", compact);
        Assert.Equal(1, Regex.Matches(result.OutputCode, "p\\.GetObj\\(\\)").Count);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT3_Indexer_ReceiverAndArguments_EvaluatedOnceInSourceOrder()
    {
        var result = Run(WrapInMethod("p[Next(), 1]++; p[Next(), G()]--;"));

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.IncrementTargets.IndexerSites);
        var compact = Compact(result.OutputCode);
        // 'p' is a bare simple-name receiver: it cannot observe a side effect,
        // so no receiver temp is allocated (only arguments are hoisted).
        Assert.DoesNotContain(compact, "__cs2j_inc_r");
        Assert.Contains("var__cs2j_inc_a1_0=Next();", compact);
        Assert.Contains("var__cs2j_inc_a1_1=1;", compact);
        // Two hoisted Next() argument occurrences plus the one Next()
        // declaration in the fixture — each argument is evaluated exactly once.
        Assert.Equal(3, Regex.Matches(result.OutputCode, "\\bNext\\(\\)").Count);
        // Second site keeps source argument order (Next before G).
        Assert.True(compact.IndexOf("__cs2j_inc_a2_0=Next()") < compact.IndexOf("__cs2j_inc_a2_1=G()"));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT3_ThisIndexer_OmitsReceiverTemp()
    {
        var src = """
            public class C
            {
                public int this[int i] { get => 0; set { } }
                void M(int i) { this[i]++; }
            }
            """;
        var result = Run(src);

        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_a1_0=i;", compact);
        Assert.DoesNotContain(compact, "__cs2j_inc_r");
        Assert.Contains("this[__cs2j_inc_a1_0]=this[__cs2j_inc_a1_0]+1", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT4_EnumArrayElement_IndexHoisted_CastCore()
    {
        var result = Run(WrapInMethod("colors[i]++; colors[0]--;"));

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.IncrementTargets.SpecialArrayElementSites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_i1_0=i;", compact);
        Assert.Contains("colors[__cs2j_inc_i1_0]=(global::Color)(byte)((byte)colors[__cs2j_inc_i1_0]+1)", compact);
        Assert.Contains("var__cs2j_inc_i2_0=0;", compact);
        // Simple literal indices are still hoisted (uniform single-evaluation
        // shape) but the array name is never captured.
        Assert.DoesNotContain(compact, "__cs2j_inc_r");
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT5_UnsignedResiduals_HelperForWideDomains_CastForNarrow()
    {
        var result = Run(WrapInMethod("uint u = 0; ulong ul = 0; u++; --u; ul++; UIntProp--; ByteProp++;"));

        var stats = result.Statistics.IncrementTargets;
        // u++, --u, ul++ are simple residuals; UIntProp-- and ByteProp++ are
        // member targets (uint property helper, byte property narrow cast).
        Assert.Equal(3, stats.UnsignedResidualSites);
        Assert.Equal(2, stats.PropertySites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("u=(uint)Cs2jUnsigned.UnsignedMath.AddUInt32((int)u,1)", compact);
        Assert.Contains("u=(uint)Cs2jUnsigned.UnsignedMath.SubtractUInt32((int)u,1)", compact);
        Assert.Contains("ul=(ulong)Cs2jUnsigned.UnsignedMath.AddUInt64((long)ul,1)", compact);
        Assert.Contains("(byte)(", compact);
        Assert.Equal(4, stats.UnsignedMathCallSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT6_SimpleEnumLocal_ThreeLayerCastCore()
    {
        var src = """
            public enum Color { Red, Green, Blue }
            public class C { void M() { Color e = Color.Red; e++; --e; } }
            """;
        var result = Run(src);

        var compact = Compact(result.OutputCode);
        Assert.Contains("e=(global::Color)(int)((int)e+1)", compact);
        Assert.Contains("e=(global::Color)(int)((int)e-1)", compact);
        Assert.Equal(2, result.Statistics.IncrementTargets.EnumCastSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT10_PrefixValueConsumed_InlinedAssignmentWithParens()
    {
        var result = Run(WrapInMethod("Use(++p.IntProp, ++p.ByteProp);"));

        var stats = result.Statistics.IncrementTargets;
        Assert.Equal(2, stats.PrefixInlineSites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("(p.IntProp=p.IntProp+1)", compact);
        Assert.Contains("(p.ByteProp=(byte)(p.ByteProp+1))", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT10_PrefixReturn_HoistsReceiverThenReturnsAssignment()
    {
        var result = Run(WrapInMethod("return ++GetObj().IntProp;", "int"));

        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_r1=GetObj();", compact);
        Assert.Contains("return(__cs2j_inc_r1.IntProp=__cs2j_inc_r1.IntProp+1);", compact);
        Assert.Equal(2, Regex.Matches(result.OutputCode, "GetObj\\(\\)").Count);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT11_PostfixValueConsumed_OldValueLiftThenWriteThenUse()
    {
        var result = Run(WrapInMethod("int x = p.IntProp++; Use(x, p.IntProp--);"));

        var stats = result.Statistics.IncrementTargets;
        Assert.Equal(2, stats.PostfixLiftedSites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_o1=p.IntProp;", compact);
        Assert.Contains("p.IntProp=__cs2j_inc_o1+1;", compact);
        Assert.Contains("intx=__cs2j_inc_o1;", compact);
        Assert.Contains("var__cs2j_inc_o2=p.IntProp;", compact);
        Assert.Contains("p.IntProp=__cs2j_inc_o2-1;", compact);
        Assert.Contains("Use(x,__cs2j_inc_o2)", compact);
        Assert.Equal(2, stats.TempsByRole[IncrementTargetDiagnostics.TempRoleOldValue]);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void IT11_PostfixStandalone_DoesNotAllocateOldValueTemp()
    {
        var result = Run(WrapInMethod("p.IntProp++;"));

        Assert.Equal(1, result.Statistics.IncrementTargets.PostfixStandaloneSites);
        Assert.Equal(0, result.Statistics.IncrementTargets.PostfixLiftedSites);
        Assert.DoesNotContain(result.Statistics.IncrementTargets.TempsByRole,
            pair => pair.Key == IncrementTargetDiagnostics.TempRoleOldValue);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── (g) decision-14 gates and illegal targets ──

    [Fact]
    public void Gate_ReadOnlyProperty_5332_Error_RollsBack()
    {
        var src = """
            public class C
            {
                public int P { get; }
                void M() { P++; }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetIllegalTarget);
    }

    [Fact]
    public void Gate_ConditionPosition_5339_Error_RollsBack()
    {
        var src = """
            public class C
            {
                public int P { get; set; }
                void M() { if (P++ > 0) { } }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void Gate_LambdaBody_5339_Error_RollsBack()
    {
        var src = """
            using System;
            public class C
            {
                public int P { get; set; }
                void M() { Action a = () => { P++; }; }
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void Gate_SimpleEnumFieldInitializer_5339_Error_RollsBack()
    {
        var src = """
            public enum Color { Red, Green }
            public class C
            {
                static Color e = Color.Red;
                Color f = e++;
            }
            """;
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void Gate_ForIncrementor_5339_Error_RollsBack()
    {
        var result = Run(WrapInMethod("for (int i = 0; i < 1; p.IntProp++) { }"));

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void Gate_DisjointForeignEffectBeforeSite_5339()
    {
        // G() is evaluated before the postfix site; hoisting the write before
        // the anchor would reorder G against the property setter.
        var result = Run(WrapInMethod("Use(G(), p.IntProp++);"));

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void Gate_TwoPostfixSitesSameAnchor_5339()
    {
        var result = Run(WrapInMethod("Use(p.IntProp++, q.IntProp++);"));

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void Gate_PostfixNotLeftmost_5339()
    {
        // Prefix first, postfix second: the old-value read cannot win.
        var result = Run(WrapInMethod("Use(++p.IntProp, q.IntProp++);"));

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void Gate_MultiplePureInlinePrefixes_Allowed()
    {
        var result = Run(WrapInMethod("Use(++p.IntProp, ++q.IntProp);"));

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        var compact = Compact(result.OutputCode);
        Assert.Contains("(p.IntProp=p.IntProp+1)", compact);
        Assert.Contains("(q.IntProp=q.IntProp+1)", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Gate_PostfixFirstThenPurePrefix_Allowed()
    {
        var result = Run(WrapInMethod("Use(p.IntProp++, ++q.IntProp);"));

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_o1=p.IntProp;", compact);
        Assert.Contains("Use(__cs2j_inc_o1,(q.IntProp=q.IntProp+1))", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Gate_EnclosingCallIsNotForeign_PostfixArgumentExpands()
    {
        var result = Run(WrapInMethod("Use(p.IntProp++, 0);"));

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── (h) checked region: C17 bare arithmetic is helperized by the C15 sweep ──

    [Fact]
    public void Checked_UIntProperty_BareArithmeticTakenOverByC15()
    {
        var result = Run(WrapInMethod("checked { UIntProp++; }"));

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        // C17 emits bare uint +1 inside checked; the C15 sweep (Order=20)
        // converts it to the checked unsigned helper.
        Assert.Contains("AddCheckedUInt32", Compact(result.OutputCode));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Checked_EnumProperty_BareCastCoreSurvivesSweep()
    {
        var result = Run(WrapInMethod("checked { ColorProp++; }"));

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        var compact = Compact(result.OutputCode);
        // The C15 checked sweep helperizes the normalized Int32 arithmetic;
        // M1's outer (E) cast survives, so the result is still assigned via
        // an explicit enum conversion.
        Assert.Contains("(global::Color)(Cs2jUnsigned.UnsignedMath.AddCheckedInt32((int)ColorProp,1))", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Checked_SimpleByteLocal_LeftToC15_NotOwnedByC17()
    {
        var result = Run(WrapInMethod("checked { byte b = 0; b++; }"));

        var stats = result.Statistics.IncrementTargets;
        Assert.Equal(0, stats.PropertySites + stats.IndexerSites + stats.UnsignedResidualSites);
        Assert.DoesNotContain(result.OutputCode, "__cs2j_inc_");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
    }

    // ── (i) info diagnostics & static members ──

    [Fact]
    public void Info_5330_5331_Aggregated_WhenSitesExpanded()
    {
        var result = Run(WrapInMethod("p.IntProp++; int x = p.UIntProp++;"));

        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Info
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetSiteInfo);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Info
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetTempInfo);
    }

    [Fact]
    public void StaticProperty_ImplicitAndTypeQualified_ExpandsWithoutReceiverTemp()
    {
        var src = """
            public class C
            {
                public static int SP { get; set; }
                void M() { SP++; C.SP++; }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.OutputCode, "__cs2j_inc_r");
        AssertOutputCompiles(result.OutputCode);
    }

    // ── (j) M2 IT-7: decimal expands to old ± 1m (CS2J5334 Info, C34 owns Java) ──

    [Fact]
    public void M2_Decimal_Local_PrefixAndPostfix_ExpandsToOneMLiteral()
    {
        var src = """
            public class C
            {
                void M()
                {
                    decimal d = 0m;
                    d++; --d;
                }
            }
            """;

        var result = Run(src);
        var stats = result.Statistics.IncrementTargets;

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Equal(2, stats.DecimalSites);
        Assert.Equal(2, stats.TotalSites);
        Assert.Equal(0, stats.PropertySites + stats.IndexerSites + stats.SpecialArrayElementSites);

        var compact = Compact(result.OutputCode);
        // Roslyn normalizes the generated literal token to an uppercase M.
        Assert.Contains("d=d+1M", compact);
        Assert.Contains("d=d-1M", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_Decimal_PropertyAndIndexer_CountCoreAndShapeBuckets()
    {
        var src = """
            public class C
            {
                public decimal DP { get; set; }
                public decimal this[int i] { get => 0m; set { } }

                void M()
                {
                    DP++;
                    this[0]--;
                }
            }
            """;

        var result = Run(src);
        var stats = result.Statistics.IncrementTargets;

        Assert.True(result.Changed);
        Assert.Equal(2, stats.DecimalSites);
        Assert.Equal(1, stats.PropertySites);
        Assert.Equal(1, stats.IndexerSites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("DP=DP+1M", compact);
        Assert.Contains("-1M", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_Decimal_ArrayElement_HoistsIndex_ExpandsToOneM()
    {
        var src = """
            public class C
            {
                void M(decimal[] a, int i)
                {
                    a[i]++;
                    a[1]--;
                }
            }
            """;

        var result = Run(src);
        var stats = result.Statistics.IncrementTargets;

        Assert.True(result.Changed);
        Assert.Equal(2, stats.DecimalSites);
        Assert.Equal(2, stats.SpecialArrayElementSites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_i1_0=i;", compact);
        Assert.Contains("a[__cs2j_inc_i1_0]=a[__cs2j_inc_i1_0]+1M", compact);
        Assert.Contains("-1M", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_Decimal_PostfixValueConsumed_LiftsOldValueTemp()
    {
        var src = """
            public class C
            {
                void M()
                {
                    decimal d = 0m;
                    object o = d++;
                }
            }
            """;

        var result = Run(src);

        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_inc_o1=d;", compact);
        Assert.Contains("d=__cs2j_inc_o1+1M", compact);
        Assert.Contains("objecto=__cs2j_inc_o1", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_Decimal_PrecisionProbeLiteral_28FractionalDigits_PreservedThroughRewrite()
    {
        // Task-book precision probe: 27 zeros then 1 (1e-28m). A 2026-10-01
        // compile/run probe verified the value is preserved and survives
        // (1m + 1e-28m); runtime equality is exercised by the sample fixture.
        var src = """
            public class C
            {
                void M()
                {
                    decimal tiny = 0.0000000000000000000000000001m;
                    decimal one = 1m;
                    one = one + tiny;
                    one++;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("0.0000000000000000000000000001m", result.OutputCode);
        Assert.Contains("one=one+1M", Compact(result.OutputCode));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_Decimal_5334_OneAggregatedInfoPerFile_NoneWithoutDecimalSites()
    {
        var withDecimal = Run("public class C { void M() { decimal d = 0m; d++; d--; } }");

        var info5334 = withDecimal.Diagnostics
            .Where(d => d.Severity == NumericBoundarySeverity.Info
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetDecimalInfo)
            .ToList();
        Assert.Single(info5334);
        Assert.Contains("2 decimal increment/decrement site(s)", info5334[0].Message);

        var withoutDecimal = Run(PropertyIncrementSource);
        Assert.DoesNotContain(withoutDecimal.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.IncrementTargetDecimalInfo);
    }

    [Fact]
    public void M2_Decimal_SecondRun_IsIdempotentFixedPoint()
    {
        var first = Run("public class C { void M() { decimal d = 0m; d++; } }");
        var second = Run(first.OutputCode);

        Assert.True(first.Changed);
        Assert.Equal(1, first.Statistics.IncrementTargets.DecimalSites);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Equal(0, second.Statistics.IncrementTargets.DecimalSites);
    }

    [Fact]
    public void M2_Decimal_C34Off_LeavesBinaryAddOneM_C34On_ReducesToDecimalAdd()
    {
        var src = "public class C { void M() { decimal d = 0m; d++; } }";

        var off = Run(src, new NumericBoundaryOptions
        {
            EnableIncrementTargets = true,
            EnableDecimalOps = false,
            Strict = true,
        });
        var offCompact = Compact(off.OutputCode);
        Assert.Contains("d=d+1M", offCompact);
        Assert.DoesNotContain("decimal.Add", offCompact);
        AssertOutputCompiles(off.OutputCode);

        var on = Run(src, new NumericBoundaryOptions
        {
            EnableIncrementTargets = true,
            EnableDecimalOps = true,
            Strict = true,
        });
        var onCompact = Compact(on.OutputCode);
        // C34 (Order=30, rebound model) sees the normal binary + decimal
        // binding and reduces it to the static call, parenthesized.
        Assert.Contains("(decimal.Add(d,1M))", onCompact);
        Assert.DoesNotContain("d=d+1M", onCompact);
        // The IT-7 site counting and CS2J5334 Info still come from rule 10.
        Assert.Equal(1, on.Statistics.IncrementTargets.DecimalSites);
        Assert.Contains(on.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Info
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetDecimalInfo);
        AssertOutputCompiles(on.OutputCode);
    }

    // ── (k) M2 IT-8/10: owned-but-unsupported cores — Error + NB-12 rollback ──

    [Fact]
    public void M2_NativeInt_LocalAndProperty_5333_Error_RollsBack()
    {
        var src = """
            using System;

            public class C
            {
                public nint NP { get; set; }

                void M()
                {
                    nint n = 0;
                    nuint un = 0;
                    IntPtr p = 0;
                    UIntPtr up = 0;
                    n++; un--; p++; up++;
                    NP++;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        var errors = result.Diagnostics
            .Where(d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetNativeInt)
            .ToList();
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public void M2_NullableLifted_5335_Error_RollsBack_WhenC22Disabled()
    {
        // The C22 NL-10/11 handoff owns lifted RMW sites when EnableNullable;
        // CS2J5335 remains the C17 behavior only with C22 switched off.
        var src = """
            public enum Color { Red, Green }
            public class C
            {
                void M()
                {
                    int? x = 1;
                    Color? e = Color.Red;
                    x++;
                    e--;
                }
            }
            """;
        var options = new NumericBoundaryOptions
        {
            EnableNullable = false,
            EnableIncrementTargets = true,
            EnableDecimalOps = false,
            Strict = true,
        };

        var result = Run(src, options);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(2, result.Diagnostics.Count(d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNullableLifted));
    }

    [Fact]
    public void M2_Dynamic_LocalAndMember_5336_Error_RollsBack()
    {
        var src = """
            public class C2 { }
            public class C
            {
                void M()
                {
                    dynamic d = 1;
                    d++;
                    dynamic o = new C2();
                    o.Foo++;
                }
            }
            """;

        var result = Run(src, fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(2, result.Diagnostics.Count(d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetDynamic));
    }

    [Fact]
    public void M2_ExpressionTree_5337_Error_RollsBack()
    {
        // Baseline CS0832 makes this unreachable from compilable input; the
        // branch is defensive and the erroneous tree still classifies.
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

        var result = Run(src, fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetExpressionTree);
    }

    [Fact]
    public void M2_CustomOperator_LocalPropertyIndexer_5338_Error_RollsBackAndCountsSites()
    {
        var src = """
            public struct K
            {
                public static K operator ++(K v) => default;
                public static K operator --(K v) => default;
            }
            public class C
            {
                public K KP { get; set; }
                public K this[int i] { get => default; set { } }

                void M()
                {
                    K k = default;
                    k++; k--;
                    KP++;
                    this[0]++;
                }
            }
            """;

        var result = Run(src);
        var stats = result.Statistics.IncrementTargets;

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(4, result.Diagnostics.Count(d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetCustomOperator));
        // The hit counter increments on the true site even though NB-12
        // rolls the whole file back (same convention as EventSites).
        Assert.Equal(4, stats.CustomOperatorSites);
    }

    [Fact]
    public void M2_CustomOperator_DesugaredContractHook_IsAlwaysUnavailable()
    {
        // Decision 11 extension point: a 2026-10-01 probe proved hand-authored
        // static Increment/Decrement methods never bind ++/--, so no desugared
        // method can ever be resolved until C14 freezes the naming contract.
        Assert.False(IncrementTargetAnalyzer.TryGetDesugaredIncrementContract(null, out var resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void M2_EnumFromValue_FingerprintHook_ClassifiesButIsNotOwnedAndPassesThrough()
    {
        var src = """
            public struct Money
            {
                public int Value { get; }
                public static Money FromValue(int value) => default;
            }
            public class C
            {
                void M()
                {
                    Money v = default;
                    v++;
                }
            }
            """;

        var classification = SingleClassification(src);
        Assert.Equal(ArithmeticCore.EnumFromValue, classification.Core);
        Assert.Equal(TargetKind.SimpleDirect, classification.Target);
        Assert.False(IncrementTargetAnalyzer.IsOwnedByRule(classification));

        var result = Run(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
    }

    // ── (l) M2 IT-9: ref-returning targets — non-strict alias rewrite / strict rollback ──

    private const string RefReturningSource = """
        public class C
        {
            private int _x;
            private static int _s;
            private int[] _a = new int[4];

            public ref int P => ref _x;
            public static ref int SP => ref _s;
            public ref int this[int i] => ref _a[i];
            public ref int Get() => ref _x;
            public ref int GetAt(int i) => ref _a[i];
            public int Next() => 0;

            void M()
            {
                P++;
                SP++;
                this[Next()]++;
                Get()++;
                GetAt(Next())--;
            }
        }
        """;

    [Fact]
    public void M2_RefReturning_NonStrict_AllShapes_RewrittenViaRefAlias_With5340Warning()
    {
        var result = Run(RefReturningSource, RuleOptions(strict: false));
        var stats = result.Statistics.IncrementTargets;

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Equal(5, stats.RefAliasSites);
        Assert.Equal(5, stats.TempsByRole[IncrementTargetDiagnostics.TempRoleRefAlias]);

        var warnings = result.Diagnostics
            .Where(d => d.Severity == NumericBoundarySeverity.Warning
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetRefReturn)
            .ToList();
        Assert.Equal(5, warnings.Count);

        var compact = Compact(result.OutputCode);
        // Implicit/static ref properties: alias over the bare invocation shape.
        Assert.Contains("refint__cs2j_inc_f1=refP;", compact);
        Assert.Contains("refint__cs2j_inc_f2=refSP;", compact);
        // Ref indexer: index arg hoisted first, alias evaluated exactly once.
        Assert.Contains("var__cs2j_inc_a3_0=Next();", compact);
        Assert.Contains("refint__cs2j_inc_f3=refthis[__cs2j_inc_a3_0];", compact);
        // Ref method with no args / with an effectful by-value arg.
        Assert.Contains("refint__cs2j_inc_f4=refGet();", compact);
        Assert.Contains("var__cs2j_inc_a5_0=Next();", compact);
        Assert.Contains("refint__cs2j_inc_f5=refGetAt(__cs2j_inc_a5_0);", compact);
        Assert.Contains("__cs2j_inc_f5=__cs2j_inc_f5-1", compact);
        // Each Next() call is evaluated once: one declaration plus the two
        // call sites, both hoisted into temps — three textual occurrences.
        Assert.Equal(3, Regex.Matches(result.OutputCode, "\\bNext\\(\\)").Count);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_RefReturning_NonStrict_PostfixValueConsumed_LiftsOldValueAfterAlias()
    {
        var src = """
            public class C
            {
                private int _x;
                public ref int P => ref _x;

                void M()
                {
                    int v = P++;
                }
            }
            """;

        var result = Run(src, RuleOptions(strict: false));

        var compact = Compact(result.OutputCode);
        Assert.Contains("refint__cs2j_inc_f1=refP;", compact);
        Assert.Contains("var__cs2j_inc_o1=__cs2j_inc_f1;", compact);
        Assert.Contains("__cs2j_inc_f1=__cs2j_inc_o1+1;", compact);
        Assert.Contains("intv=__cs2j_inc_o1;", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_RefReturning_Strict_5340_Error_RollsBack()
    {
        var src = """
            public class C
            {
                private int _x;
                public ref int P => ref _x;

                void M()
                {
                    P++;
                }
            }
            """;

        var result = Run(src, RuleOptions(strict: true));

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetRefReturn);
        Assert.Equal(0, result.Statistics.IncrementTargets.RefAliasSites);
    }

    [Fact]
    public void M2_RefReadonly_PropertyIndexerMethod_5332_Error_RollsBack()
    {
        var src = """
            public class C
            {
                private int _x;
                private int[] _a = new int[2];

                public ref readonly int P => ref _x;
                public ref readonly int this[int i] => ref _a[i];
                public ref readonly int Get() => ref _x;

                void M()
                {
                    P++;
                    this[0]++;
                    Get()++;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(3, result.Diagnostics.Count(d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetIllegalTarget));
        Assert.Equal(0, result.Statistics.IncrementTargets.RefAliasSites);
    }

    [Fact]
    public void M2_RefReturning_AliasTempNameCollision_AddsNumericSuffix()
    {
        var src = """
            public class C
            {
                private int _x;
                public ref int P => ref _x;

                void M()
                {
                    int __cs2j_inc_f1 = 0;
                    P++;
                }
            }
            """;

        var result = Run(src, RuleOptions(strict: false));

        var compact = Compact(result.OutputCode);
        Assert.Contains("refint__cs2j_inc_f1_1=refP;", compact);
        Assert.Contains("__cs2j_inc_f1_1=__cs2j_inc_f1_1+1", compact);
        AssertOutputCompiles(result.OutputCode);
    }
}
