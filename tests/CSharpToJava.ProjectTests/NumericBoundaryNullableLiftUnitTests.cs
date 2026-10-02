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
        bool fullFrameworkReferences = false)
    {
        var (_, model) = ParseWithSemantics(src, fullFrameworkReferences);
        return new NumericBoundaryPreprocessor().Rewrite(src, model, options ?? RuleOptions());
    }

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

        references.Add(MetadataReference.CreateFromFile(typeof(Cs2jUnsigned.UnsignedMath).Assembly.Location));

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
    public void M0_AllLiftedShapes_ZeroRewrite_ByteIdentical_NoDiagnostics()
    {
        var result = Run(LiftedCrossProductSource);

        Assert.False(result.Changed);
        Assert.Equal(LiftedCrossProductSource, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Empty(result.Diagnostics);
        AssertOutputCompiles(result.OutputCode);

        // M0 discipline: every C22 bucket stays zero.
        var stats = result.Statistics.Nullable;
        Assert.Equal(0, stats.LiftedBinarySites + stats.LiftedUnarySites
            + stats.LiftedRelationalSites + stats.LiftedEqualitySites
            + stats.LiftedBoolLogicSites + stats.GetValueOrDefaultSites
            + stats.UnwrapConversions + stats.LiftedConversions
            + stats.LiftedIncrementSites + stats.NullableEnumBackstopSites
            + stats.GuardedCheckedInnerSites + stats.TempsLifted);
        Assert.Empty(stats.DeferredByReason);
    }

    [Fact]
    public void M0_DecimalNullable_LiftedSite_PassesThroughUnchanged()
    {
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

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        AssertOutputCompiles(result.OutputCode);
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
}
