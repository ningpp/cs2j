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
    public void M1_AllLiftedShapes_M1OwnedSitesGuarded_M2ShapesPassThrough()
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

        // M2 shapes pass through untouched: enum bit ops/~, GOV, conversions,
        // increment/compound, and never trip the staged NL-14 residual gate.
        Assert.Contains("eand=e1&e2", compact);
        Assert.Contains("enot=~e1", compact);
        Assert.Contains("ni.GetValueOrDefault()", compact);
        Assert.Contains("ni.GetValueOrDefault(5)", compact);
        Assert.Contains("y=(int)x", compact);
        Assert.Contains("z=(int?)nl", compact);
        Assert.Contains("x++", compact);
        Assert.Contains("++x", compact);
        Assert.Contains("x-=1", compact);

        // Every operand here is a stable local/literal → zero temps.
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
        Assert.Equal(0, stats.TempsLifted);
        Assert.Equal(0, stats.GetValueOrDefaultSites + stats.UnwrapConversions
            + stats.LiftedConversions + stats.LiftedIncrementSites
            + stats.NullableEnumBackstopSites);
        Assert.False(stats.DeferredByReason.Keys.Any(k =>
            k.StartsWith(PreprocessDiagnosticCodes.NullableLiftResidual, StringComparison.Ordinal)));
        Assert.Equal(1, result.Statistics.FilesTransformed);
    }

    [Fact]
    public void M1_DecimalNullable_ArithmeticGuarded_BareInnerKept_CompoundPassesThrough()
    {
        // Decision 19: decimal? arithmetic gets the HasValue guard in M1 while
        // the INNER bare decimal op stays for the C34 downstream sweep;
        // lifted compound assignment is NL-11 (M2) and passes through.
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
        Assert.Contains("c*=3m", compact);
        Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Equal(0, result.Statistics.Nullable.LiftedIncrementCompoundSites);
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
    public void Coexist_LiftedIncrement_C17Handoff_CS2J5335_StillFires_UntilM2()
    {
        // M2 hands the simple lifted increment from C17 to NL-10; in M0 the
        // established CS2J5335 error + NB-12 rollback behavior is unchanged,
        // and C22 never rewrites (so output bytes stay identical).
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
    public void M1_CoexistsWith_C15Checked_C22GuardFirst_C15NullableWrapCastRejected_M2SeamRollback()
    {
        // M1/M2 seam (decision 18 implementation is deferred to M2): C22 runs
        // at Order=15 and explicitizes the lifted site into
        // `a.HasValue && b.HasValue ? (int?)(a.Value + b.Value) : (int?)null`.
        // C15 (Order=20) then sweeps checked arithmetic, but its cast visitor
        // still classifies the guard's nullable-wrapping conversion `(int?)…`
        // as a lifted-checked site (CS2J5325 "nullable lifted checked
        // arithmetic cannot be translated yet") — it does not yet descend into
        // the guard's WhenTrue branch to emit AddCheckedInt32. Per NB-12 the
        // whole file rolls back. M2 flips this into a successful helper sweep
        // inside the live branch with the null branch untouched.
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

        // C22 did classify and count its site before the family rollback.
        Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryNullableLifted);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
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
    public void M1_ResidualOwnershipPredicate_OnlyM1ShapesOwned()
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
        var shared = new NumericBoundaryAnalyzer(h.Model, projectDefaultChecked: false);
        var analyzer = new NullableLiftAnalyzer(h.Model, RuleOptions(), shared);

        var owned = new List<string>();
        foreach (var node in h.Root.DescendantNodes())
        {
            if (h.Model.GetOperation(node) is { } operation
                && NullableLiftRule.IsM1OwnedResidual(analyzer, node, operation))
            {
                owned.Add(node.ToString());
            }
        }

        Assert.Equal(2, owned.Count);
        Assert.Contains("a + b", owned);
        Assert.Contains("-a", owned);
    }

    [Fact]
    public void M1_ResidualGate_SeverityFollowsStrictFlag()
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
            var shared = new NumericBoundaryAnalyzer(h.Model, projectDefaultChecked: false);
            gate!.Invoke(null,
            [
                h.Model, h.Root, options, stats, diagnostics, shared,
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
}
