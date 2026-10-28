using System.Reflection;
using System.Text.RegularExpressions;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.Nullable;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBOp = Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Unit tests for C22 M0 (lifted <c>Nullable&lt;T&gt;</c> explicitization),
/// co-located inside the numeric-boundary pass as the "nullable-lift" family
/// rule (Order=15, 2026-10-02 implementation correction). M0 ships the rule
/// seam and the read-only semantic classifier only: every source passes
/// through with zero rewrite, zero diagnostics and byte-identical output;
/// these tests lock the Nullable&lt;T&gt; predicates, the conversion quadrants,
/// the target shapes and the NL-14 residual scanner skeleton.
/// </summary>
public class NumericBoundaryNullableLiftUnitTests
{
    // Family isolation for zero-harm runs: C17 still owns the LiftedNullable
    // handoff (CS2J5335) until M2, so increment sites run with C17 disabled
    // here; coexistence with C17/C15 enabled is covered separately below.
    private static NumericBoundaryOptions RuleOptions(bool enabled = true, bool strict = true) =>
        new()
        {
            EnableNullable = enabled,
            EnableIncrementTargets = false,
            EnableDecimalOps = false,
            Strict = strict,
        };

    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(
        string src,
        bool fullFrameworkReferences = false,
        IEnumerable<MetadataReference>? extraReferences = null)
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

        if (extraReferences != null)
        {
            references.AddRange(extraReferences);
        }

        if (references.Count == 0)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        var compilation = CSharpCompilation.Create("c22-nullable-lift-test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    private static void AssertNoCompileErrors(SemanticModel model)
    {
        var errors = model.SyntaxTree.GetDiagnostics()
            .Concat(model.Compilation.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0,
            "test input must compile without errors:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors));
    }

    private static NumericBoundaryFileResult Run(
        string src,
        NumericBoundaryOptions? options = null,
        bool fullFrameworkReferences = false,
        IEnumerable<MetadataReference>? extraReferences = null)
    {
        var (_, model) = ParseWithSemantics(src, fullFrameworkReferences, extraReferences);
        return new NumericBoundaryPreprocessor().Rewrite(src, model, options ?? RuleOptions());
    }

    private static void AssertOutputCompiles(
        string code,
        bool fullFrameworkReferences = true,
        MetadataReference? extraReference = null)
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

        references.Add(MetadataReference.CreateFromFile(typeof(Cs2jUnsigned.UnsignedMath).Assembly.Location));
        if (extraReference != null)
        {
            references.Add(extraReference);
        }

        var compilation = CSharpCompilation.Create("c22-output-compiles",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0,
            "C22 output must compile:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors)
            + Environment.NewLine + "── output ──" + Environment.NewLine + code);
    }

    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");

    // ── runtime semantics harness: emit original vs rewritten sources and
    // invoke them so the guard truth tables are verified against the C#
    // compiler's own lifted-operator semantics (not just text) ──

    private static List<MetadataReference> RuntimeReferences()
    {
        var references = new List<MetadataReference>();
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        foreach (var assembly in (trustedAssemblies ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(assembly))
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        references.Add(MetadataReference.CreateFromFile(typeof(Cs2jUnsigned.UnsignedMath).Assembly.Location));
        return references;
    }

    private static Assembly CompileAndLoad(string code) => CompileAndLoad(code, null);

    private static Assembly CompileAndLoad(
        string code, IEnumerable<MetadataReference>? extraReferences)
    {
        var compilation = CSharpCompilation.Create(
            "c22-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(code) },
            extraReferences is null ? RuntimeReferences() : RuntimeReferences().Concat(extraReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "test harness emit failed:" + Environment.NewLine
            + string.Join(Environment.NewLine, emit.Diagnostics.Select(d => d.ToString()))
            + Environment.NewLine + code);
        return Assembly.Load(ms.ToArray());
    }

    private static object? InvokeStatic(Assembly assembly, string method, params object?[] args)
    {
        var type = assembly.GetType("C")!;
        try
        {
            return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>
    /// Run a two-argument lifted expression in both the ORIGINAL and the C22
    /// rewritten assembly and assert identical results (boxed value or null).
    /// </summary>
    private static void AssertSemanticEquivalence2(
        string expression, string typeKeyword, object? a, object? b,
        NumericBoundaryOptions? options = null)
    {
        var source = $$"""
            public class C
            {
                public static object? F({{typeKeyword}}? a, {{typeKeyword}}? b)
                    => (object?)({{expression}});
            }
            """;

        var originalResult = InvokeStatic(CompileAndLoad(source), "F", a, b);

        var rewritten = Run(source, options ?? RuleOptions()).OutputCode;
        AssertOutputCompiles(rewritten);
        var rewrittenResult = InvokeStatic(CompileAndLoad(rewritten), "F", a, b);

        Assert.Equal(originalResult, rewrittenResult);
    }

    /// <summary>One-argument form for lifted unary truth tables.</summary>
    private static void AssertSemanticEquivalence1(
        string expression, string typeKeyword, object? a)
    {
        var source = $$"""
            public class C
            {
                public static object? F({{typeKeyword}}? a)
                    => (object?)({{expression}});
            }
            """;

        var originalResult = InvokeStatic(CompileAndLoad(source), "F", a);

        var rewritten = Run(source).OutputCode;
        AssertOutputCompiles(rewritten);
        var rewrittenResult = InvokeStatic(CompileAndLoad(rewritten), "F", a);

        Assert.Equal(originalResult, rewrittenResult);
    }

    private sealed record Harness(
        NullableLiftAnalyzer Analyzer,
        SemanticModel Model,
        CompilationUnitSyntax Root);

    private static Harness BuildHarness(string src, bool fullFrameworkReferences = false)
    {
        var (tree, model) = ParseWithSemantics(src, fullFrameworkReferences);
        AssertNoCompileErrors(model);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var shared = new NumericBoundaryAnalyzer(model, projectDefaultChecked: false);
        var analyzer = new NullableLiftAnalyzer(model, RuleOptions(), shared);
        return new Harness(analyzer, model, root);
    }

    private static ITypeSymbol LocalType(Harness h, string name)
    {
        var declarator = h.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .First(v => v.Identifier.Text == name);
        return Assert.IsAssignableFrom<ILocalSymbol>(h.Model.GetDeclaredSymbol(declarator)).Type;
    }

    private static T FirstNode<T>(SyntaxNode root) where T : SyntaxNode =>
        root.DescendantNodes().OfType<T>().First();

    // ── (a) M0 zero-harm cross-product: all lifted shapes pass through ──

    private const string LiftedCrossProductSource = """
        public enum Color { Red, Green, Blue }

        public class C
        {
            public void M()
            {
                int? ni = 1, nj = 2;
                long? nl = 3L;
                float? nf = 1f;
                double? nd = 2.0;
                uint? nu = 4u;
                bool? b1 = true, b2 = false;
                Color? e1 = Color.Red, e2 = Color.Green;

                var add = ni + nj;
                var sub = nl - 1L;
                var mul = nf * 2f;
                var div = nd / 2.0;
                var uadd = nu + 1u;
                var eand = e1 & e2;

                int? neg = -ni;
                long? plus = +nl;
                Color? enot = ~e1;
                bool? lnot = !b1;

                bool rel1 = ni < nj;
                bool rel2 = nu >= 3u;
                bool eq1 = ni == nj;
                bool eq2 = ni == null;
                bool eq3 = b1 == true;
                bool neq = b1 != b2;

                bool? band = b1 & b2;
                bool? bor = b1 | b2;
                bool? bxor = b1 ^ b2;

                int g1 = ni.GetValueOrDefault();
                int g2 = ni.GetValueOrDefault(5);

                int? x = 3;
                int y = (int)x;
                int? z = (int?)nl;
                x++;
                ++x;
                x -= 1;
            }
        }
        """;

    [Fact]
    public void M2_AllLiftedShapes_M1AndM2SitesExplicitized_IncrementCompoundGuarded()
    {
        var result = Run(LiftedCrossProductSource);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != NumericBoundarySeverity.Info);

        var compact = Compact(result.OutputCode);
        // M1-owned shapes are guarded:
        Assert.Contains("ni.HasValue&&nj.HasValue?(int?)(ni.Value+nj.Value):(int?)null", compact);
        Assert.Contains("(long?)(nl.Value-1L)", compact);
        Assert.Contains("(float?)(nf.Value*2f)", compact);
        Assert.Contains("(double?)(nd.Value/2.0)", compact);
        Assert.Contains("(uint?)(nu.Value+1u)", compact);
        Assert.Contains("ni.HasValue?(int?)(-ni.Value):(int?)null", compact);
        Assert.Contains("nl.HasValue?(long?)(+nl.Value):(long?)null", compact);
        Assert.Contains("b1.HasValue?(bool?)(!b1.Value):(bool?)null", compact);
        Assert.Contains("ni.HasValue&&nj.HasValue&&ni.Value<nj.Value", compact);
        Assert.Contains("nu.HasValue&&nu.Value>=3u", compact);
        Assert.Contains("(!ni.HasValue)", compact);
        Assert.Contains("b1.HasValue&&b1.Value==true", compact);
        Assert.Contains("b1.HasValue&&!b1.Value||b2.HasValue&&!b2.Value", compact);
        Assert.Contains("(bool?)(b1.Value^b2.Value)", compact);

        // NL-6 GetValueOrDefault (both shapes):
        Assert.Contains("g1=(ni.HasValue?ni.Value:default(int))", compact);
        Assert.Contains("g2=(ni.HasValue?ni.Value:5)", compact);
        Assert.DoesNotContain("GetValueOrDefault", compact);

        // NL-7/8 conversions:
        Assert.Contains("y=x.Value", compact);
        Assert.Contains("z=(nl.HasValue?(int?)((int)(nl.Value)):(int?)null)", compact);

        // NL-9 enum backstop (int underlying, so no unchecked wrap):
        Assert.Contains(
            "eand=(e1.HasValue&&e2.HasValue?(global::Color?)((global::Color)((int)(e1.Value)&(int)(e2.Value))):(global::Color?)null)",
            compact);
        Assert.Contains(
            "enot=(e1.HasValue?(global::Color?)((global::Color)((int)(~((int)(e1.Value))))):(global::Color?)null)",
            compact);

        // NL-10/11 increment/compound are explicitized: postfix keeps the old
        // value in an `o` temp and splices the write before the anchor;
        // prefix and compound render as plain assignments.
        Assert.Contains("var__cs2j_n_o1=x", compact);
        Assert.Contains("x=(__cs2j_n_o1.HasValue?(int?)(__cs2j_n_o1.Value+1):(int?)null)", compact);
        Assert.Contains("x=(x.HasValue?(int?)(x.Value+1):(int?)null)", compact);
        Assert.Contains("x=(x.HasValue?(int?)(x.Value-1):(int?)null)", compact);
        Assert.DoesNotContain("x++", compact);
        Assert.DoesNotContain("++x", compact);
        Assert.DoesNotContain("x-=1", compact);

        // The postfix old-value temp is the only hoisted temp in this file.
        var stats = result.Statistics.Nullable;
        Assert.Equal(5, stats.LiftedBinarySites);
        Assert.Equal(2, stats.LiftedUnarySites);
        Assert.Equal(2, stats.LiftedRelationalSites);
        Assert.Equal(1, stats.LiftedEqualityNullLiteralSites);
        Assert.Equal(1, stats.LiftedEqualityOneSidedSites);
        Assert.Equal(2, stats.LiftedEqualityBothNullableSites);
        Assert.Equal(1, stats.LiftedBoolLogicAndSites);
        Assert.Equal(1, stats.LiftedBoolLogicOrSites);
        Assert.Equal(1, stats.LiftedBoolLogicXorSites);
        Assert.Equal(1, stats.LiftedBoolLogicNotSites);
        Assert.Equal(2, stats.GetValueOrDefaultSites);
        Assert.Equal(1, stats.UnwrapConversions);
        Assert.Equal(1, stats.LiftedConversions);
        Assert.Equal(2, stats.NullableEnumBackstopSites);
        Assert.Equal(1, stats.LiftedIncrementPostfixSites);
        Assert.Equal(1, stats.LiftedIncrementPrefixSites);
        Assert.Equal(1, stats.LiftedIncrementCompoundSites);
        Assert.Equal(1, stats.TempsLifted);
        Assert.False(stats.DeferredByReason.Keys.Any(k =>
            k.StartsWith(PreprocessDiagnosticCodes.NullableLiftResidual, StringComparison.Ordinal)));
        Assert.Equal(1, result.Statistics.FilesTransformed);
    }

    [Fact]
    public void M2_DecimalNullable_ArithmeticGuarded_BareInnerKept_CompoundGuarded()
    {
        // Decision 19: decimal? arithmetic gets the HasValue guard while
        // the INNER bare decimal op stays for the C34 downstream sweep;
        // NL-11 lifted compound assignment gets the same guard treatment.
        var src = """
            public class C
            {
                public void M()
                {
                    decimal? a = 1m, b = 2m;
                    decimal? c = a + b;
                    c *= 3m;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity != NumericBoundarySeverity.Info);
        var compact = Compact(result.OutputCode);
        Assert.Contains("a.HasValue&&b.HasValue?(decimal?)(a.Value+b.Value):(decimal?)null", compact);
        Assert.Contains("c=(c.HasValue?(decimal?)(c.Value*3m):(decimal?)null)", compact);
        Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementCompoundSites);
    }

    [Fact]
    public void M0_KillSwitchOff_RuleDoesNotRun_OutputIdentical()
    {
        var result = Run(LiftedCrossProductSource, RuleOptions(enabled: false));

        Assert.False(result.Changed);
        Assert.Equal(LiftedCrossProductSource, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void M0_ParseOnlyInput_NoSemanticModel_NoOp()
    {
        var result = new NumericBoundaryPreprocessor()
            .Rewrite(LiftedCrossProductSource, semanticModel: null, RuleOptions());

        Assert.False(result.Changed);
        Assert.Equal(LiftedCrossProductSource, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    // ── (b) Nullable<T> detection and domain classification ──

    [Fact]
    public void Classify_NullableUnwrap_AnyUnderlyingT()
    {
        var src = """
            public enum E { A, B }
            public class C
            {
                public void M()
                {
                    int? i = 0;
                    bool? b = true;
                    E? e = E.A;
                    int nonN = 0;
                }
            }
            """;
        var h = BuildHarness(src);

        Assert.True(NullableLiftAnalyzer.TryUnwrapNullable(LocalType(h, "i"), out var ti));
        Assert.Equal(SpecialType.System_Int32, ti!.SpecialType);
        Assert.True(NullableLiftAnalyzer.TryUnwrapNullable(LocalType(h, "b"), out var tb));
        Assert.Equal(SpecialType.System_Boolean, tb!.SpecialType);
        Assert.True(NullableLiftAnalyzer.TryUnwrapNullable(LocalType(h, "e"), out var te));
        Assert.Equal(TypeKind.Enum, te!.TypeKind);
        Assert.False(NullableLiftAnalyzer.TryUnwrapNullable(LocalType(h, "nonN"), out _));

        Assert.Equal(NullableValueDomain.Integer, NullableLiftAnalyzer.ClassifyDomain(LocalType(h, "i")));
        Assert.Equal(NullableValueDomain.Bool, NullableLiftAnalyzer.ClassifyDomain(LocalType(h, "b")));
        Assert.Equal(NullableValueDomain.Enum, NullableLiftAnalyzer.ClassifyDomain(LocalType(h, "e")));
        Assert.Equal(NullableValueDomain.NotNullable, NullableLiftAnalyzer.ClassifyDomain(LocalType(h, "nonN")));
    }

    [Theory]
    [InlineData("sbyte?", "sbyte x = 0; sbyte? n = x;", (int)NullableValueDomain.Integer)]
    [InlineData("char?", "char x = 'a'; char? n = x;", (int)NullableValueDomain.Integer)]
    [InlineData("uint?", "uint x = 0; uint? n = x;", (int)NullableValueDomain.Unsigned)]
    [InlineData("ulong?", "ulong x = 0; ulong? n = x;", (int)NullableValueDomain.Unsigned)]
    [InlineData("float?", "float x = 0; float? n = x;", (int)NullableValueDomain.Floating)]
    [InlineData("double?", "double x = 0; double? n = x;", (int)NullableValueDomain.Floating)]
    [InlineData("decimal?", "decimal x = 0; decimal? n = x;", (int)NullableValueDomain.Decimal)]
    public void Classify_AllNumericDomains(string label, string locals, int expectedDomain)
    {
        var src = $"public class C {{ public void M() {{ {locals} }} }}";
        var h = BuildHarness(src);
        Assert.Equal((NullableValueDomain)expectedDomain,
            NullableLiftAnalyzer.ClassifyDomain(LocalType(h, "n")));
    }

    [Fact]
    public void Classify_C41Domains_NintAndInt128_AreSpecialNumeric()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    nint? n = 0;
                    System.Int128? big = 0;
                }
            }
            """;
        var h = BuildHarness(src, fullFrameworkReferences: true);

        Assert.Equal(NullableValueDomain.SpecialNumeric, NullableLiftAnalyzer.ClassifyDomain(LocalType(h, "n")));
        Assert.Equal(NullableValueDomain.SpecialNumeric, NullableLiftAnalyzer.ClassifyDomain(LocalType(h, "big")));
    }

    // ── (c) lifted binary/unary flavors and built-in vs user-defined ──

    [Fact]
    public void Classify_LiftedBinary_FlavorsAndOperandShapes()
    {
        var src = """
            public enum E { A, B }
            public class C
            {
                public void M()
                {
                    int? a = 1, b = 2;
                    uint? u = 1, v = 2;
                    bool? f = true, g = false;
                    E? e1 = E.A, e2 = E.B;

                    var add = a + b;
                    var oneSided = a + 1;
                    var rel = a < b;
                    var bothEq = a == b;
                    var oneEq = a == 1;
                    var nullEq = a == null;
                    var band = f & g;
                    var bor = f | g;
                    var bxor = f ^ g;
                    var uadd = u + v;
                    var eand = e1 | e2;
                }
            }
            """;
        var h = BuildHarness(src);
        var binaries = h.Root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Select(h.Analyzer.ClassifyBinary)
            .ToList();

        Assert.Equal(11, binaries.Count);

        // `a == null` binds as a non-lifted null check (IsLifted=false): the
        // compiler does not emit a lifted equality call there, so C22 never
        // owns the null-literal shape.
        Assert.Null(binaries[5]);
        var owned = binaries.Where(c => c != null).Select(c => c!).ToList();
        Assert.Equal(10, owned.Count);

        Assert.Equal(LiftedBinaryFlavor.Arithmetic, owned[0].Flavor);
        Assert.Equal(NullableValueDomain.Integer, owned[0].Domain);
        Assert.True(owned[0].IsBuiltInLifted);
        Assert.Null(owned[0].OperatorMethod);
        Assert.True(owned[0].LeftNullable && owned[0].RightNullable);

        Assert.True(owned[1].LeftNullable);
        Assert.False(owned[1].RightNullable);
        Assert.False(owned[1].HasNullLiteralOperand);

        Assert.Equal(LiftedBinaryFlavor.Relational, owned[2].Flavor);
        Assert.Equal(LiftedBinaryFlavor.Equality, owned[3].Flavor);
        Assert.True(owned[3].LeftNullable && owned[3].RightNullable);
        Assert.Equal(LiftedBinaryFlavor.Equality, owned[4].Flavor);
        Assert.False(owned[4].RightNullable);

        Assert.Equal(LiftedBinaryFlavor.BoolAnd, owned[5].Flavor);
        Assert.Equal(LiftedBinaryFlavor.BoolOr, owned[6].Flavor);
        Assert.Equal(LiftedBinaryFlavor.BoolXor, owned[7].Flavor);
        Assert.Equal(NullableValueDomain.Bool, owned[5].Domain);

        Assert.Equal(NullableValueDomain.Unsigned, owned[8].Domain);
        Assert.Equal(LiftedBinaryFlavor.Arithmetic, owned[9].Flavor);
        Assert.Equal(NullableValueDomain.Enum, owned[9].Domain);
    }

    [Fact]
    public void Classify_LiftedUnary_ArithmeticAndBoolNot()
    {
        var src = """
            public enum E { A }
            public class C
            {
                public void M()
                {
                    int? a = 1;
                    bool? f = true;
                    E? e = E.A;
                    var neg = -a;
                    var pos = +a;
                    var not = !f;
                    var tild = ~e;
                }
            }
            """;
        var h = BuildHarness(src);
        var unaries = h.Root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
            .Select(h.Analyzer.ClassifyUnary)
            .ToList();

        Assert.Equal(4, unaries.Count);
        Assert.All(unaries, c => Assert.NotNull(c));
        Assert.Equal(LiftedUnaryFlavor.Arithmetic, unaries[0]!.Flavor);
        Assert.Equal(LiftedUnaryFlavor.Arithmetic, unaries[1]!.Flavor);
        Assert.Equal(LiftedUnaryFlavor.BoolNot, unaries[2]!.Flavor);
        Assert.Equal(NullableValueDomain.Bool, unaries[2]!.Domain);
        Assert.Equal(LiftedUnaryFlavor.Arithmetic, unaries[3]!.Flavor);
        Assert.Equal(NullableValueDomain.Enum, unaries[3]!.Domain);
        Assert.All(unaries, c => Assert.True(c!.IsBuiltInLifted));
    }

    [Fact]
    public void Classify_LiftedDecimalBinary_IsBuiltInDespiteOperatorMethod()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    decimal? a = 1m, b = 2m;
                    var c = a + b;
                }
            }
            """;
        var h = BuildHarness(src);
        var binary = FirstNode<BinaryExpressionSyntax>(h.Root);
        var classification = h.Analyzer.ClassifyBinary(binary);

        Assert.NotNull(classification);
        Assert.Equal(NullableValueDomain.Decimal, classification!.Domain);
        Assert.Equal(LiftedBinaryFlavor.Arithmetic, classification.Flavor);
        // Roslyn 5 binds lifted decimal arithmetic without an operator method
        // symbol (non-lifted decimal carries BuiltinOperator); either form is
        // built-in owned, never CS2J5353.
        Assert.True(classification.IsBuiltInLifted);
    }

    [Fact]
    public void Classify_UserDefinedLiftedBinary_FlavorUserDefined()
    {
        var src = """
            public struct Vec
            {
                public int X;
                public static Vec operator +(Vec left, Vec right) => default;
            }
            public class C
            {
                public void M()
                {
                    Vec? a = default, b = default;
                    var c = a + b;
                }
            }
            """;
        var h = BuildHarness(src);
        var classification = h.Analyzer.ClassifyBinary(FirstNode<BinaryExpressionSyntax>(h.Root));

        Assert.NotNull(classification);
        Assert.Equal(LiftedBinaryFlavor.UserDefined, classification!.Flavor);
        Assert.False(classification.IsBuiltInLifted);
        Assert.NotNull(classification.OperatorMethod);
        Assert.Equal(MethodKind.UserDefinedOperator, classification.OperatorMethod!.MethodKind);
        Assert.Equal(NullableValueDomain.Other, classification.Domain);
    }

    // ── (d) conversion four quadrants (decision 13) ──

    [Fact]
    public void Classify_ConversionFourQuadrants()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    int v = 5;
                    int? x = 3;
                    long? l = 4L;
                    long l2 = 5L;

                    int? w1 = (int?)v;      // wrap T -> T?
                    int u1 = (int)x;       // unwrap T? -> T
                    int? w2 = (int?)l;     // lifted S? -> T?
                    int u2 = (int)l;       // unwrap + convert S? -> T
                    int? w3 = (int?)l2;    // lifted S -> T? (cross-domain)
                    int? w4 = (int?)x;     // identity T? -> T?
                }
            }
            """;
        var h = BuildHarness(src);
        var casts = h.Root.DescendantNodes().OfType<CastExpressionSyntax>()
            .Select(h.Analyzer.ClassifyConversion)
            .ToList();

        Assert.Equal(6, casts.Count);
        Assert.Equal(NullableConversionQuadrant.Wrap, casts[0].Quadrant);
        Assert.Equal(NullableConversionQuadrant.Unwrap, casts[1].Quadrant);
        Assert.Equal(NullableConversionQuadrant.Lifted, casts[2].Quadrant);
        Assert.Equal(NullableConversionQuadrant.UnwrapAndConvert, casts[3].Quadrant);
        Assert.Equal(NullableConversionQuadrant.Lifted, casts[4].Quadrant);
        Assert.Equal(NullableConversionQuadrant.None, casts[5].Quadrant);

        Assert.True(casts[1].IsLiftedBuiltIn);
        Assert.True(casts[2].IsLiftedBuiltIn);
        Assert.True(casts[3].IsLiftedBuiltIn);
        Assert.False(casts[0].IsLiftedBuiltIn);
    }

    [Fact]
    public void Classify_LiftedUserDefinedConversion_QuadrantUserDefined()
    {
        var src = """
            public struct UA { public static explicit operator UB(UA a) => default; }
            public struct UB { public int V; }
            public class C
            {
                public void M()
                {
                    UA? a = default;
                    var b = (UB?)a;
                }
            }
            """;
        var h = BuildHarness(src);
        var classification = h.Analyzer.ClassifyConversion(FirstNode<CastExpressionSyntax>(h.Root));

        Assert.Equal(NullableConversionQuadrant.UserDefined, classification.Quadrant);
        Assert.NotNull(classification.OperatorMethod);
        // Roslyn 5 labels user-defined conversion methods MethodKind.Conversion.
        Assert.Equal(MethodKind.Conversion, classification.OperatorMethod!.MethodKind);
        Assert.False(classification.IsLiftedBuiltIn);
    }

    // ── (e) lifted increment / compound targets (decisions 15/17) ──

    [Fact]
    public void Classify_LiftedIncrement_PostfixPrefixCompound_OnSimpleTargets()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    int? x = 1;
                    x++;
                    ++x;
                    x += 2;
                }
            }
            """;
        var h = BuildHarness(src);

        var postfix = FirstNode<PostfixUnaryExpressionSyntax>(h.Root);
        var postfixC = h.Analyzer.ClassifyLiftedTarget(postfix);
        Assert.NotNull(postfixC);
        Assert.True(postfixC!.IsIncrement);
        Assert.False(postfixC.IsPrefix);
        Assert.False(postfixC.IsCompound);
        Assert.Equal(NumericBoundaryAnalyzer.TargetShape.LocalOrParameter, postfixC.TargetShape);
        Assert.Equal(NullableValueDomain.Integer, postfixC.Domain);

        var prefix = FirstNode<PrefixUnaryExpressionSyntax>(h.Root);
        var prefixC = h.Analyzer.ClassifyLiftedTarget(prefix);
        Assert.True(prefixC!.IsPrefix);

        var compound = FirstNode<AssignmentExpressionSyntax>(h.Root);
        var compoundC = h.Analyzer.ClassifyLiftedTarget(compound);
        Assert.NotNull(compoundC);
        Assert.True(compoundC!.IsCompound);
        Assert.Equal(NumericBoundaryAnalyzer.TargetShape.LocalOrParameter, compoundC.TargetShape);
    }

    [Fact]
    public void Classify_LiftedIncrement_Field_IsSimple_Property_IsComplex()
    {
        var src = """
            public class C
            {
                int? _field;
                int? Prop { get; set; }

                public void M()
                {
                    _field++;
                    Prop++;
                }
            }
            """;
        var h = BuildHarness(src);
        var sites = h.Root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().ToList();

        Assert.Equal(NumericBoundaryAnalyzer.TargetShape.Field,
            h.Analyzer.ClassifyLiftedTarget(sites[0])!.TargetShape);
        Assert.Equal(NumericBoundaryAnalyzer.TargetShape.Complex,
            h.Analyzer.ClassifyLiftedTarget(sites[1])!.TargetShape);
    }

    // ── (f) dynamic / expression-tree (CS2J5352/CS2J5354) ──

    [Fact]
    public void Classify_DynamicLiftedSite_IsDynamic()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    dynamic a = 1;
                    dynamic b = 2;
                    var c = a + b;
                }
            }
            """;
        var h = BuildHarness(src, fullFrameworkReferences: true);
        var binary = FirstNode<BinaryExpressionSyntax>(h.Root);

        Assert.True(NullableLiftAnalyzer.IsDynamicOperation(h.Model.GetOperation(binary)));
    }

    [Fact]
    public void Classify_LiftedBinaryInsideExpressionTree_DetectedViaSharedAnalyzer()
    {
        var src = """
            using System;
            using System.Linq.Expressions;
            public class C
            {
                public void M()
                {
                    Expression<Func<int?, int?>> e = a => a + 1;
                }
            }
            """;
        var h = BuildHarness(src, fullFrameworkReferences: true);
        var binary = FirstNode<BinaryExpressionSyntax>(h.Root);

        var classification = h.Analyzer.ClassifyBinary(binary);
        Assert.NotNull(classification);
        Assert.True(classification!.IsLifted);
        Assert.True(h.Analyzer.IsWithinExpressionTree(binary));
    }

    // ── (g) E04 guard product is not a lifted operator node ──

    [Fact]
    public void Classify_E04TernaryProduct_IsConditional_NotLiftedAndNotResidual()
    {
        var src = """
            public enum E { A, B }
            public class C
            {
                public void M()
                {
                    E? a = E.A, b = E.B;
                    E? r = a.HasValue ? (E?)(E)(a.Value | b.Value) : (E?)null;
                    int? x = 1, y = 2;
                    int? q = x.HasValue ? (int?)(x.Value + y.Value) : (int?)null;
                }
            }
            """;
        var h = BuildHarness(src);

        var conditionals = h.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>().ToList();
        Assert.Equal(2, conditionals.Count);
        foreach (var conditional in conditionals)
        {
            Assert.True(NullableLiftAnalyzer.IsConditionalProductSyntax(conditional));
            Assert.True(NullableLiftAnalyzer.IsConditionalProduct(h.Model.GetOperation(conditional)));
            // A guard product never binds as a lifted binary site.
            Assert.False(h.Model.GetOperation(conditional) is NBOp.IBinaryOperation);
        }

        // The guard inputs are non-nullable inner ops; no residual lifted site
        // survives in a guard product (wraps are quadrant Wrap, not residual).
        var residuals = NullableLiftRule.ScanResiduals(h.Model, h.Root);
        Assert.Empty(residuals);
    }

    // ── (h) GetValueOrDefault ──

    [Fact]
    public void Classify_GetValueOrDefault_BothOverloads()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    int? x = 1;
                    var a = x.GetValueOrDefault();
                    var b = x.GetValueOrDefault(7);
                }
            }
            """;
        var h = BuildHarness(src);
        var calls = h.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

        Assert.Equal(2, calls.Count);
        Assert.True(h.Analyzer.IsGetValueOrDefault(calls[0], out var hasArg0));
        Assert.False(hasArg0);
        Assert.True(h.Analyzer.IsGetValueOrDefault(calls[1], out var hasArg1));
        Assert.True(hasArg1);
    }

    // ── (i) stable reference predicate (ported from E04) ──

    [Fact]
    public void Classify_StableReferences_OnlyLocalsParamsAndStableFieldChains()
    {
        var src = """
            public class Probe
            {
                public int? P { get; set; }
                public int? F;
                public static int? SF;
                public Probe? Q;
                public int?[] Arr = new int?[1];
                public Probe Get() => this;

                public void M(int? param, Probe? arg)
                {
                    int? local = 0;
                    Use(local, param, F, SF, Q.F, arg.F, P, Get().F, Arr[0]);
                }

                void Use(params int?[] args) { }
            }
            """;
        var h = BuildHarness(src);
        var arguments = FirstNode<InvocationExpressionSyntax>(h.Root).ArgumentList.Arguments
            .Select(a => a.Expression)
            .ToList();

        Assert.Equal(9, arguments.Count);
        // local, parameter, this.field, static field, field-chain via field receiver,
        // field-chain via parameter receiver → stable.
        for (var i = 0; i < 6; i++)
        {
            Assert.True(h.Analyzer.IsStableReference(arguments[i]), $"arg {i} should be stable: {arguments[i]}");
        }

        // property / method-result field / indexer → unstable.
        for (var i = 6; i < 9; i++)
        {
            Assert.False(h.Analyzer.IsStableReference(arguments[i]), $"arg {i} should be unstable: {arguments[i]}");
        }
    }

    // ── (j) NL-14 residual scanner skeleton ──

    [Fact]
    public void ResidualScanner_FindsBuiltInLiftedSites_GovAndUnwrap()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    int? a = 1, b = 2;
                    var add = a + b;
                    var g = a.GetValueOrDefault();
                    var u = (int)a;
                }
            }
            """;
        var h = BuildHarness(src);
        var residuals = NullableLiftRule.ScanResiduals(h.Model, h.Root);

        Assert.Contains(residuals, r => r.Kind == "binary");
        Assert.Contains(residuals, r => r.Kind == "get-value-or-default");
        Assert.Contains(residuals, r => r.Kind.StartsWith("conversion-", StringComparison.Ordinal));
    }

    [Fact]
    public void ResidualScanner_UserDefinedLifted_IsNotResidualBuiltIn()
    {
        var src = """
            public struct Vec
            {
                public int X;
                public static Vec operator +(Vec left, Vec right) => default;
            }
            public class C
            {
                public void M()
                {
                    Vec? a = default, b = default;
                    var c = a + b;
                }
            }
            """;
        var h = BuildHarness(src);
        var binary = FirstNode<BinaryExpressionSyntax>(h.Root);
        var operation = h.Model.GetOperation(binary);

        Assert.False(NullableLiftRule.IsResidualBuiltInLifted(operation!, out _));
        Assert.Empty(NullableLiftRule.ScanResiduals(h.Model, h.Root));
    }

    [Fact]
    public void ResidualScanner_SupportFingerprint_IsExempt()
    {
        var node = SyntaxFactory.ParseExpression("Cs2jUnsigned.UnsignedMath.AddCheckedInt32(x, y)");
        Assert.True(NullableLiftRule.IsSupportFingerprintExempt(node));

        var other = SyntaxFactory.ParseExpression("dotnet.System.Decimal.op_Addition(x, y)");
        Assert.True(NullableLiftRule.IsSupportFingerprintExempt(other));

        var plain = SyntaxFactory.ParseExpression("a + b");
        Assert.False(NullableLiftRule.IsSupportFingerprintExempt(plain));
    }

    // ── (k) temp naming / whitelist skeletons ──

    [Fact]
    public void SyntaxFacts_TempAllocator_RolesAndCollisionAvoidance()
    {
        var root = CSharpSyntaxTree.ParseText("""
            public class C
            {
                public void M()
                {
                    int __cs2j_n_a1 = 0;
                }
            }
            """).GetRoot();
        var allocator = new NullableLiftSyntaxFacts.TempNameAllocator(root);

        Assert.Equal("__cs2j_n_a1_1", allocator.Left(1));
        Assert.Equal("__cs2j_n_b1", allocator.Right(1));
        Assert.Equal("__cs2j_n_o2", allocator.Old(2));
        Assert.Equal("__cs2j_n_d3", allocator.Default(3));
        Assert.Equal("__cs2j_n_a1", new NullableLiftSyntaxFacts.TempNameAllocator(
            CSharpSyntaxTree.ParseText("public class C { }").GetRoot()).Left(1));
    }

    [Theory]
    [InlineData("x++;", true)]
    [InlineData("return x;", true)]
    [InlineData("throw ex;", true)]
    [InlineData("if (x) { }", false)]
    public void SyntaxFacts_AnchorWhitelist_Skeletons(string statement, bool whitelisted)
    {
        var node = SyntaxFactory.ParseStatement(statement);
        Assert.Equal(whitelisted, NullableLiftSyntaxFacts.IsWhitelistedAnchor(node));
    }

    // ── (l) statistics & diagnostics M0 contract ──

    [Fact]
    public void Statistics_FreshBuckets_AllZero_DictionariesAlive()
    {
        var stats = new NullableLiftStatistics();

        Assert.Equal(0, stats.LiftedBinarySites + stats.LiftedUnarySites
            + stats.LiftedRelationalSites + stats.LiftedEqualitySites
            + stats.LiftedBoolLogicSites + stats.GetValueOrDefaultSites
            + stats.UnwrapConversions + stats.LiftedConversions
            + stats.LiftedIncrementSites + stats.NullableEnumBackstopSites
            + stats.GuardedCheckedInnerSites + stats.TempsLifted);
        Assert.Empty(stats.DeferredByReason);
        Assert.Equal(0, stats.LiftedEqualityNullLiteralSites
            + stats.LiftedEqualityOneSidedSites + stats.LiftedEqualityBothNullableSites);
        Assert.Equal(0, stats.LiftedBoolLogicAndSites + stats.LiftedBoolLogicOrSites
            + stats.LiftedBoolLogicXorSites + stats.LiftedBoolLogicNotSites);
        Assert.Equal(0, stats.LiftedIncrementPrefixSites
            + stats.LiftedIncrementPostfixSites + stats.LiftedIncrementCompoundSites);
    }

    [Fact]
    public void Diagnostics_Templates_CarryCodeAndRuleTag()
    {
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftDynamic, NullableLiftDiagnostics.Dynamic("a + b"));
        Assert.Contains("[NL-13]", NullableLiftDiagnostics.Dynamic("a + b"));
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
            NullableLiftDiagnostics.UserDefinedOperator("a + b"));
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftExpressionTree,
            NullableLiftDiagnostics.ExpressionTree("a + b"));
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftComplexTarget,
            NullableLiftDiagnostics.ComplexTarget("P++"));
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftSpecialNumeric,
            NullableLiftDiagnostics.SpecialNumeric("x + y"));
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftResidual,
            NullableLiftDiagnostics.Residual("a + b"));
        Assert.Contains("[NL-14]", NullableLiftDiagnostics.Residual("a + b"));
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftSiteInfo,
            NullableLiftDiagnostics.SiteInfo(3, "3 binary"));
        Assert.Contains(PreprocessDiagnosticCodes.NullableLiftTempInfo,
            NullableLiftDiagnostics.TempInfo(2, "1 a, 1 b"));
    }

    // ── (m) coexistence: C15/C17 behavior unchanged with EnableNullable=true ──

    [Fact]
    public void Coexist_C15CheckedSweep_StillInsertsHelper()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    checked { int r = a + b; return r; }
                }
            }
            """;
        var options = new NumericBoundaryOptions
        {
            EnableNullable = true,
            EnableIncrementTargets = true,
            EnableDecimalOps = true,
            Strict = true,
        };
        var result = Run(src, options);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Contains("AddCheckedInt32(a, b)", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Coexist_C17PropertyIncrement_StillExpands()
    {
        var src = """
            public class C
            {
                public int P { get; set; }
                void M() { P++; }
            }
            """;
        var options = new NumericBoundaryOptions
        {
            EnableNullable = true,
            EnableIncrementTargets = true,
            EnableDecimalOps = false,
            Strict = true,
        };
        var result = Run(src, options);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Equal(1, result.Statistics.IncrementTargets.PropertySites);
        Assert.Contains("P=P+1", Regex.Replace(result.OutputCode, @"\s+", ""));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Coexist_LiftedIncrement_C22OwnsSite_WhenEnabled_C17StaysSilent()
    {
        // M2 handoff (decisions 15/17): with C22 enabled a simple lifted
        // increment is explicitized by NL-10 and C17 never raises CS2J5335.
        var src = """
            public class C
            {
                void M()
                {
                    int? x = 1;
                    x++;
                }
            }
            """;
        var options = new NumericBoundaryOptions
        {
            EnableNullable = true,
            EnableIncrementTargets = true,
            EnableDecimalOps = false,
            Strict = true,
        };
        var result = Run(src, options);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.IncrementTargetNullableLifted);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_n_o1=x", compact);
        Assert.Contains("x=(__cs2j_n_o1.HasValue?(int?)(__cs2j_n_o1.Value+1):(int?)null)", compact);
        Assert.DoesNotContain("x++", compact);
        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementPostfixSites);
    }

    [Fact]
    public void Coexist_LiftedIncrement_C17StillRaises5335_WhenC22Disabled()
    {
        // C22 switched off: the established C17 CS2J5335 error + NB-12
        // rollback behavior is unchanged.
        var src = """
            public class C
            {
                void M()
                {
                    int? x = 1;
                    x++;
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
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.IncrementTargetNullableLifted);
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: NL-1 lifted arithmetic guards
    // ════════════════════════════════════════════════════════════════════

    public static IEnumerable<object[]> ArithmeticCases()
    {
        int?[] av = [null, 2, 4];
        int?[] bv = [null, 2];
        string[] ops = ["+", "-", "*", "/", "%"];
        foreach (var op in ops)
        {
            foreach (var a in av)
            {
                foreach (var b in bv)
                {
                    yield return new object[] { a!, b!, op };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ArithmeticCases))]
    public void M1_NL1_ArithmeticGuards_RuntimeTruthTable_Int(int? a, int? b, string op)
    {
        var result = Run($$"""
            public class C
            {
                public static object? F(int? a, int? b)
                    => (object?)(a {{op}} b);
            }
            """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Equal(0, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence2($"a {op} b", "int", a, b);
    }

    [Theory]
    [InlineData("long", "L")]
    [InlineData("float", "f")]
    [InlineData("double", "0.0")]
    [InlineData("uint", "u")]
    [InlineData("ulong", "UL")]
    public void M1_NL1_ArithmeticGuard_RendersUnderlyingCast_AllNumericDomains(string typeKeyword, string suffix)
    {
        var src = $$"""
            public class C
            {
                public static void F({{typeKeyword}}? a, {{typeKeyword}}? b)
                {
                    var r = a + b;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains($"a.HasValue&&b.HasValue?({typeKeyword}?)(a.Value+b.Value):({typeKeyword}?)null", compact);
    }

    [Fact]
    public void M1_NL1_NullConstantOperands_NotHoisted_AlwaysNull_BothDirections()
    {
        foreach (var expression in new[] { "a + null", "null + a", "a + (int?)null", "(int?)null + a" })
        {
            var src = $$"""
                public class C
                {
                    public static object? F(int? a) => (object?)({{expression}});
                }
                """;

            var result = Run(src);

            Assert.True(result.Changed);
            AssertOutputCompiles(result.OutputCode);
            Assert.Equal(0, result.Statistics.Nullable.TempsLifted);
            Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
            // Null propagates: every lifted add with a null operand yields null.
            Assert.Null(InvokeStatic(CompileAndLoad(result.OutputCode), "F", (object?)1));
            Assert.Null(InvokeStatic(CompileAndLoad(result.OutputCode), "F", (object?)null));
        }
    }

    [Fact]
    public void M1_NL1_OneSidedNonNullable_StableOperand_BareUse_NoHasValue()
    {
        var src = """
            public class C
            {
                public static object? F(int? a) => (object?)(a + 1);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("a.HasValue?(int?)(a.Value+1):(int?)null", compact);
        Assert.DoesNotContain("1.HasValue", compact);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence1("a + 1", "int", 1);
        AssertSemanticEquivalence1("a + 1", "int", null);
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: NL-2 lifted unary guards
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0, "-")]
    [InlineData(1, "-")]
    [InlineData(5, "-")]
    [InlineData(null, "+")]
    [InlineData(7, "+")]
    public void M1_NL2_UnaryArithmetic_RuntimeTruthTable(int? a, string op)
    {
        var result = Run($$"""
            public class C
            {
                public static object? F(int? a) => (object?)({{op}}a);
            }
            """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedUnarySites);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence2($"{op}a", "int", a, null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void M1_NL2_BitwiseComplement_IntegerDomain_Owned(int? a)
    {
        var result = Run("""
            public class C
            {
                public static object? F(int? a) => (object?)(~a);
            }
            """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedUnarySites);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence2("~a", "int", a, null);
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: NL-3 lifted relational guards
    // ════════════════════════════════════════════════════════════════════

    public static IEnumerable<object[]> RelationalCases()
    {
        int?[][] pairs =
        [
            [null, null], [null, 1], [1, null], [1, 1], [1, 2], [2, 1],
        ];
        string[] ops = ["<", ">", "<=", ">="];
        foreach (var op in ops)
        {
            foreach (var pair in pairs)
            {
                yield return new object[] { pair[0]!, pair[1]!, op };
            }
        }
    }

    [Theory]
    [MemberData(nameof(RelationalCases))]
    public void M1_NL3_Relational_AlwaysFalseWhenNull_RuntimeTruthTable(int? a, int? b, string op)
    {
        var result = Run($$"""
            public class C
            {
                public static object? F(int? a, int? b)
                    => (object?)(a {{op}} b);
            }
            """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedRelationalSites);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence2($"a {op} b", "int", a, b);
        if (a is null || b is null)
        {
            Assert.Equal(false, InvokeStatic(CompileAndLoad(result.OutputCode), "F", a, b));
        }
    }

    [Fact]
    public void M1_NL3_Relational_NullLiterals_BothDirections_False()
    {
        // A bare `null >= null` has no operand type and does not bind; the
        // legal lifted shapes are (int?)null against a null literal.
        foreach (var expression in new[]
                 {
                     "(int?)null >= null", "(int?)null < null",
                     "a > null", "null <= a",
                 })
        {
            var src = $$"""
                public class C
                {
                    public static object? F(int? a) => (object?)({{expression}});
                }
                """;

            var result = Run(src);

            Assert.True(result.Changed);
            Assert.Equal(1, result.Statistics.Nullable.LiftedRelationalSites);
            AssertOutputCompiles(result.OutputCode);
            Assert.Equal(false, InvokeStatic(CompileAndLoad(result.OutputCode), "F", (object?)1));
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: NL-4 lifted equality guards
    // ════════════════════════════════════════════════════════════════════

    public static IEnumerable<object[]> EqualityCases()
    {
        int?[][] pairs =
        [
            [null, null], [null, 1], [1, null], [1, 1], [1, 2], [2, 1],
        ];
        foreach (var op in new[] { "==", "!=" })
        {
            foreach (var pair in pairs)
            {
                yield return new object[] { pair[0]!, pair[1]!, op };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EqualityCases))]
    public void M1_NL4_BothNullableEquality_NineCellRuntimeTruthTable(int? a, int? b, string op)
    {
        var result = Run($$"""
            public class C
            {
                public static object? F(int? a, int? b)
                    => (object?)(a {{op}} b);
            }
            """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedEqualityBothNullableSites);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence2($"a {op} b", "int", a, b);
    }

    [Fact]
    public void M1_NL4_RawNullLiteralEquality_HasValueShape_BothDirections()
    {
        // Roslyn binds `x == null` as a NON-lifted binary: dedicated predicate
        // path renders !HasValue / HasValue (not the lifted guard shape).
        var cases = new (string Expr, string Expected)[]
        {
            ("a == null", "(!a.HasValue)"),
            ("null == a", "(!a.HasValue)"),
            ("a != null", "(a.HasValue)"),
            ("null != a", "(a.HasValue)"),
        };

        foreach (var (expression, expected) in cases)
        {
            var src = $$"""
                public class C
                {
                    public static object? F(int? a) => (object?)({{expression}});
                }
                """;

            var result = Run(src);

            Assert.True(result.Changed);
            Assert.Equal(1, result.Statistics.Nullable.LiftedEqualityNullLiteralSites);
            Assert.Contains(expected, Compact(result.OutputCode));
            AssertOutputCompiles(result.OutputCode);
            AssertSemanticEquivalence1(expression, "int", null);
            AssertSemanticEquivalence1(expression, "int", 3);
        }
    }

    [Fact]
    public void M1_NL4_OneSidedEquality_NonNullableSide_BareUse()
    {
        var src = """
            public class C
            {
                public static object? F(int? a) => (object?)(a == 1);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedEqualityOneSidedSites);
        Assert.Contains("a.HasValue&&a.Value==1", Compact(result.OutputCode));
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence1("a == 1", "int", null);
        AssertSemanticEquivalence1("a == 1", "int", 1);
        AssertSemanticEquivalence1("a == 1", "int", 2);
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: NL-5 bool? three-valued logic
    // ════════════════════════════════════════════════════════════════════

    public static IEnumerable<object?[]> BoolPairs()
    {
        bool?[] values = [null, false, true];
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                yield return new object?[] { a, b };
            }
        }
    }

    public static IEnumerable<object?[]> BoolBinaryCases()
    {
        foreach (var op in new[] { "&", "|", "^", "==", "!=" })
        {
            foreach (var pair in BoolPairs())
            {
                yield return new object?[] { pair[0], pair[1], op };
            }
        }
    }

    [Theory]
    [MemberData(nameof(BoolBinaryCases), MemberType = typeof(NumericBoundaryNullableLiftUnitTests))]
    public void M1_NL5_BoolNullable_Binary_NineCellRuntimeTruthTable(bool? a, bool? b, string op)
    {
        var result = Run($$"""
            public class C
            {
                public static object? F(bool? a, bool? b)
                    => (object?)(a {{op}} b);
            }
            """);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence2($"a {op} b", "bool", a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void M1_NL5_BoolNullable_Not_NullPropagation(bool? a)
    {
        var result = Run("""
            public class C
            {
                public static object? F(bool? a) => (object?)(!a);
            }
            """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedBoolLogicNotSites);
        AssertOutputCompiles(result.OutputCode);
        AssertSemanticEquivalence2("!a", "bool", a, null);
    }

    [Fact]
    public void M1_NL5_ShortCircuitOperators_NotLifted_NoRewrite()
    {
        // && / || only exist on non-nullable bool and are never lifted.
        var src = """
            public class C
            {
                public static bool F(bool a, bool b) => a && b || true;
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Theory]
    [InlineData("flag == true", true)]
    [InlineData("flag == false", false)]
    [InlineData("flag != true", false)]
    public void M1_NL5_BoolOneSidedEquality_RuntimeTruthTable(string expression, bool literal)
    {
        var src = $$"""
            public class C
            {
                public static object? F(bool? flag) => (object?)({{expression}});
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedEqualityOneSidedSites);
        AssertOutputCompiles(result.OutputCode);
        foreach (var value in new bool?[] { null, false, true })
        {
            var original = InvokeStatic(CompileAndLoad(src), "F", value);
            var rewritten = InvokeStatic(CompileAndLoad(result.OutputCode), "F", value);
            Assert.Equal(original, rewritten);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: nesting, temp hoisting, single evaluation
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M1_NestedArithmetic_InnerConditionalHoisted_OneTemp_Compiles()
    {
        var src = """
            public class C
            {
                public static object? F(int? a, int? b, int? c)
                {
                    return (object?)(a + b + c);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        // The outer site is the first (and only) hoist in this expression; it
        // captures the inner guard conditional before evaluating c.
        Assert.Contains("__cs2j_n_a1", compact);
        Assert.DoesNotContain("__cs2j_n_b1", compact);
        foreach (var (a, b, c) in new (int?, int?, int?)[]
                 {
                     (null, null, null), (null, 1, 2), (1, null, 2),
                     (1, 2, null), (1, 2, 3),
                 })
        {
            var original = InvokeStatic(CompileAndLoad(src), "F", a, b, c);
            var rewritten = InvokeStatic(CompileAndLoad(result.OutputCode), "F", a, b, c);
            Assert.Equal(original, rewritten);
        }
    }

    [Fact]
    public void M1_StableLocalsParamsLiterals_ZeroTemps()
    {
        var src = """
            public class C
            {
                public int? M(int? p)
                {
                    int? a = 1, b = 2;
                    int? r1 = a + b;
                    int? r2 = p * 3;
                    int? r3 = a - 1;
                    return r1 ?? r2 ?? r3;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M1_SingleEvaluation_TwoUnstableOperands_EachCalledOnce_SourceOrder()
    {
        var src = """
            public class C
            {
                public static int CallsA, CallsB;
                public static string Log = "";
                public static int? A() { CallsA++; Log += "A"; return 10; }
                public static int? B() { CallsB++; Log += "B"; return 20; }
                public static object? F()
                {
                    return (object?)(A() + B());
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
        var asm = CompileAndLoad(result.OutputCode);
        Assert.Equal(30, InvokeStatic(asm, "F"));
        Assert.Equal(1, asm.GetType("C")!.GetField("CallsA")!.GetValue(null));
        Assert.Equal(1, asm.GetType("C")!.GetField("CallsB")!.GetValue(null));
        Assert.Equal("AB", asm.GetType("C")!.GetField("Log")!.GetValue(null));
    }

    [Fact]
    public void M1_SingleEvaluation_NestedUnstable_InnerDeclarationsFirst_SourceOrder()
    {
        var src = """
            public class C
            {
                public static string Log = "";
                public static int? Get(char tag, int? v) { Log += tag; return v; }
                public static object? F()
                {
                    return (object?)((Get('A', 10) + Get('B', 20)) + Get('C', 30));
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var asm = CompileAndLoad(result.OutputCode);
        Assert.Equal(60, InvokeStatic(asm, "F"));
        Assert.Equal("ABC", asm.GetType("C")!.GetField("Log")!.GetValue(null));
    }

    [Fact]
    public void M1_SingleEvaluation_IndexerSideEffect_EvaluatedOnce()
    {
        var src = """
            public class C
            {
                public static int IndexCalls;
                public static int Next() { IndexCalls++; return 0; }
                public static int?[] Arr = { 42 };
                public static int? X = 7;
                public static object? F()
                {
                    return (object?)(Arr[Next()] < X);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
        var asm = CompileAndLoad(result.OutputCode);
        Assert.Equal(false, InvokeStatic(asm, "F"));
        Assert.Equal(1, asm.GetType("C")!.GetField("IndexCalls")!.GetValue(null));
    }

    [Fact]
    public void M1_SingleEvaluation_PropertyAccessSideEffect_EvaluatedOnce()
    {
        var src = """
            public class C
            {
                public static int Gets;
                public bool? P { get; set; }
                public static C? Instance;
                public static C? Get() { Gets++; return Instance; }
                public static object? F(bool? flag)
                {
                    return (object?)(Get()!.P | flag);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
        var asm = CompileAndLoad(result.OutputCode);
        var type = asm.GetType("C")!;
        var instance = Activator.CreateInstance(type);
        type.GetProperty("P")!.SetValue(instance, (bool?)true);
        type.GetField("Instance")!.SetValue(null, instance);

        // Kleene OR: true | null == true; Get() must run exactly once.
        Assert.Equal(true, InvokeStatic(asm, "F", new object?[] { null }));
        Assert.Equal(1, type.GetField("Gets")!.GetValue(null));
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: cross-rule composition (E03 ?? chain, conditional access, E04
    // product, C15 checked coexistence)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M1_ChainsWith_E03_NullCoalesce_GuardThenCoalesce_RuntimeEquivalent()
    {
        var src = """
            public class C
            {
                public static object? F(int? a, int? b) => (object?)((a + b) ?? 0);
            }
            """;

        var c22 = Run(src);
        Assert.True(c22.Changed);
        AssertOutputCompiles(c22.OutputCode);

        var (_, model2) = ParseWithSemantics(c22.OutputCode);
        var e03 = new global::CSharpToJava.Core.NullCoalesceRewriter.NullCoalesceRewriter()
            .Preprocess(c22.OutputCode, model2,
                new global::CSharpToJava.Core.NullCoalesceRewriter.NullCoalesceOptions());
        Assert.True(e03.Changed);
        AssertOutputCompiles(e03.OutputCode);

        foreach (var (a, b) in new (int?, int?)[] { (null, 1), (1, null), (null, null), (1, 2) })
        {
            var original = InvokeStatic(CompileAndLoad(src), "F", a, b);
            var final = InvokeStatic(CompileAndLoad(e03.OutputCode), "F", a, b);
            Assert.Equal(original, final);
        }
    }

    [Fact]
    public void M1_NullCoalesceAlone_C22ZeroRewrite_ByteIdentical()
    {
        var src = """
            public class C
            {
                public static int? F(int? a, int? b) => a ?? b;
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void M1_NestedCompareAndBoolLogic_RuntimeEquivalent()
    {
        var src = """
            public class C
            {
                public static object? F(int? a, int? b, bool? flag)
                {
                    return (object?)(((a + b) > 0) & flag);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Equal(1, result.Statistics.Nullable.LiftedRelationalSites);
        Assert.Equal(1, result.Statistics.Nullable.LiftedBoolLogicAndSites);
        // The relational guard feeds the bool-& guard and the arithmetic
        // guard feeds the relational: each enclosing site hoists the inner
        // conditional once; flag is a stable parameter and is never hoisted.
        Assert.Equal(2, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);

        foreach (var (a, b, flag) in new (int?, int?, bool?)[]
                 {
                     (null, 1, null), (1, 2, null), (-5, 2, true),
                     (1, 2, false), (null, null, false),
                 })
        {
            var original = InvokeStatic(CompileAndLoad(src), "F", a, b, flag);
            var rewritten = InvokeStatic(CompileAndLoad(result.OutputCode), "F", a, b, flag);
            Assert.Equal(original, rewritten);
        }
    }

    [Fact]
    public void M1_ConditionalAccess_NullableProperty_OneSidedEquality_RuntimeEquivalent()
    {
        // b?.P is an unstable operand (conditional access over a property
        // getter), so the one-sided equality guard needs a single-evaluation
        // temp — a block-bodied method supplies the statement anchor; an
        // expression body would correctly trip CS2J5355. Box lives in a
        // shared reference assembly so the original/rewritten F assemblies
        // (each their own load context) accept the same Box instances.
        var sharedPath = Path.Combine(
            Path.GetTempPath(), "cs2j-box-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            var boxCompilation = CSharpCompilation.Create(
                "c22-box-" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText("public class Box { public bool? P { get; set; } }") },
                RuntimeReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var fs = File.Create(sharedPath))
            {
                var boxEmit = boxCompilation.Emit(fs);
                Assert.True(boxEmit.Success,
                    string.Join(Environment.NewLine, boxEmit.Diagnostics.Select(d => d.ToString())));
            }

            var boxAssembly = Assembly.LoadFrom(sharedPath);
            var boxReference = MetadataReference.CreateFromFile(sharedPath);

            var src = """
                public class C
                {
                    public static object? F(Box? b)
                    {
                        return (object?)((b?.P) == true);
                    }
                }
                """;

            var result = Run(src,
                fullFrameworkReferences: true,
                extraReferences: new[] { boxReference });

            Assert.True(result.Changed);
            AssertOutputCompiles(result.OutputCode, extraReference: boxReference);
            var originalAsm = CompileAndLoad(src, new[] { boxReference });
            var rewrittenAsm = CompileAndLoad(result.OutputCode, new[] { boxReference });
            var boxType = boxAssembly.GetType("Box")!;
            object?[] inputs =
            [
                null,
                Activator.CreateInstance(boxType),
                Activator.CreateInstance(boxType),
                Activator.CreateInstance(boxType),
            ];
            boxType.GetProperty("P")!.SetValue(inputs[1], (bool?)null);
            boxType.GetProperty("P")!.SetValue(inputs[2], (bool?)true);
            boxType.GetProperty("P")!.SetValue(inputs[3], (bool?)false);

            foreach (var box in inputs)
            {
                var original = InvokeStatic(originalAsm, "F", box);
                var rewritten = InvokeStatic(rewrittenAsm, "F", box);
                Assert.Equal(original, rewritten);
            }
        }
        finally
        {
            // Assembly.LoadFrom maps the file for the process lifetime; on
            // Windows the delete is best-effort only.
            try { File.Delete(sharedPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void M1_E04EnumTernaryProduct_TextInput_ZeroRewrite()
    {
        // The exact product shape E04 emits for lifted enum ops: a plain
        // conditional with non-lifted inner enum OR — never re-lifted.
        var src = """
            public enum Color { Red, Green, Blue }
            public class C
            {
                public static Color? F(Color? a, Color? b)
                    => a.HasValue && b.HasValue ? (Color?)(a.Value | b.Value) : (Color?)null;
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void M2_CoexistsWith_C15Checked_C22GuardFirst_C15HelperInsideLiveBranch_NullBranchUntouched()
    {
        // Decision 18 seam (M2): C22 runs at Order=15 and explicitizes the
        // lifted site into
        // `a.HasValue && b.HasValue ? (int?)(a.Value + b.Value) : (int?)null`.
        // C15 (Order=20) recognizes the guard's `(int?)…`/`(int?)null` branch
        // casts (guard annotation) and, instead of CS2J5325, descends into the
        // live branch and inserts AddCheckedInt32 there; the null branch keeps
        // no helper. The co-run tally GuardedCheckedInnerSites moves.
        var src = """
            public class C
            {
                public static int? F(int? a, int? b)
                {
                    checked
                    {
                        int? r = a + b;
                        return r;
                    }
                }
            }
            """;
        var options = new NumericBoundaryOptions
        {
            EnableNullable = true,
            EnableIncrementTargets = false,
            EnableDecimalOps = false,
            Strict = true,
        };

        var result = Run(src, options);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NumericBoundaryNullableLifted);

        var compact = Compact(result.OutputCode);
        // Helper lands in the live branch, right next to the .Value reads:
        Assert.Contains(
            "a.HasValue&&b.HasValue?(int?)(Cs2jUnsigned.UnsignedMath.AddCheckedInt32(a.Value,b.Value)):(int?)null",
            compact);
        // The null branch carries no helper call — exactly one helper call in
        // the whole document, already pinned to the live branch above, and the
        // declaration ends with the bare null branch:
        Assert.Contains(":(int?)null);", compact);
        Assert.Equal(1, compact.Split("UnsignedMath").Length - 1);

        Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Equal(1, result.Statistics.Nullable.GuardedCheckedInnerSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_CoexistsWith_C15Checked_LiftedConversionGuard_InnerNarrowCastHelperized()
    {
        // NL-8 inside checked: (int?)x with x: long? renders a guard whose
        // live branch wraps (int)x.Value; C15 turns the narrowing inner cast
        // into a checked conversion helper while the wrap casts pass through.
        var src = """
            public class C
            {
                public static int? F(long? x)
                {
                    checked
                    {
                        int? r = (int?)x;
                        return r;
                    }
                }
            }
            """;

        var result = Run(src, new NumericBoundaryOptions
        {
            EnableNullable = true,
            EnableIncrementTargets = false,
            EnableDecimalOps = false,
            Strict = true,
        });

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        var compact = Compact(result.OutputCode);
        Assert.Contains("x.HasValue?(int?)(Cs2jUnsigned.UnsignedMath.ToInt32CheckedInt64((x.Value))):(int?)null",
            compact);
        Assert.Equal(1, result.Statistics.Nullable.LiftedConversions);
        Assert.Equal(1, result.Statistics.Nullable.GuardedCheckedInnerSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_C15Checked_LiftedSiteWithoutNullableRule_StillCS2J5325()
    {
        // With EnableNullable=false the C22 guard never exists, so a lifted
        // arithmetic site directly in checked scope keeps the established
        // CS2J5325 rejection + NB-12 whole-file rollback.
        var src = """
            public class C
            {
                public static int? F(int? a, int? b)
                {
                    checked
                    {
                        int? r = a + b;
                        return r;
                    }
                }
            }
            """;
        var options = new NumericBoundaryOptions
        {
            EnableNullable = false,
            EnableIncrementTargets = false,
            EnableDecimalOps = false,
            Strict = true,
        };

        var result = Run(src, options);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryNullableLifted);
    }

    // ════════════════════════════════════════════════════════════════════
    // M2: NL-6 GetValueOrDefault explicitization (decision 10)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M2_NL6_GetValueOrDefault_BothShapes_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int Zero(int? a) => a.GetValueOrDefault();
                public static int One(int? a, int d) => a.GetValueOrDefault(d);
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.Nullable.GetValueOrDefaultSites);
        var compact = Compact(result.OutputCode);
        Assert.Contains("(a.HasValue?a.Value:default(int))", compact);
        Assert.Contains("(a.HasValue?a.Value:d)", compact);
        Assert.DoesNotContain("GetValueOrDefault", compact);
        AssertOutputCompiles(result.OutputCode);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? a in new int?[] { 7, 0, null })
        {
            Assert.Equal(
                InvokeStatic(original, "Zero", a),
                InvokeStatic(rewritten, "Zero", a));
            Assert.Equal(
                InvokeStatic(original, "One", a, 5),
                InvokeStatic(rewritten, "One", a, 5));
        }
    }

    [Fact]
    public void M2_NL6_UnstableDefaultArgument_HoistedOnce_SingleEvaluation()
    {
        var src = """
            public class C
            {
                public static int Calls;
                public static int D() { Calls++; return 9; }
                public static int F(int? a)
                {
                    return a.GetValueOrDefault(D());
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.GetValueOrDefaultSites);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);
        var compact = Compact(result.OutputCode);
        // The d role gets its own __cs2j_n_d* temp, evaluated before the guard:
        Assert.Contains("var__cs2j_n_d1=D()", compact);
        Assert.Contains("(a.HasValue?a.Value:__cs2j_n_d1)", compact);
        AssertOutputCompiles(result.OutputCode);

        var asm = CompileAndLoad(result.OutputCode);
        var type = asm.GetType("C")!;
        // Default expression evaluates eagerly even when HasValue is true
        // (matches the real GOV call's argument evaluation), exactly once.
        Assert.Equal(7, InvokeStatic(asm, "F", (object?)7));
        Assert.Equal(1, type.GetField("Calls")!.GetValue(null));
        Assert.Equal(9, InvokeStatic(asm, "F", new object?[] { null }));
        Assert.Equal(2, type.GetField("Calls")!.GetValue(null));
    }

    [Fact]
    public void M2_NL6_UnstableReceiver_HoistedOnce_SingleEvaluation()
    {
        var src = """
            public class C
            {
                public static int Calls;
                public static int? R() { Calls++; return 11; }
                public static int F()
                {
                    return R().GetValueOrDefault();
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.GetValueOrDefaultSites);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_n_a1=R()", compact);
        Assert.Contains("(__cs2j_n_a1.HasValue?__cs2j_n_a1.Value:default(int))", compact);
        AssertOutputCompiles(result.OutputCode);

        var asm = CompileAndLoad(result.OutputCode);
        Assert.Equal(11, InvokeStatic(asm, "F"));
        Assert.Equal(1, asm.GetType("C")!.GetField("Calls")!.GetValue(null));
    }

    // ════════════════════════════════════════════════════════════════════
    // M2: NL-7 unwrap / unwrap-and-convert (decision 13)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M2_NL7_UnwrapSameType_RendersValue_RuntimeEquivalence()
    {
        var src = """
            public enum Color { Red, Green, Blue }
            public class C
            {
                public static int Num(int? a) => (int)a;
                public static Color En(Color? e) => (Color)e;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.Nullable.UnwrapConversions);
        var compact = Compact(result.OutputCode);
        Assert.Contains("=>a.Value", compact);
        Assert.Contains("=>e.Value", compact);
        AssertOutputCompiles(result.OutputCode);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        Assert.Equal(
            InvokeStatic(original, "Num", (object?)5),
            InvokeStatic(rewritten, "Num", (object?)5));
        Assert.Equal(
            InvokeStatic(original, "En", System.Enum.Parse(
                original.GetType("Color")!, "Green"))!.ToString(),
            InvokeStatic(rewritten, "En", System.Enum.Parse(
                rewritten.GetType("Color")!, "Green"))!.ToString());
    }

    [Fact]
    public void M2_NL7_UnwrapAndConvert_DifferentUnderlying_NullThrowsInvalidOperationException()
    {
        var src = """
            public class C
            {
                public static byte F(long? a) => (byte)a;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.UnwrapConversions);
        Assert.Contains("((byte)a.Value)", Compact(result.OutputCode));
        AssertOutputCompiles(result.OutputCode);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        Assert.Equal((byte)8, InvokeStatic(original, "F", (object?)8L));
        Assert.Equal((byte)8, InvokeStatic(rewritten, "F", (object?)8L));
        Assert.Throws<InvalidOperationException>(
            () => InvokeStatic(original, "F", new object?[] { null }));
        Assert.Throws<InvalidOperationException>(
            () => InvokeStatic(rewritten, "F", new object?[] { null }));
    }

    // ════════════════════════════════════════════════════════════════════
    // M2: NL-8 lifted S?→T? and S→T? conversions (decision 13)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M2_NL8_LiftedConversion_WideningAndNarrowing_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static long? Widen(int? a) => (long?)a;
                public static byte? Narrow(int? a) => (byte?)a;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.Nullable.LiftedConversions);
        var compact = Compact(result.OutputCode);
        Assert.Contains("(a.HasValue?(long?)((long)(a.Value)):(long?)null)", compact);
        Assert.Contains("(a.HasValue?(byte?)((byte)(a.Value)):(byte?)null)", compact);
        AssertOutputCompiles(result.OutputCode);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? a in new int?[] { 5, 300, null })
        {
            Assert.Equal(
                InvokeStatic(original, "Widen", a),
                InvokeStatic(rewritten, "Widen", a));
            Assert.Equal(
                InvokeStatic(original, "Narrow", a),
                InvokeStatic(rewritten, "Narrow", a));
        }
    }

    [Fact]
    public void M2_NL8_LiftedEnumConversion_RuntimeEquivalence()
    {
        var src = """
            public enum Color { Red, Green, Blue }
            public enum Mark { A, B, C }
            public class C
            {
                public static Mark? F(Color? c) => (Mark?)c;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedConversions);
        // Distinct enum types always carry the explicit (Mark) cast even when
        // their shared underlying is int; null propagates through the guard.
        Assert.Contains(
            "(c.HasValue?(global::Mark?)((global::Mark)(c.Value)):(global::Mark?)null)",
            Compact(result.OutputCode));
        AssertOutputCompiles(result.OutputCode);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        var originalColor = original.GetType("Color")!;
        var rewrittenColor = rewritten.GetType("Color")!;
        foreach (var name in new[] { "Red", "Green", "Blue" })
        {
            var expected = InvokeStatic(original, "F",
                System.Enum.Parse(originalColor, name));
            var actual = InvokeStatic(rewritten, "F",
                System.Enum.Parse(rewrittenColor, name));
            Assert.Equal(expected!.ToString(), actual!.ToString());
        }

        Assert.Null(InvokeStatic(original, "F", new object?[] { null }));
        Assert.Null(InvokeStatic(rewritten, "F", new object?[] { null }));
    }

    [Fact]
    public void M2_NL8_NonNullableSource_WrapConvert_NoGuardBranch()
    {
        // S → T? (non-nullable source) can never take the null branch: the
        // guard conditional is dropped and only the wrap cast stays.
        var src = """
            public class C
            {
                public static int? F(long x) => (int?)x;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.LiftedConversions);
        var compact = Compact(result.OutputCode);
        Assert.Contains("(int?)((int)(x))", compact);
        Assert.DoesNotContain("HasValue", compact);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(5, InvokeStatic(CompileAndLoad(result.OutputCode), "F", 5L));
    }

    // ════════════════════════════════════════════════════════════════════
    // M2: NL-9 nullable-enum backstop (decision 12)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M2_NL9_UnstableEnumOperands_BackstopHoists_InsteadOfE04Skip()
    {
        // E04 only rewrites stable enum operands; an E? property access used
        // in a lifted bit op is the unstable face C22 must own (hoist + guard,
        // both getter reads evaluated once).
        var src = """
            public enum Color { Red = 1, Green = 2, Blue = 4 }
            public class C
            {
                public static int Gets;
                public Color? P
                {
                    get { Gets++; return Color.Red; }
                }

                public static object? F(C c)
                {
                    return (object?)(c.P & c.P);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.NullableEnumBackstopSites);
        Assert.Equal(2, result.Statistics.Nullable.TempsLifted);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_n_a1=c.P", compact);
        Assert.Contains("var__cs2j_n_b1=c.P", compact);
        Assert.Contains(
            "(__cs2j_n_a1.HasValue&&__cs2j_n_b1.HasValue?(global::Color?)((global::Color)((int)(__cs2j_n_a1.Value)&(int)(__cs2j_n_b1.Value))):(global::Color?)null)",
            compact);
        AssertOutputCompiles(result.OutputCode);

        var asm = CompileAndLoad(result.OutputCode);
        var type = asm.GetType("C")!;
        var instance = Activator.CreateInstance(type);
        Assert.Equal(System.Enum.Parse(asm.GetType("Color")!, "Red"),
            InvokeStatic(asm, "F", instance));
        Assert.Equal(2, type.GetField("Gets")!.GetValue(null));
    }

    [Fact]
    public void M2_NL9_NarrowByteEnum_Not_UncheckedShellStrippedOutsideChecked_RuntimeEquivalence()
    {
        var src = """
            public enum SmallEnum : byte { A = 1, B = 2 }
            public class C
            {
                public static SmallEnum? F(SmallEnum? e) => ~e;
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.NullableEnumBackstopSites);
        var compact = Compact(result.OutputCode);
        // NB-1 strips the unchecked shell outside checked scopes:
        Assert.DoesNotContain("unchecked", compact);
        Assert.Contains(
            "(e.HasValue?(global::SmallEnum?)((global::SmallEnum)((byte)(~((byte)(e.Value))))):(global::SmallEnum?)null)",
            compact);
        AssertOutputCompiles(result.OutputCode);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        var originalEnum = original.GetType("SmallEnum")!;
        var rewrittenEnum = rewritten.GetType("SmallEnum")!;
        Assert.Equal((byte)254, (byte)InvokeStatic(rewritten, "F",
            System.Enum.Parse(rewrittenEnum, "A"))!);
        Assert.Equal(
            ((byte)InvokeStatic(original, "F", System.Enum.Parse(originalEnum, "B"))!).ToString(),
            ((byte)InvokeStatic(rewritten, "F", System.Enum.Parse(rewrittenEnum, "B"))!).ToString());
        Assert.Null(InvokeStatic(original, "F", new object?[] { null }));
        Assert.Null(InvokeStatic(rewritten, "F", new object?[] { null }));
    }

    [Fact]
    public void M2_NL9_BackstopOperators_RuntimeTruthTable()
    {
        var enumDecl = "public enum Color { Red = 1, Green = 2, Blue = 4 }";
        var values = new int?[] { null, 1, 2, 4 };

        foreach (var op in new[] { "&", "|", "^" })
        {
            var src = $$"""
                {{enumDecl}}
                public class C
                {
                    public static object? F(Color? a, Color? b)
                    {
                        return (object?)(a {{op}} b);
                    }
                }
                """;

            var result = Run(src);
            Assert.True(result.Changed);
            Assert.Equal(1, result.Statistics.Nullable.NullableEnumBackstopSites);
            AssertOutputCompiles(result.OutputCode);

            var original = CompileAndLoad(src);
            var rewritten = CompileAndLoad(result.OutputCode);
            var enumType = original.GetType("Color")!;
            var rewrittenEnumType = rewritten.GetType("Color")!;
            foreach (var a in values)
            {
                foreach (var b in values)
                {
                    var expected = InvokeStatic(original, "F",
                        a is int av2 ? System.Enum.ToObject(enumType, av2) : null,
                        b is int bv2 ? System.Enum.ToObject(enumType, bv2) : null);
                    var actual = InvokeStatic(rewritten, "F",
                        a is int av3 ? System.Enum.ToObject(rewrittenEnumType, av3) : null,
                        b is int bv3 ? System.Enum.ToObject(rewrittenEnumType, bv3) : null);
                    Assert.Equal(expected?.ToString(), actual?.ToString());
                }
            }
        }

        var notSrc = $$"""
            {{enumDecl}}
            public class C
            {
                public static object? F(Color? a) => (object?)(~a);
            }
            """;
        var notResult = Run(notSrc);
        Assert.True(notResult.Changed);
        Assert.Equal(1, notResult.Statistics.Nullable.NullableEnumBackstopSites);
        AssertOutputCompiles(notResult.OutputCode);

        var notOriginal = CompileAndLoad(notSrc);
        var notRewritten = CompileAndLoad(notResult.OutputCode);
        var origColor = notOriginal.GetType("Color")!;
        var newColor = notRewritten.GetType("Color")!;
        Assert.Null(InvokeStatic(notRewritten, "F", new object?[] { null }));
        foreach (var v in values)
        {
            if (v == null)
            {
                continue;
            }

            var expected = InvokeStatic(notOriginal, "F", System.Enum.ToObject(origColor, v.Value));
            var actual = InvokeStatic(notRewritten, "F", System.Enum.ToObject(newColor, v.Value));
            Assert.Equal(expected!.ToString(), actual!.ToString());
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // M2: NL-10 lifted increment/decrement (decision 15)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M2_NL10_PostfixStandalone_GuardedWrite_AnchorConsumed_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int? F(int? a)
                {
                    int? x = a;
                    x++;
                    return x;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains(
            "var__cs2j_n_o1=x;x=(__cs2j_n_o1.HasValue?(int?)(__cs2j_n_o1.Value+1):(int?)null);",
            compact);
        Assert.DoesNotContain("x++", compact);
        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementPostfixSites);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? v in new int?[] { null, 0, 41 })
        {
            Assert.Equal(
                InvokeStatic(original, "F", v),
                InvokeStatic(rewritten, "F", v));
        }

        // The explicitized form carries no lifted increment anymore.
        var second = Run(result.OutputCode);
        Assert.False(second.Changed);
    }

    [Fact]
    public void M2_NL10_PostfixValueContext_SiteBecomesOldTemp_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int? Old(int? a)
                {
                    int? x = a;
                    var z = x++;
                    return z;
                }

                public static int? Stored(int? a)
                {
                    int? x = a;
                    var z = x++;
                    return x;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_n_o1=x", compact);
        Assert.Contains("varz=__cs2j_n_o1", compact);
        Assert.Contains(
            "x=(__cs2j_n_o1.HasValue?(int?)(__cs2j_n_o1.Value+1):(int?)null)",
            compact);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? v in new int?[] { null, 5 })
        {
            Assert.Equal(
                InvokeStatic(original, "Old", v),
                InvokeStatic(rewritten, "Old", v));
            Assert.Equal(
                InvokeStatic(original, "Stored", v),
                InvokeStatic(rewritten, "Stored", v));
        }
    }

    [Fact]
    public void M2_NL10_Prefix_StandaloneAndValueContext_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int? Stored(int? a)
                {
                    int? x = a;
                    ++x;
                    return x;
                }

                public static int? Value(int? a)
                {
                    int? x = a;
                    var z = ++x;
                    return z;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains("x=(x.HasValue?(int?)(x.Value+1):(int?)null)", compact);
        // Value-position prefix is a parenthesized assignment.
        Assert.Contains("z=(x=(x.HasValue?(int?)(x.Value+1):(int?)null))", compact);
        Assert.DoesNotContain("++x", compact);
        Assert.Equal(2, result.Statistics.Nullable.LiftedIncrementPrefixSites);
        Assert.Equal(0, result.Statistics.Nullable.TempsLifted);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? v in new int?[] { null, 5 })
        {
            Assert.Equal(
                InvokeStatic(original, "Stored", v),
                InvokeStatic(rewritten, "Stored", v));
            Assert.Equal(
                InvokeStatic(original, "Value", v),
                InvokeStatic(rewritten, "Value", v));
        }
    }

    [Fact]
    public void M2_NL10_Decrement_PrefixAndPostfix_NullPropagates_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int? Post(int? a)
                {
                    int? x = a;
                    x--;
                    return x;
                }

                public static int? Pre(int? a)
                {
                    int? x = a;
                    var z = --x;
                    return z;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains(
            "x=(__cs2j_n_o1.HasValue?(int?)(__cs2j_n_o1.Value-1):(int?)null)",
            compact);
        Assert.Contains(
            "z=(x=(x.HasValue?(int?)(x.Value-1):(int?)null))",
            compact);
        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementPostfixSites);
        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementPrefixSites);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? v in new int?[] { null, 5 })
        {
            Assert.Equal(
                InvokeStatic(original, "Post", v),
                InvokeStatic(rewritten, "Post", v));
            Assert.Equal(
                InvokeStatic(original, "Pre", v),
                InvokeStatic(rewritten, "Pre", v));
        }
    }

    [Fact]
    public void M2_NL10_EnumPostfixAndPrefix_CastShape_RuntimeEquivalence()
    {
        var src = """
            public enum Color { Red = 1, Green = 2, Blue = 4 }
            public enum Small : byte { A = 1, B = 2 }
            public class C
            {
                public static string? Post(Color? a)
                {
                    Color? x = a;
                    x++;
                    return x.HasValue ? x.Value.ToString() : null;
                }

                public static string? Pre(Color? a)
                {
                    Color? x = a;
                    var z = ++x;
                    return z.HasValue ? z.Value.ToString() : null;
                }

                public static string? Narrow(Small? a)
                {
                    Small? x = a;
                    x++;
                    return x.HasValue ? x.Value.ToString() : null;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains(
            "x=(__cs2j_n_o1.HasValue?(global::Color?)((global::Color)((int)(__cs2j_n_o1.Value)+1)):(global::Color?)null)",
            compact);
        Assert.Contains(
            "z=(x=(x.HasValue?(global::Color?)((global::Color)((int)(x.Value)+1)):(global::Color?)null))",
            compact);
        // Narrow is the file's second postfix site → the o2 temp.
        Assert.Contains(
            "x=(__cs2j_n_o2.HasValue?(global::Small?)((global::Small)((byte)(__cs2j_n_o2.Value)+1)):(global::Small?)null)",
            compact);
        Assert.Equal(2, result.Statistics.Nullable.LiftedIncrementPostfixSites);
        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementPrefixSites);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? v in new int?[] { null, 1, 4 })
        {
            var colorValue = v is int iv ? System.Enum.ToObject(original.GetType("Color")!, iv) : null;
            var rewrittenColor = v is int rv ? System.Enum.ToObject(rewritten.GetType("Color")!, rv) : null;
            Assert.Equal(
                InvokeStatic(original, "Post", colorValue)?.ToString(),
                InvokeStatic(rewritten, "Post", rewrittenColor)?.ToString());
            Assert.Equal(
                InvokeStatic(original, "Pre", colorValue)?.ToString(),
                InvokeStatic(rewritten, "Pre", rewrittenColor)?.ToString());
        }

        foreach (byte v in new byte?[] { 1, 2 })
        {
            var smallValue = System.Enum.ToObject(original.GetType("Small")!, v);
            var rewrittenSmall = System.Enum.ToObject(rewritten.GetType("Small")!, v);
            Assert.Equal(
                InvokeStatic(original, "Narrow", smallValue)?.ToString(),
                InvokeStatic(rewritten, "Narrow", rewrittenSmall)?.ToString());
        }

        Assert.Null(InvokeStatic(rewritten, "Narrow",
            new object?[] { null }));
    }

    [Fact]
    public void M2_NL10_StandalonePostfix_EmbeddedSlot_WrappedInBlock_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int? F(bool c, int? a)
                {
                    int? x = a;
                    if (c) x++;
                    return x;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.Contains("if(c){var__cs2j_n_o1=x;x=(__cs2j_n_o1.HasValue?", Compact(result.OutputCode));

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (var c in new[] { false, true })
        {
            foreach (int? v in new int?[] { null, 41 })
            {
                Assert.Equal(
                    InvokeStatic(original, "F", c, v),
                    InvokeStatic(rewritten, "F", c, v));
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // M3: NL-10/11 decision 17 — complex RMW targets (get/set properties,
    // get/set indexers and true array elements) are now explicitized with
    // target/receiver/index temporaries instead of CS2J5355.
    // ════════════════════════════════════════════════════════════════════

    private static object NewBox(Assembly assembly, string typeName, params object?[] args) =>
        assembly.GetType(typeName)!
            .GetConstructors()
            .Single(c => c.GetParameters().Length == args.Length)
            .Invoke(args);

    [Fact]
    public void M3_NL10_ComplexProperty_GetterSetterEachOnce_StatsAndCounters()
    {
        var src = """
            public class Box
            {
                private int? _p;
                public int gets;
                public int sets;
                public Box(int? p) { _p = p; }
                public int? P
                {
                    get { gets++; return _p; }
                    set { sets++; _p = value; }
                }
            }
            public class C
            {
                public static (int? p, int gets, int sets) Post(Box b)
                {
                    b.P++;
                    return (b.P, b.gets, b.sets);
                }
                public static (int? p, int gets, int sets) Pre(Box b)
                {
                    ++b.P;
                    return (b.P, b.gets, b.sets);
                }
                public static (int? p, int gets, int sets) Compound(Box b, int? y)
                {
                    b.P += y;
                    return (b.P, b.gets, b.sets);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var stats = result.Statistics.Nullable;
        Assert.Equal(3, stats.ComplexRmwSites);
        Assert.Equal(1, stats.LiftedIncrementPostfixSites);
        Assert.Equal(1, stats.LiftedIncrementPrefixSites);
        Assert.Equal(1, stats.LiftedIncrementCompoundSites);
        Assert.Equal(3, stats.TempsTargetRole);
        Assert.Equal(0, stats.TempsReceiverRole);
        Assert.Equal(0, stats.TempsIndexRole);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? init in new int?[] { null, 7, -3 })
        {
            foreach (var method in new[] { "Post", "Pre" })
            {
                Assert.Equal(
                    InvokeStatic(original, method, NewBox(original, "Box", init))?.ToString(),
                    InvokeStatic(rewritten, method, NewBox(rewritten, "Box", init))?.ToString());
            }

            foreach (int? y in new int?[] { null, 5 })
            {
                Assert.Equal(
                    InvokeStatic(original, "Compound", NewBox(original, "Box", init), y)?.ToString(),
                    InvokeStatic(rewritten, "Compound", NewBox(rewritten, "Box", init), y)?.ToString());
            }
        }
    }

    [Fact]
    public void M3_NL10_ComplexProperty_ValuePosition_RuntimeTruthTable()
    {
        var src = """
            public class Box
            {
                private int? _p;
                public Box(int? p) { _p = p; }
                public int? P
                {
                    get { return _p; }
                    set { _p = value; }
                }
            }
            public class C
            {
                // All three sites sit in VALUE positions (declarator
                // initializers / return value): postfix yields the old value,
                // prefix and compound yield the new nullable.
                public static (int? old, int? afterPost, int? pre, int? final) F(Box b, int? y)
                {
                    var z = b.P++;
                    var afterPost = b.P;
                    var q = ++b.P;
                    var f = b.P += y;
                    return (z, afterPost, q, f);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var stats = result.Statistics.Nullable;
        Assert.Equal(3, stats.ComplexRmwSites);
        Assert.Equal(1, stats.LiftedIncrementPostfixSites);
        Assert.Equal(1, stats.LiftedIncrementPrefixSites);
        Assert.Equal(1, stats.LiftedIncrementCompoundSites);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? init in new int?[] { null, 1, 40 })
        {
            foreach (int? y in new int?[] { null, 2 })
            {
                Assert.Equal(
                    InvokeStatic(original, "F", NewBox(original, "Box", init), y)?.ToString(),
                    InvokeStatic(rewritten, "F", NewBox(rewritten, "Box", init), y)?.ToString());
            }
        }
    }

    [Fact]
    public void M3_ComplexProperty_EffectfulReceiverOnce_StableChainNotHoisted()
    {
        var src = """
            public class Box
            {
                private int? _p;
                public int receiverEvals;
                public Box(int? p) { _p = p; }
                public int? P
                {
                    get { return _p; }
                    set { _p = value; }
                }
                public Box Self()
                {
                    receiverEvals++;
                    return this;
                }
            }
            public class Holder
            {
                public Box Box;
                public Holder(Box b) { Box = b; }
            }
            public class C
            {
                public static (int? p, int receiverEvals) Effectful(Box b)
                {
                    b.Self().P++;
                    return (b.P, b.receiverEvals);
                }
                public static int? StableQualifiedChain(Holder h, int? y)
                {
                    h.Box.P += y;
                    return h.Box.P;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        // The effectful receiver is read once into an r temp; the stable
        // qualified chain (h.Box) is rebuilt per access with no r temp.
        Assert.Contains("var__cs2j_n_r1=b.Self();", compact);
        Assert.Equal(1, result.Statistics.Nullable.TempsReceiverRole);
        Assert.Contains("var__cs2j_n_t2=h.Box.P;h.Box.P=", compact);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? init in new int?[] { null, 9 })
        {
            Assert.Equal(
                InvokeStatic(original, "Effectful", NewBox(original, "Box", init))?.ToString(),
                InvokeStatic(rewritten, "Effectful", NewBox(rewritten, "Box", init))?.ToString());

            foreach (int? y in new int?[] { null, 3 })
            {
                static object? Holder(Assembly a, object? box)
                {
                    return a.GetType("Holder")!
                        .GetConstructor(new[] { a.GetType("Box")! })!
                        .Invoke(new[] { box });
                }

                Assert.Equal(
                    InvokeStatic(original, "StableQualifiedChain",
                        Holder(original, NewBox(original, "Box", init)), y)?.ToString(),
                    InvokeStatic(rewritten, "StableQualifiedChain",
                        Holder(rewritten, NewBox(rewritten, "Box", init)), y)?.ToString());
            }
        }
    }

    [Fact]
    public void M3_NL10_ComplexIndexer_ReceiverAndIndicesEachOnce_RuntimeEquivalence()
    {
        var src = """
            public class Box
            {
                private int? _v;
                public int gets;
                public int sets;
                public int receiverEvals;
                public int indexEvals;
                public Box(int? v) { _v = v; }
                public Box Self()
                {
                    receiverEvals++;
                    return this;
                }
                public int At(int i)
                {
                    indexEvals++;
                    return i;
                }
                public int? this[int i]
                {
                    get { gets++; return _v; }
                    set { sets++; _v = value; }
                }
                public int? this[int i, int j]
                {
                    get { return _v; }
                    set { _v = value; }
                }
            }
            public class C
            {
                public static (int? v, int gets, int sets, int receiverEvals, int indexEvals) Post(Box b)
                {
                    b.Self()[b.At(0)]++;
                    return (b[0], b.gets, b.sets, b.receiverEvals, b.indexEvals);
                }
                public static (int? v, int gets, int sets, int receiverEvals, int indexEvals) Pre(Box b)
                {
                    ++b.Self()[b.At(0)];
                    return (b[0], b.gets, b.sets, b.receiverEvals, b.indexEvals);
                }
                public static (int? v, int gets, int sets, int receiverEvals, int indexEvals) Compound(Box b, int? y)
                {
                    b.Self()[b.At(0)] += y;
                    return (b[0], b.gets, b.sets, b.receiverEvals, b.indexEvals);
                }
                public static int? TwoDimensional(Box b)
                {
                    b[0, 1]++;
                    return b[0, 0];
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var stats = result.Statistics.Nullable;
        Assert.Equal(4, stats.ComplexRmwSites);
        Assert.Equal(3, stats.TempsReceiverRole);
        Assert.Equal(5, stats.TempsIndexRole); // 0/1/2 single-arg sites + 2 for the 2D site
        Assert.Contains("var__cs2j_n_i04=0;var__cs2j_n_i14=1;", Compact(result.OutputCode));

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? init in new int?[] { null, 11 })
        {
            foreach (var method in new[] { "Post", "Pre" })
            {
                Assert.Equal(
                    InvokeStatic(original, method, NewBox(original, "Box", init))?.ToString(),
                    InvokeStatic(rewritten, method, NewBox(rewritten, "Box", init))?.ToString());
            }

            Assert.Equal(
                InvokeStatic(original, "TwoDimensional", NewBox(original, "Box", init))?.ToString(),
                InvokeStatic(rewritten, "TwoDimensional", NewBox(rewritten, "Box", init))?.ToString());

            foreach (int? y in new int?[] { null, 4 })
            {
                Assert.Equal(
                    InvokeStatic(original, "Compound", NewBox(original, "Box", init), y)?.ToString(),
                    InvokeStatic(rewritten, "Compound", NewBox(rewritten, "Box", init), y)?.ToString());
            }
        }
    }

    [Fact]
    public void M3_NL10_ComplexArrayElement_IndexEachOnce_RuntimeEquivalence()
    {
        var src = """
            public class IndexProbe
            {
                public int calls;
                public int At(int i)
                {
                    calls++;
                    return i;
                }
            }
            public class C
            {
                public static (int? v, int calls) Post(int?[] arr, IndexProbe ix)
                {
                    arr[ix.At(0)]++;
                    return (arr[0], ix.calls);
                }
                public static (int? v, int calls) Pre(int?[] arr, IndexProbe ix)
                {
                    ++arr[ix.At(0)];
                    return (arr[0], ix.calls);
                }
                public static (int? v, int calls) Compound(int?[] arr, IndexProbe ix, int? y)
                {
                    arr[ix.At(0)] += y;
                    return (arr[0], ix.calls);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var stats = result.Statistics.Nullable;
        Assert.Equal(3, stats.ComplexRmwSites);
        Assert.Equal(3, stats.TempsIndexRole);
        Assert.Equal(0, stats.TempsReceiverRole); // arrays are not materialized

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? init in new int?[] { null, 21 })
        {
            foreach (var method in new[] { "Post", "Pre" })
            {
                Assert.Equal(
                    InvokeStatic(original, method,
                        new int?[] { init }, NewBox(original, "IndexProbe"))?.ToString(),
                    InvokeStatic(rewritten, method,
                        new int?[] { init }, NewBox(rewritten, "IndexProbe"))?.ToString());
            }

            foreach (int? y in new int?[] { null, 7 })
            {
                Assert.Equal(
                    InvokeStatic(original, "Compound",
                        new int?[] { init }, NewBox(original, "IndexProbe"), y)?.ToString(),
                    InvokeStatic(rewritten, "Compound",
                        new int?[] { init }, NewBox(rewritten, "IndexProbe"), y)?.ToString());
            }
        }
    }

    [Fact]
    public void M3_NL11_ComplexTargets_BoolAndEnumCompounds_RuntimeEquivalence()
    {
        var src = """
            public enum Color { Red, Green, Blue }
            public class Box
            {
                public bool? B { get; set; }
                public Color? E { get; set; }
            }
            public class C
            {
                // Enum arguments arrive as int (-1 == null) to keep reflection
                // invocation independent of emitted-assembly type identity.
                public static (bool?, bool?, bool?, Color?, Color?, Color?) F(
                    Box b, bool? init, bool? x, int eInit, int y)
                {
                    Color? ev = y < 0 ? null : (Color?)y;
                    b.B = init;
                    b.B &= x;
                    var bAnd = b.B;
                    b.B = init;
                    b.B |= x;
                    var bOr = b.B;
                    b.B = init;
                    b.B ^= x;
                    var bXor = b.B;
                    b.E = eInit < 0 ? null : (Color?)eInit;
                    b.E &= ev;
                    var eAnd = b.E;
                    b.E = eInit < 0 ? null : (Color?)eInit;
                    b.E |= ev;
                    var eOr = b.E;
                    b.E = eInit < 0 ? null : (Color?)eInit;
                    b.E ^= ev;
                    var eXor = b.E;
                    return (bAnd, bOr, bXor, eAnd, eOr, eXor);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.Equal(6, result.Statistics.Nullable.ComplexRmwSites);
        Assert.Equal(6, result.Statistics.Nullable.TempsTargetRole);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (bool? init in new bool?[] { null, false, true })
        {
            foreach (bool? x in new bool?[] { null, false, true })
            {
                foreach (int eInit in new[] { -1, 0, 1, 2 })
                {
                    foreach (int y in new[] { -1, 0, 1, 2 })
                    {
                        Assert.Equal(
                            InvokeStatic(original, "F",
                                NewBox(original, "Box"), init, x, eInit, y)?.ToString(),
                            InvokeStatic(rewritten, "F",
                                NewBox(rewritten, "Box"), init, x, eInit, y)?.ToString());
                    }
                }
            }
        }
    }

    [Fact]
    public void M3_ComplexStaticProperty_NoReceiverTemp_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                private static int? _sp;
                public static int? SP
                {
                    get { return _sp; }
                    set { _sp = value; }
                }
                public static int? F(int? init, int? y)
                {
                    SP = init;
                    SP++;
                    ++SP;
                    return SP += y;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var stats = result.Statistics.Nullable;
        Assert.Equal(3, stats.ComplexRmwSites);
        Assert.Equal(0, stats.TempsReceiverRole);
        Assert.DoesNotContain("__cs2j_n_r", Compact(result.OutputCode));

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? init in new int?[] { null, 100 })
        {
            foreach (int? y in new int?[] { null, -5 })
            {
                Assert.Equal(
                    InvokeStatic(original, "F", init, y),
                    InvokeStatic(rewritten, "F", init, y));
            }
        }
    }

    [Fact]
    public void M3_ComplexTargets_TwoValuePostfixSites_SharedAnchor_Ordering()
    {
        var src = """
            public class Box
            {
                private int? _p;
                public Box(int? p) { _p = p; }
                public int? P
                {
                    get { return _p; }
                    set { _p = value; }
                }
            }
            public class C
            {
                // Two value-position complex postfix sites share one anchor
                // statement; neither consumes the anchor, so both hoists
                // splice in visit (inner-to-outer) order.
                public static (int? old1, int? old2, int? after1, int? after2) F(Box x, Box y)
                {
                    var t = (x.P++, y.P++);
                    return (t.Item1, t.Item2, x.P, y.P);
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var stats = result.Statistics.Nullable;
        Assert.Equal(2, stats.ComplexRmwSites);
        Assert.Equal(2, stats.LiftedIncrementPostfixSites);
        Assert.Equal(2, stats.TempsTargetRole);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? x in new int?[] { null, 1 })
        {
            foreach (int? y in new int?[] { null, 2 })
            {
                Assert.Equal(
                    InvokeStatic(original, "F",
                        NewBox(original, "Box", x), NewBox(original, "Box", y))?.ToString(),
                    InvokeStatic(rewritten, "F",
                        NewBox(rewritten, "Box", x), NewBox(rewritten, "Box", y))?.ToString());
            }
        }
    }

    [Fact]
    public void M3_ComplexTargets_RefReturning_InitOnly_QualifiedField_NamedArgument_Still5355()
    {
        var sources = new[]
        {
            // ref-returning property: the write would alias storage, not an
            // invokable get/set pair.
            """
            public class Box
            {
                private int? _p = 1;
                public ref int? RefP { get { return ref _p; } }
            }
            public class C
            {
                public static void F(Box b) { b.RefP++; }
            }
            """,
            // init-only setter inside the constructor: get/init is not a
            // get/set RMW pair.
            """
            public class C
            {
                public int? P { get; init; }
                public C()
                {
                    P++;
                }
            }
            """,
            // qualified field stays a simple-target handoff (C17), not a
            // property accessor expansion.
            """
            public class Box { public int? F; }
            public class C
            {
                public static void M(Box b) { b.F++; }
            }
            """,
            // named indexer arguments cannot be rebuilt positionally.
            """
            public class Box
            {
                public int? this[int i] { get => 0; set { } }
            }
            public class C
            {
                public static void M(Box b) { b[i: 0]++; }
            }
            """,
            """
            using System.Linq;
            using System.Collections.Generic;
            public class C
            {
                public int? P { get; set; }
                public static IEnumerable<int?> F(IEnumerable<C> xs)
                    => from c in xs select c.P++;
            }
            """,
            """
            public class C
            {
                public int? P { get; set; }
                public static System.Func<int?> F(C c) => () => c.P++;
            }
            """,
            // Attribute named argument: input itself is CS0655 (nullable
            // named attribute arguments are illegal), yet the lifted binary
            // still binds and must be reported rather than half-rewritten.
            """
            using System;
            public class AAttribute : Attribute { public int? X { get; set; } }
            public class C
            {
                public static int? Get() => 1;
                [A(X = Get() + 1)]
                public void M() { }
            }
            """,
        };

        foreach (var src in sources)
        {
            var result = Run(src, fullFrameworkReferences: true);

            Assert.False(result.Changed);
            Assert.Equal(src, result.OutputCode);
            var error = Assert.Single(result.Diagnostics, d =>
                d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
            Assert.True(error.Line > 0 && error.Column > 0);
            Assert.Contains(
                result.Statistics.Nullable.DeferredByReason,
                kv => kv.Key == "CS2J5355:unliftable-context" && kv.Value == 1);
        }
    }

    [Fact]
    public void M2_NL10_PostfixInExpressionBody_NoAnchor_5355_Rollback()
    {
        var src = """
            public class C
            {
                public static int? F(int? x) => x++;
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }

    // ════════════════════════════════════════════════════════════════════
    // M2: NL-11 lifted compound assignment (decision 16)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M2_NL11_ArithmeticCompound_RuntimeTruthTable()
    {
        int?[] values = [null, 3, 7];
        foreach (var op in new[] { "+", "-", "*", "%" })
        {
            var src = $$"""
                public class C
                {
                    public static int? F(int? a, int? b)
                    {
                        int? x = a;
                        x {{op}}= b;
                        return x;
                    }
                }
                """;

            var result = Run(src);
            Assert.True(result.Changed);
            Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementCompoundSites);
            AssertOutputCompiles(result.OutputCode);
            Assert.Contains(
                "x=(x.HasValue&&b.HasValue?(int?)(x.Value" + op + "b.Value):(int?)null)",
                Compact(result.OutputCode));

            var original = CompileAndLoad(src);
            var rewritten = CompileAndLoad(result.OutputCode);
            foreach (var a in values)
            {
                foreach (var b in values)
                {
                    Assert.Equal(
                        InvokeStatic(original, "F", a, b),
                        InvokeStatic(rewritten, "F", a, b));
                }
            }
        }
    }

    [Fact]
    public void M2_NL11_OneSidedLiteralCompound_GuardDropsRhsHasValue_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int? F(int? a)
                {
                    int? x = a;
                    x -= 1;
                    return x;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains("x=(x.HasValue?(int?)(x.Value-1):(int?)null)", compact);
        Assert.DoesNotContain("1.HasValue", compact);
        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementCompoundSites);

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? v in new int?[] { null, 5 })
        {
            Assert.Equal(
                InvokeStatic(original, "F", v),
                InvokeStatic(rewritten, "F", v));
        }
    }

    [Fact]
    public void M2_NL11_UnstableRhs_HoistedOnce_EvaluatedEvenWhenTargetNull()
    {
        var src = """
            public class C
            {
                public static int Calls;
                public static int? R() { Calls++; return 10; }

                public static int? F(int? a)
                {
                    int? x = a;
                    x += R();
                    return x;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_n_b1=R()", compact);
        Assert.Contains(
            "x=(x.HasValue&&__cs2j_n_b1.HasValue?(int?)(x.Value+__cs2j_n_b1.Value):(int?)null)",
            compact);

        var asm = CompileAndLoad(result.OutputCode);
        var type = asm.GetType("C")!;
        Assert.Null(InvokeStatic(asm, "F", new object?[] { null }));
        Assert.Equal(1, type.GetField("Calls")!.GetValue(null));
        Assert.Equal(15, InvokeStatic(asm, "F", (object?)5));
        Assert.Equal(2, type.GetField("Calls")!.GetValue(null));
    }

    [Fact]
    public void M2_NL11_BoolCompound_AndOrXor_RuntimeTruthTable()
    {
        bool?[] values = [null, true, false];
        foreach (var (op, kind) in new[]
        {
            ("&", SyntaxKind.AmpersandToken),
            ("|", SyntaxKind.BarToken),
            ("^", SyntaxKind.CaretToken),
        })
        {
            var src = $$"""
                public class C
                {
                    public static bool? F(bool? a, bool? b)
                    {
                        bool? x = a;
                        x {{op}}= b;
                        return x;
                    }
                }
                """;

            var result = Run(src);
            Assert.True(result.Changed);
            Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementCompoundSites);
            AssertOutputCompiles(result.OutputCode);

            var original = CompileAndLoad(src);
            var rewritten = CompileAndLoad(result.OutputCode);
            foreach (var a in values)
            {
                foreach (var b in values)
                {
                    Assert.Equal(
                        InvokeStatic(original, "F", a, b),
                        InvokeStatic(rewritten, "F", a, b));
                }
            }
        }
    }

    [Fact]
    public void M2_NL11_EnumBitCompound_BackstopShape_RuntimeTruthTable()
    {
        int?[] values = [null, 1, 2, 4];
        foreach (var op in new[] { "&", "|", "^" })
        {
            var src = $$"""
                public enum Color { Red = 1, Green = 2, Blue = 4 }
                public class C
                {
                    public static string? F(Color? a, Color? b)
                    {
                        Color? x = a;
                        x {{op}}= b;
                        return x.HasValue ? x.Value.ToString() : null;
                    }
                }
                """;

            var result = Run(src);
            Assert.True(result.Changed);
            Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementCompoundSites);
            AssertOutputCompiles(result.OutputCode);
            Assert.Contains(
                "x=(x.HasValue&&b.HasValue?(global::Color?)((global::Color)((int)(x.Value)" + op
                + "(int)(b.Value))):(global::Color?)null)",
                Compact(result.OutputCode));

            var original = CompileAndLoad(src);
            var rewritten = CompileAndLoad(result.OutputCode);
            var originalEnum = original.GetType("Color")!;
            var rewrittenEnum = rewritten.GetType("Color")!;
            foreach (var a in values)
            {
                foreach (var b in values)
                {
                    var ea = a is int av ? System.Enum.ToObject(originalEnum, av) : null;
                    var eb = b is int bv ? System.Enum.ToObject(originalEnum, bv) : null;
                    var ra = a is int av2 ? System.Enum.ToObject(rewrittenEnum, av2) : null;
                    var rb = b is int bv2 ? System.Enum.ToObject(rewrittenEnum, bv2) : null;
                    Assert.Equal(
                        InvokeStatic(original, "F", ea, eb)?.ToString(),
                        InvokeStatic(rewritten, "F", ra, rb)?.ToString());
                }
            }
        }
    }

    [Fact]
    public void M2_NL11_ValueUsedCompound_SiteValueIsNewValue_RuntimeEquivalence()
    {
        var src = """
            public class C
            {
                public static int? Site(int? a, int? b)
                {
                    int? x = a;
                    var z = x += b;
                    return z;
                }

                public static int? Stored(int? a, int? b)
                {
                    int? x = a;
                    var z = x += b;
                    return x;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.Contains(
            "z=(x=(x.HasValue&&b.HasValue?(int?)(x.Value+b.Value):(int?)null))",
            Compact(result.OutputCode));

        var original = CompileAndLoad(src);
        var rewritten = CompileAndLoad(result.OutputCode);
        foreach (int? a in new int?[] { null, 3 })
        {
            foreach (int? b in new int?[] { null, 4 })
            {
                Assert.Equal(
                    InvokeStatic(original, "Site", a, b),
                    InvokeStatic(rewritten, "Site", a, b));
                Assert.Equal(
                    InvokeStatic(original, "Stored", a, b),
                    InvokeStatic(rewritten, "Stored", a, b));
            }
        }
    }

    [Fact]
    public void M2_NL10_11_CheckedContext_InnerOpsGetCheckedHelpers_NullBranchBare()
    {
        // Decision 18: C15 post-sweeps the guarded increment/compound live
        // branches to AddChecked helpers; the null branches stay bare.
        var src = """
            public class C
            {
                public static int? Inc(int? a)
                {
                    int? x = a;
                    checked { x++; }
                    return x;
                }

                public static int? Add(int? a)
                {
                    int? x = a;
                    checked { x += 2; }
                    return x;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        Assert.Contains(
            "__cs2j_n_o1.HasValue?(int?)(Cs2jUnsigned.UnsignedMath.AddCheckedInt32(__cs2j_n_o1.Value,1)):(int?)null",
            compact);
        Assert.Contains(
            "x.HasValue?(int?)(Cs2jUnsigned.UnsignedMath.AddCheckedInt32(x.Value,2)):(int?)null",
            compact);
        // Exactly two helper calls — none in a null branch.
        Assert.Equal(2, compact.Split("UnsignedMath").Length - 1);
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: idempotency, BOM, NL-14 staged residual gate
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M1_OutputIsIdempotent_SecondRun_NoChange_NoDiff()
    {
        var src = """
            public class C
            {
                public static object? F(int? a, int? b, int? c)
                {
                    return (object?)(a + b + c);
                }
            }
            """;

        var first = Run(src);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void M1_CleanFile_ByteIdentical_BomPreserved()
    {
        var src = "\uFEFFpublic class C { public int M(int a) => a + 1; }\n";

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.True(result.OutputCode[0] == '\uFEFF');
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void M2_ResidualOwnershipPredicate_FullScope_AllBuiltInShapesOwned()
    {
        var src = """
            public enum Color { Red, Green }
            public class C
            {
                public void M()
                {
                    int? a = 1, b = 2;
                    var add = a + b;
                    var neg = -a;
                    Color? e1 = Color.Red, e2 = Color.Green;
                    var eand = e1 & e2;
                    int? x = 1;
                    x++;
                    var g = a.GetValueOrDefault();
                    int y = (int)a;
                }
            }
            """;

        var h = BuildHarness(src);

        // M2 full scope: binary (incl. enum backstop), unary, increment,
        // GOV and unwrap/lifted conversions are all owned residuals.
        var residuals = NullableLiftRule.ScanResiduals(h.Model, h.Root);
        var owned = residuals.Select(r => r.Node.ToString()).ToList();
        var kinds = residuals.Select(r => r.Kind).ToList();

        Assert.Contains("a + b", owned);
        Assert.Contains("-a", owned);
        Assert.Contains("e1 & e2", owned);
        Assert.Contains("x++", owned);
        Assert.Contains("a.GetValueOrDefault()", owned);
        Assert.Contains("(int)a", owned);
        Assert.Contains(kinds, k => k == "binary");
        Assert.Contains(kinds, k => k == "unary");
        Assert.Contains(kinds, k => k == "increment");
        Assert.Contains(kinds, k => k == "get-value-or-default");
        Assert.Contains(kinds, k => k.StartsWith("conversion-", StringComparison.Ordinal));
    }

    [Fact]
    public void M2_ResidualGate_FullScope_SeverityFollowsStrictFlag()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    int? a = 1, b = 2;
                    var add = a + b;
                }
            }
            """;
        var h = BuildHarness(src);

        var gate = typeof(NullableLiftRule).GetMethod(
            "ApplyResidualGate",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(gate);

        NumericBoundaryDiagnostic RunGate(NumericBoundaryOptions options)
        {
            var stats = new NullableLiftStatistics();
            var diagnostics = new List<NumericBoundaryDiagnostic>();
            gate!.Invoke(null,
            [
                h.Model, h.Root, options, stats, diagnostics,
            ]);
            return Assert.Single(diagnostics);
        }

        var strict = RunGate(RuleOptions(strict: true));
        Assert.Equal(NumericBoundarySeverity.Error, strict.Severity);
        Assert.Equal(PreprocessDiagnosticCodes.NullableLiftResidual, strict.Code);

        var relaxed = RunGate(RuleOptions(strict: false));
        Assert.Equal(NumericBoundarySeverity.Warning, relaxed.Severity);
        Assert.Equal(PreprocessDiagnosticCodes.NullableLiftResidual, relaxed.Code);
    }

    [Fact]
    public void M1_InfoDiagnostics_5350SiteSummary_5351TempSummary()
    {
        var stable = Run("""
            public class C
            {
                public static object? F(int? a, int? b) => (object?)(a + b);
            }
            """);
        Assert.Contains(stable.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Info
            && d.Code == PreprocessDiagnosticCodes.NullableLiftSiteInfo);
        Assert.DoesNotContain(stable.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NullableLiftTempInfo);

        var withTemp = Run("""
            public class C
            {
                static int? Get() => 1;
                public static object? F(int? a)
                {
                    return (object?)(Get() + a);
                }
            }
            """);
        Assert.Contains(withTemp.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Info
            && d.Code == PreprocessDiagnosticCodes.NullableLiftTempInfo);
    }

    // ════════════════════════════════════════════════════════════════════
    // M1: site-level errors → NB-12 file rollback
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M1_Error_DynamicNullableSite_5352_Rollback()
    {
        var src = """
            public class C
            {
                public static void F()
                {
                    dynamic d = 1;
                    int? a = 2;
                    var x = d + a;
                }
            }
            """;

        var result = Run(src, fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftDynamic);
    }

    [Fact]
    public void M1_Error_UserDefinedLiftedOperator_5353_Rollback()
    {
        var src = """
            public struct Vec
            {
                public int X;
                public static Vec operator +(Vec a, Vec b) => default;
            }
            public class C
            {
                public static void F()
                {
                    Vec? a = default, b = default;
                    var c = a + b;
                }
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator);
    }

    [Fact]
    public void M1_Error_ExpressionTreeLiftedSite_5354_Rollback()
    {
        var src = """
            using System;
            using System.Linq.Expressions;
            public class C
            {
                public static Expression<Func<int?>> F(int? a) => () => a + 1;
            }
            """;

        var result = Run(src, fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftExpressionTree);
    }

    [Fact]
    public void M1_Error_UnstableLiftedInFieldInitializer_5355_Rollback()
    {
        var src = """
            public class C
            {
                public static int? Get() => 1;
                public static int? Z = Get() + 1;
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }

    [Fact]
    public void M1_Error_UnstableLiftedInConditionalArm_5355_Rollback()
    {
        var src = """
            public class C
            {
                public static int? Get() => 1;
                public static int? F(bool cond, int? b) => cond ? Get() + b : b;
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }

    [Fact]
    public void M1_Error_SpecialNumericNullable_5356_Rollback()
    {
        var src = """
            public class C
            {
                public static void F()
                {
                    System.Int128? a = 1;
                    var x = a + 1;
                }
            }
            """;

        var result = Run(src, fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftSpecialNumeric);
    }

    // ════════════════════════════════════════════════════════════════════
    // M3 B-block: CS2J5352-5356 error-site polish. Every site error carries
    // a non-zero line/column, a DeferredByReason key, rolls back the whole
    // file byte-for-byte, and never pairs with a CS2J5357 residual.
    // ════════════════════════════════════════════════════════════════════

    private static void AssertSiteError(
        NumericBoundaryFileResult result,
        string src,
        string code,
        string reasonKey,
        int expectedCount = 1)
    {
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        var errors = result.Diagnostics.Where(d =>
            d.Severity == NumericBoundarySeverity.Error && d.Code == code).ToList();
        Assert.Equal(expectedCount, errors.Count);
        Assert.All(errors, error => Assert.True(error.Line > 0 && error.Column > 0,
            $"{code} must report a non-zero position"));
        Assert.Contains(
            result.Statistics.Nullable.DeferredByReason,
            kv => kv.Key == reasonKey && kv.Value >= expectedCount);
        // A rolled-back site never also trips the residual gate.
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);
    }

    [Fact]
    public void M3_Error_DynamicNullableCompound_BothDirections_5352_Rollback()
    {
        var rhs = """
            public class C
            {
                public static void F()
                {
                    int? a = 2;
                    dynamic d = 1;
                    a += d;
                }
            }
            """;

        var rhsResult = Run(rhs, fullFrameworkReferences: true);
        AssertSiteError(rhsResult, rhs,
            PreprocessDiagnosticCodes.NullableLiftDynamic, "CS2J5352:dynamic");

        var lhs = """
            public class C
            {
                public static void F()
                {
                    dynamic d = 1;
                    int? a = 2;
                    d += a;
                }
            }
            """;

        var lhsResult = Run(lhs, fullFrameworkReferences: true);
        AssertSiteError(lhsResult, lhs,
            PreprocessDiagnosticCodes.NullableLiftDynamic, "CS2J5352:dynamic");

        // Non-nullable dynamic RMW is owned by other families: pass-through,
        // zero false positives from the nullable rule.
        var plain = Run("""
            public class C
            {
                public static void F()
                {
                    int a = 1;
                    dynamic d = 1;
                    a += d;
                }
            }
            """, fullFrameworkReferences: true);
        Assert.False(plain.Changed);
        Assert.DoesNotContain(plain.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NullableLiftDynamic);
    }

    [Fact]
    public void M3_Error_UserDefinedLiftedUnaryAndConversion_5353_Rollback()
    {
        var unary = """
            public struct Vec
            {
                public int X;
                public static Vec operator -(Vec a) => default;
            }
            public class C
            {
                public static void F()
                {
                    Vec? a = default;
                    var x = -a;
                }
            }
            """;

        var unaryResult = Run(unary);
        AssertSiteError(unaryResult, unary,
            PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
            "CS2J5353:user-defined-operator");

        var conversion = """
            public struct Src
            {
                public static implicit operator Dst(Src s) => default;
            }
            public struct Dst
            {
                public int X;
            }
            public class C
            {
                public static void F()
                {
                    Src? s = default;
                    var x = (Dst?)s;
                }
            }
            """;

        var conversionResult = Run(conversion);
        AssertSiteError(conversionResult, conversion,
            PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
            "CS2J5353:user-defined-operator");
    }

    [Fact]
    public void M3_Error_ExpressionTreeLiftedSite_5354_PositionAndKey()
    {
        // A lifted ++ inside an expression tree is CS0832 at the language
        // level; the compilable lifted shape inside a tree is the binary
        // below (source CS0832 inputs never reach a buildable pipeline).
        var src = """
            using System;
            using System.Linq.Expressions;
            public class C
            {
                public static Expression<Func<int?>> F(int? a) => () => a + 1;
            }
            """;

        var result = Run(src, fullFrameworkReferences: true);
        AssertSiteError(result, src,
            PreprocessDiagnosticCodes.NullableLiftExpressionTree,
            "CS2J5354:expression-tree");
    }

    [Fact]
    public void M3_Error_SpecialNumericNullable_FourDomains_5356_Rollback()
    {
        var sources = new (string Src, int Count)[]
        {
            ("""
            public class C
            {
                public static void F()
                {
                    System.Int128? a = 1;
                    var x = a + 1;
                }
            }
            """, 1),
            ("""
            public class C
            {
                public static void F()
                {
                    System.UInt128? a = 1;
                    var x = a + 1;
                }
            }
            """, 1),
            ("""
            public class C
            {
                public static void F()
                {
                    System.Half? h = (System.Half)1;
                    System.Half one = (System.Half)1;
                    var x = h + one;
                }
            }
            """, 1),
            ("""
            public class C
            {
                public static void F()
                {
                    nint? a = 1;
                    var neg = -a;
                    var add = a + 1;
                }
            }
            """, 2),
            ("""
            public class C
            {
                public static nint? P { get; set; }
                public static void F()
                {
                    P++;
                    P += 1;
                }
            }
            """, 2),
        };

        foreach (var (src, count) in sources)
        {
            var result = Run(src, fullFrameworkReferences: true);
            AssertSiteError(result, src,
                PreprocessDiagnosticCodes.NullableLiftSpecialNumeric,
                "CS2J5356:special-numeric", count);
        }
    }

    [Fact]
    public void M3_BuiltInNullableDomains_AllComplexShapes_ZeroErrorAudit()
    {
        var src = """
            public enum Color { Red, Blue }
            public class Box
            {
                public int? P { get; set; }
                public bool? B { get; set; }
                public Color? E { get; set; }
                public decimal? D { get; set; }
                public int? this[int i] { get => 0; set { } }
            }
            public class C
            {
                public static void F(Box b, int?[] arr, int? x, bool? q, Color? e, decimal? d)
                {
                    int? a = 1;
                    a += 2;
                    a++;
                    --a;
                    var z = a + x;
                    bool? bb = true;
                    bb &= q;
                    bb |= q;
                    bb ^= q;
                    bb = !bb;
                    Color? ee = Color.Red;
                    ee &= e;
                    ee++;
                    decimal? dd = 1m;
                    dd += d;
                    b.P += x;
                    b.P++;
                    ++b.P;
                    b.B &= q;
                    b.E |= e;
                    b.D += d;
                    b[0]++;
                    b[0] += x;
                    arr[0] += x;
                    arr[0]++;
                }
            }
            """;

        var result = Run(src, fullFrameworkReferences: true);

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error);
        foreach (var code in new[]
        {
            PreprocessDiagnosticCodes.NullableLiftDynamic,
            PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
            PreprocessDiagnosticCodes.NullableLiftExpressionTree,
            PreprocessDiagnosticCodes.NullableLiftComplexTarget,
            PreprocessDiagnosticCodes.NullableLiftSpecialNumeric,
        })
        {
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == code);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // M3 C-block: residual gate boundary counterexamples.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void M3_ResidualScan_BoundaryCounterexamples_NoResiduals()
    {
        var src = """
            using System;
            public class C
            {
                public static void F(int? a)
                {
                    var underlying = Nullable.GetUnderlyingType(typeof(int?));
                    var t = typeof(int?);
                    int? wrap = (int?)1;
                    var hasValue = a.HasValue;
                    var value = a.Value;
                    // Lifted binary whose text carries a support fingerprint:
                    // emitted by another family's support layer, exempt.
                    var fp = Cs2jUnsigned.UnsignedMath.AddCheckedInt32(1, 2) + a;
                }
            }
            """;

        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(Cs2jUnsigned.UnsignedMath).Assembly.Location),
        };
        var (tree, model) = ParseWithSemantics(
            src, fullFrameworkReferences: true, extraReferences: references);
        AssertNoCompileErrors(model);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var residuals = NullableLiftRule.ScanResiduals(model, root);

        Assert.Empty(residuals);
    }

    [Fact]
    public void M3_ResidualScan_ConditionalAccessHiddenInvocation_EnumeratedOnce()
    {
        // `T? x; x?.GetValueOrDefault()` does not compile (CS1061: the
        // conditional access exposes the UNDERLYING T's members), but the
        // bound WhenNotNull invocation hides inside the conditional-access
        // operation and shares its syntax node. The scanner must still
        // enumerate it (defense for malformed/partial trees), exactly once.
        var src = """
            public class C
            {
                public static int? Get() => 1;
                public static int? F() => Get()?.GetValueOrDefault();
            }
            """;

        var (tree, model) = ParseWithSemantics(src);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var residuals = NullableLiftRule.ScanResiduals(model, root);

        // The CS1061 error binding surfaces the shared-syntax node as a
        // lifted conversion rather than a resolvable GOV invocation; what
        // matters is that the hidden subtree is enumerated exactly once.
        var hidden = residuals
            .Where(r => r.Node.ToString() == "Get()?.GetValueOrDefault()")
            .ToList();
        Assert.Single(hidden);
    }

    [Fact]
    public void M3_ResidualGate_ImplicitLiftedConversion_PipelineDualState()
    {
        // NL-7/8 own explicit cast conversions only; an implicit lifted
        // S?→T? conversion at a declarator is an unowned (M4-class) shape:
        // relaxed runs warn and still emit, strict runs roll the file back.
        var src = """
            public class C
            {
                public static void F()
                {
                    int? a = 1;
                    var g = a + 2;
                    long? t = a;
                }
            }
            """;

        var relaxed = Run(src, RuleOptions(strict: false));
        Assert.True(relaxed.Changed);
        var warning = Assert.Single(relaxed.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Warning
            && d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);
        Assert.True(warning.Line == 0); // whole-file residual summary
        Assert.Contains("long?t=a", Compact(relaxed.OutputCode));
        Assert.Contains(relaxed.Statistics.Nullable.DeferredByReason,
            kv => kv.Key == PreprocessDiagnosticCodes.NullableLiftResidual);

        var strict = Run(src, RuleOptions(strict: true));
        Assert.False(strict.Changed);
        Assert.Equal(src, strict.OutputCode);
        Assert.Contains(strict.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);
    }

    [Fact]
    public void M3_ResidualGate_HiddenConditionalGov_MalformedInput_DualState()
    {
        // Malformed input (CS1061, see above) exercising the conditional-
        // access penetration through the whole pipeline: the owned lifted
        // binary is rewritten while the hidden GOV survives to the gate.
        var src = """
            public class C
            {
                public static int? Get() => 1;
                public static int? F()
                {
                    int? a = 1;
                    var g = a + 2;
                    return Get()?.GetValueOrDefault();
                }
            }
            """;

        var relaxed = Run(src, RuleOptions(strict: false));
        Assert.True(relaxed.Changed);
        Assert.Contains(relaxed.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Warning
            && d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);
        var compact = Compact(relaxed.OutputCode);
        Assert.Contains("(a.HasValue?(int?)(a.Value+2):(int?)null)", compact);
        Assert.Contains("Get()?.GetValueOrDefault()", compact);

        var strict = Run(src, RuleOptions(strict: true));
        Assert.False(strict.Changed);
        Assert.Equal(src, strict.OutputCode);
        Assert.Contains(strict.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);
    }

    // 引用类型 → T?（`(E?)object`）是拆箱/引用转换：null 必须保持 null。
    // 旧实现把它当值类型 lifted 转换，生成 `(E?)(E)(obj)`，对 null 抛 NRE
    // （regular-expressions RegexParserTests.Parse 388 例失败）。
    [Fact]
    public void LiftedConversion_FromReferenceSource_LeavesCastUntouched()
    {
        var src = """
            public enum E { A }

            public class C
            {
                public static E? F(object o) => (E?)o;
            }
            """;

        var result = Run(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    // ── null-literal lifted comparison on a user-defined-operator struct
    //    (GraphLayout CS2J5353 eradication; C# spec: a null operand never
    //    calls the user-defined operator, so `x != null` ≡ `x.HasValue`) ──

    private const string UserDefinedPointStruct = """
        public struct Point
        {
            public int X;
            public Point(int x) { X = x; }
            public static bool operator ==(Point a, Point b) => a.X == b.X;
            public static bool operator !=(Point a, Point b) => a.X != b.X;
            public override bool Equals(object? o) => o is Point p && p.X == X;
            public override int GetHashCode() => X;
        }
        """;

    [Fact]
    public void M1_UserDefinedLifted_NullLiteralEquality_RewritesToHasValue()
    {
        var src = $$"""
            {{UserDefinedPointStruct}}

            public class C
            {
                public static bool NotNull(int mode)
                {
                    Point? q = mode == 0 ? (Point?)null : new Point(5);
                    if (q != null)
                    {
                        return true;
                    }

                    return false;
                }

                public static bool IsNull(int mode)
                {
                    Point? q = mode == 0 ? (Point?)null : new Point(5);
                    if (q == null)
                    {
                        return true;
                    }

                    return false;
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(result.OutputCode);
        Assert.Contains("if((q.HasValue))", compact);
        Assert.Contains("if((!q.HasValue))", compact);
        AssertOutputCompiles(result.OutputCode);

        foreach (var code in new[] { src, result.OutputCode })
        {
            var type = CompileAndLoad(code).GetType("C")!;
            var notNull = type.GetMethod("NotNull")!;
            var isNull = type.GetMethod("IsNull")!;
            Assert.False((bool)notNull.Invoke(null, new object?[] { 0 })!);
            Assert.True((bool)notNull.Invoke(null, new object?[] { 1 })!);
            Assert.True((bool)isNull.Invoke(null, new object?[] { 0 })!);
            Assert.False((bool)isNull.Invoke(null, new object?[] { 1 })!);
        }
    }

    [Fact]
    public void M1_Error_UserDefinedLifted_NonNullLiteralComparison_5353_Rollback()
    {
        var src = $$"""
            {{UserDefinedPointStruct}}

            public class C
            {
                public static bool F(Point? a, Point? b) => a != b;
            }
            """;

        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator);
    }
}
