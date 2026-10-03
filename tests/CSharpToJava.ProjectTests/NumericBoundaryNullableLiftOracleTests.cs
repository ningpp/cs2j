using System.Reflection;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.Nullable;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C22 M2 native-oracle matrix (design §7.1-13): the paired Native*/Guarded*
/// members in the SampleNumericBoundaryLib/Nullable fixture are compiled in
/// three assemblies — the ORIGINAL source (C# compiler lifted truth plus the
/// hand-written guarded sample) and the C22-rewritten source — and every cell
/// of the null/value grids (including boundary values, the absorption cells
/// and the throwing unwrap cells) is invoked in all four variants. Every
/// fixture file must additionally rewrite with zero CS2J5357 residuals, fill
/// the §4.7 statistics buckets and emit the M2 CS2J5350/CS2J5351 info text.
/// </summary>
public class NumericBoundaryNullableLiftOracleTests
{
    private const string FixtureNamespace = "SampleNumericBoundaryLib.Nullable";

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution",
            "SampleNumericBoundaryLib", "Nullable");

    private static NumericBoundaryOptions RuleOptions() =>
        new()
        {
            EnableNullable = true,
            EnableIncrementTargets = false,
            EnableDecimalOps = false,
            Strict = true,
        };

    // ── harness ──

    private static (SyntaxTree Tree, SemanticModel Model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
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
        var compilation = CSharpCompilation.Create(
            "c22-oracle-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    private static NumericBoundaryFileResult RewriteFixture(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(FixtureDir, fileName));
        var (_, model) = ParseWithSemantics(source);
        var errors = model.SyntaxTree.GetDiagnostics()
            .Concat(model.Compilation.GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            $"fixture {fileName} must compile standalone:" + Environment.NewLine
            + string.Join(Environment.NewLine, errors));
        return new NumericBoundaryPreprocessor().Rewrite(source, model, RuleOptions());
    }

    private static Assembly CompileAndLoad(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
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
        var compilation = CSharpCompilation.Create(
            "c22-oracle-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "oracle emit failed:" + Environment.NewLine
            + string.Join(Environment.NewLine, emit.Diagnostics.Select(d => d.ToString()))
            + Environment.NewLine + code);
        return Assembly.Load(ms.ToArray());
    }

    private static object? Invoke(
        Assembly assembly, string typeName, string method, params object?[] args)
    {
        var type = assembly.GetType(typeName)!;
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

    /// <summary>Enum results normalize to their underlying int so enums from
    /// separately emitted assemblies compare by value; tuples normalize to
    /// "old,new" strings.</summary>
    private static object? Norm(object? value) => value switch
    {
        null => null,
        Enum e => Convert.ToInt32(e),
        ValueTuple<int?, int?> t => $"{t.Item1?.ToString() ?? "null"}/{t.Item2?.ToString() ?? "null"}",
        _ => value,
    };

    private static void Agree1(
        Assembly original, Assembly rewritten, string typeName,
        string native, string guarded, object? a)
    {
        Assert.Equal(Norm(Invoke(original, typeName, native, a)),
            Norm(Invoke(original, typeName, guarded, a)));
        Assert.Equal(Norm(Invoke(original, typeName, native, a)),
            Norm(Invoke(rewritten, typeName, native, a)));
        Assert.Equal(Norm(Invoke(original, typeName, native, a)),
            Norm(Invoke(rewritten, typeName, guarded, a)));
    }

    private static void Agree2(
        Assembly original, Assembly rewritten, string typeName,
        string native, string guarded, object? a, object? b)
    {
        var expected = Norm(Invoke(original, typeName, native, a, b));
        Assert.Equal(expected, Norm(Invoke(original, typeName, guarded, a, b)));
        Assert.Equal(expected, Norm(Invoke(rewritten, typeName, native, a, b)));
        Assert.Equal(expected, Norm(Invoke(rewritten, typeName, guarded, a, b)));
    }

    private static readonly string ArithmeticType = $"{FixtureNamespace}.NullableArithmeticSamples";
    private static readonly string RelationalType = $"{FixtureNamespace}.NullableRelationalSamples";
    private static readonly string EqualityType = $"{FixtureNamespace}.NullableEqualitySamples";
    private static readonly string BoolType = $"{FixtureNamespace}.NullableBoolLogicSamples";
    private static readonly string GovType = $"{FixtureNamespace}.NullableGetValueOrDefaultSamples";
    private static readonly string ConversionType = $"{FixtureNamespace}.NullableConversionSamples";
    private static readonly string EnumType = $"{FixtureNamespace}.NullableEnumBackstopSamples";
    private static readonly string IncrementType = $"{FixtureNamespace}.NullableIncrementSamples";
    private static readonly string ColorEnumType = $"{FixtureNamespace}.NullableLiftColor";
    private static readonly string ProbeType = $"{FixtureNamespace}.NullableDefaultProbe";

    // ── 1. arithmetic / unary grid (36 cells) ──

    [Fact]
    public void Oracle_ArithmeticAndUnary_NullValueBoundaryGrid_AllFourVariantsAgree()
    {
        var fileName = "NullableArithmetic.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        foreach (int? a in new int?[] { null, int.MaxValue, 2 })
        {
            foreach (int? b in new int?[] { null, int.MaxValue, 2 })
            {
                Agree2(original, rewritten, ArithmeticType,
                    "NativeAdd", "GuardedAdd", a, b);
            }
        }

        foreach (int? a in new int?[] { null, 1, int.MaxValue })
        {
            foreach (var b in new[] { 1, -2 })
            {
                Agree2(original, rewritten, ArithmeticType,
                    "NativeAddOneSided", "GuardedAddOneSided", a, b);
            }
        }

        foreach (long? a in new long?[] { null, long.MaxValue, 5L })
        {
            foreach (long? b in new long?[] { null, long.MaxValue, 5L })
            {
                Agree2(original, rewritten, ArithmeticType,
                    "NativeSubtract", "GuardedSubtract", a, b);
            }
        }

        foreach (double? a in new double?[] { null, 1.5, -2.0 })
        {
            foreach (var b in new[] { 2.0, -3.0 })
            {
                Agree2(original, rewritten, ArithmeticType,
                    "NativeMultiply", "GuardedMultiply", a, b);
            }
        }

        foreach (int? a in new int?[] { null, -7, 42 })
        {
            Agree1(original, rewritten, ArithmeticType,
                "NativeNegate", "GuardedNegate", a);
        }

        foreach (long? a in new long?[] { null, 42L, -9L })
        {
            Agree1(original, rewritten, ArithmeticType,
                "NativeUnaryPlus", "GuardedUnaryPlus", a);
        }

        // Pinned counter-intuitive boundary cells.
        Assert.Equal(-2, Invoke(rewritten, ArithmeticType, "NativeAdd",
            int.MaxValue, int.MaxValue));
        Assert.Equal(-2, Invoke(original, ArithmeticType, "GuardedAdd",
            int.MaxValue, int.MaxValue));
    }

    // ── 2. relational grid (42 cells; null>=null pinned false) ──

    [Fact]
    public void Oracle_Relational_NullValueGrid_NullGeNullFalse_AllVariantsAgree()
    {
        var fileName = "NullableRelational.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        int?[] values = [null, 1, 2];
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                Agree2(original, rewritten, RelationalType,
                    "NativeGreaterThanOrEqual", "GuardedGreaterThanOrEqual", a, b);
                Agree2(original, rewritten, RelationalType,
                    "NativeGreaterThan", "GuardedGreaterThan", a, b);
                Agree2(original, rewritten, RelationalType,
                    "NativeLessThanOrEqual", "GuardedLessThanOrEqual", a, b);
                Agree2(original, rewritten, RelationalType,
                    "NativeLessThan", "GuardedLessThan", a, b);
            }
        }

        foreach (int? a in values)
        {
            Agree2(original, rewritten, RelationalType,
                "NativeGreaterThanOrEqualOneSided", "GuardedGreaterThanOrEqualOneSided", a, 1);
            Agree2(original, rewritten, RelationalType,
                "NativeLessThanOneSided", "GuardedLessThanOneSided", a, 1);
        }

        Assert.False((bool)Invoke(rewritten, RelationalType,
            "NativeGreaterThanOrEqual", null, null)!);
        Assert.False((bool)Invoke(original, RelationalType,
            "GuardedGreaterThanOrEqual", null, null)!);
    }

    // ── 3. equality grid (36 cells; null==null pinned true) ──

    [Fact]
    public void Oracle_Equality_NineCellGrids_DoubleNullTrue_AllVariantsAgree()
    {
        var fileName = "NullableEquality.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        int?[] values = [null, 0, 1];
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                Agree2(original, rewritten, EqualityType,
                    "NativeEqual", "GuardedEqual", a, b);
                Agree2(original, rewritten, EqualityType,
                    "NativeNotEqual", "GuardedNotEqual", a, b);
            }

            Agree2(original, rewritten, EqualityType,
                "NativeEqualOneSided", "GuardedEqualOneSided", a, 0);
            Agree2(original, rewritten, EqualityType,
                "NativeNotEqualOneSided", "GuardedNotEqualOneSided", a, 0);
        }

        foreach (bool? a in new bool?[] { null, false, true })
        {
            Agree1(original, rewritten, EqualityType,
                "NativeBoolEqualTrue", "GuardedBoolEqualTrue", a);
        }

        foreach (int? a in values)
        {
            Agree1(original, rewritten, EqualityType, "NativeIsNull", "GuardedIsNull", a);
            Agree1(original, rewritten, EqualityType, "NativeIsNotNull", "GuardedIsNotNull", a);
        }

        Assert.True((bool)Invoke(rewritten, EqualityType, "NativeEqual", null, null)!);
        Assert.True((bool)Invoke(original, EqualityType, "GuardedEqual", null, null)!);
    }

    // ── 4. bool? three-valued logic (30 cells; absorption pinned) ──

    [Fact]
    public void Oracle_BoolLogic_FullThreeValuedTable_AbsorptionCells_AllVariantsAgree()
    {
        var fileName = "NullableBoolLogic.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        bool?[] values = [null, false, true];
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                Agree2(original, rewritten, BoolType, "NativeAnd", "GuardedAnd", a, b);
                Agree2(original, rewritten, BoolType, "NativeOr", "GuardedOr", a, b);
                Agree2(original, rewritten, BoolType, "NativeXor", "GuardedXor", a, b);
            }

            Agree1(original, rewritten, BoolType, "NativeNot", "GuardedNot", a);
        }

        // false & null == false (false absorbs); true | null == true; null&null == null.
        Assert.False((bool?)Invoke(rewritten, BoolType, "NativeAnd", false, null));
        Assert.True((bool?)Invoke(rewritten, BoolType, "NativeOr", true, null));
        Assert.Null(Invoke(rewritten, BoolType, "NativeAnd", null, null));
    }

    // ── 5. GOV grids + eager single-evaluation default (6 + 4 scenarios) ──

    [Fact]
    public void Oracle_GetValueOrDefault_GridsAndSingleEvaluationDefault_AllVariantsAgree()
    {
        var fileName = "NullableGetValueOrDefault.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        foreach (int? a in new int?[] { null, 0, 42 })
        {
            Agree1(original, rewritten, GovType, "NativeGov", "GuardedGov", a);
            Agree2(original, rewritten, GovType,
                "NativeGovDefault", "GuardedGovDefault", a, 5);
        }

        // The default argument evaluates eagerly even when HasValue is true,
        // exactly once, in both the original and the rewritten assembly.
        foreach (var asm in new[] { original, rewritten })
        {
            foreach (var method in new[] { "NativeGovSideEffect", "GuardedGovSideEffect" })
            {
                var probeType = asm.GetType(ProbeType)!;
                var probeHas = Activator.CreateInstance(probeType);
                Assert.Equal(42, Invoke(asm, GovType, method, (object?)42, probeHas));
                Assert.Equal(1, (int)probeType.GetProperty("Evaluations")!.GetValue(probeHas)!);

                var probeNull = Activator.CreateInstance(probeType);
                Assert.Equal(7, Invoke(asm, GovType, method, null, probeNull));
                Assert.Equal(1, (int)probeType.GetProperty("Evaluations")!.GetValue(probeNull)!);
            }
        }

        // d-role temp bookkeeping and info text.
        Assert.Equal(3, result.Statistics.Nullable.GetValueOrDefaultSites);
        Assert.Equal(1, result.Statistics.Nullable.TempsDefaultRole);
        Assert.Equal(1, result.Statistics.Nullable.TempsLifted);
        var siteInfo = Assert.Single(result.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NullableLiftSiteInfo);
        Assert.Contains("3 gov", siteInfo.Message);
        var tempInfo = Assert.Single(result.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NullableLiftTempInfo);
        Assert.Contains("1 d", tempInfo.Message);
    }

    // ── 6. unwrap throws + lifted conversion grids (8 + 10 cells) ──

    [Fact]
    public void Oracle_Conversions_UnwrapBothWaysThrow_LiftedGridsWrap_AllVariantsAgree()
    {
        var fileName = "NullableConversions.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        foreach (var asm in new[] { original, rewritten })
        {
            Assert.Equal(42, Invoke(asm, ConversionType, "NativeUnwrapCast", (object?)42));
            Assert.Equal(42, Invoke(asm, ConversionType, "GuardedUnwrapCast", (object?)42));
            Assert.Equal(42, Invoke(asm, ConversionType, "NativeUnwrapMember", (object?)42));
            Assert.Equal(42, Invoke(asm, ConversionType, "GuardedUnwrapMember", (object?)42));
            foreach (var method in new[]
            {
                "NativeUnwrapCast", "NativeUnwrapMember",
                "GuardedUnwrapCast", "GuardedUnwrapMember",
            })
            {
                Assert.Throws<InvalidOperationException>(
                    () => Invoke(asm, ConversionType, method, (object?)null));
            }
        }

        foreach (int? a in new int?[] { null, 42, -1 })
        {
            Agree1(original, rewritten, ConversionType, "NativeToLong", "GuardedToLong", a);
        }

        foreach (int? a in new int?[] { null, 255, 256, 1 })
        {
            Agree1(original, rewritten, ConversionType, "NativeToByte", "GuardedToByte", a);
        }

        foreach (double? a in new double?[] { null, 2.9, -3.7 })
        {
            Agree1(original, rewritten, ConversionType, "NativeToInt", "GuardedToInt", a);
        }

        // Unchecked narrowing wrap pinned through the rewritten guard.
        Assert.Equal((byte)0, Invoke(rewritten, ConversionType, "NativeToByte", (int?)256));
        Assert.Equal(1, result.Statistics.Nullable.UnwrapConversions);
        Assert.Equal(3, result.Statistics.Nullable.LiftedConversions);
    }

    // ── 7. nullable enum backstop grid (40 cells) ──

    [Fact]
    public void Oracle_EnumBackstop_BitGridAndNotAndConversion_AllVariantsAgree()
    {
        var fileName = "NullableEnumBackstop.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        int?[] values = [null, 0, 1, 2, 3];
        foreach (var a in values)
        {
            foreach (var b in values)
            {
                AgreeEnum2(original, rewritten, "NativeBitAnd", "GuardedBitAnd", a, b);
                AgreeEnum2(original, rewritten, "NativeBitOr", "GuardedBitOr", a, b);
            }

            AgreeEnum1(original, rewritten, "NativeBitNot", "GuardedBitNot", a);
            AgreeEnum1(original, rewritten, "NativeEnumToInt", "GuardedEnumToInt", a);
        }

        // null propagation cells pinned in the rewritten assembly.
        Assert.Null(Invoke(rewritten, EnumType, "NativeBitAnd",
            EnumArg(rewritten, null), EnumArg(rewritten, 1)));
        Assert.Null(Invoke(rewritten, EnumType, "NativeBitNot", (object?)null));
        Assert.Null(Invoke(rewritten, EnumType, "NativeEnumToInt", (object?)null));
        Assert.Equal(3, result.Statistics.Nullable.NullableEnumBackstopSites);
    }

    private static object? EnumArg(Assembly assembly, int? value) =>
        value is int v ? Enum.ToObject(assembly.GetType(ColorEnumType)!, v) : null;

    private static void AgreeEnum2(
        Assembly original, Assembly rewritten, string native, string guarded,
        int? a, int? b)
    {
        var expected = Norm(Invoke(original, EnumType, native,
            EnumArg(original, a), EnumArg(original, b)));
        Assert.Equal(expected, Norm(Invoke(original, EnumType, guarded,
            EnumArg(original, a), EnumArg(original, b))));
        Assert.Equal(expected, Norm(Invoke(rewritten, EnumType, native,
            EnumArg(rewritten, a), EnumArg(rewritten, b))));
        Assert.Equal(expected, Norm(Invoke(rewritten, EnumType, guarded,
            EnumArg(rewritten, a), EnumArg(rewritten, b))));
    }

    private static void AgreeEnum1(
        Assembly original, Assembly rewritten, string native, string guarded, int? a)
    {
        var expected = Norm(Invoke(original, EnumType, native, EnumArg(original, a)));
        Assert.Equal(expected, Norm(Invoke(original, EnumType, guarded, EnumArg(original, a))));
        Assert.Equal(expected, Norm(Invoke(rewritten, EnumType, native, EnumArg(rewritten, a))));
        Assert.Equal(expected, Norm(Invoke(rewritten, EnumType, guarded, EnumArg(rewritten, a))));
    }

    // ── 8. lifted increment / compound (18 cells, old/new values) ──

    [Fact]
    public void Oracle_Increment_NullValueBoundary_PostfixOldNew_AllVariantsAgree()
    {
        var fileName = "NullableIncrement.cs";
        var result = RewriteFixture(fileName);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity is NumericBoundarySeverity.Error or NumericBoundarySeverity.Warning);
        var original = CompileAndLoad(File.ReadAllText(Path.Combine(FixtureDir, fileName)));
        var rewritten = CompileAndLoad(result.OutputCode);

        foreach (int? start in new int?[] { null, 41, int.MaxValue })
        {
            Agree1(original, rewritten, IncrementType, "NativePrefix", "GuardedPrefix", start);
            Agree1(original, rewritten, IncrementType, "NativePostfix", "GuardedPostfix", start);
            Agree1(original, rewritten, IncrementType,
                "NativePostfixDecrement", "GuardedPostfixDecrement", start);
        }

        foreach (int? x in new int?[] { null, 1, 40 })
        {
            foreach (int? y in new int?[] { null, 2 })
            {
                Agree2(original, rewritten, IncrementType,
                    "NativeCompound", "GuardedCompound", x, y);
            }

            Agree2(original, rewritten, IncrementType,
                "NativeCompoundOneSided", "GuardedCompoundOneSided", x, 2);
        }

        // Old value captured, new value wrapped at the boundary.
        var postfix = Invoke(rewritten, IncrementType, "NativePostfix", (int?)int.MaxValue);
        Assert.Equal($"{int.MaxValue}/{int.MinValue}", Norm(postfix));
        var nullPostfix = Invoke(rewritten, IncrementType, "NativePostfix", (object?)null);
        Assert.Equal("null/null", Norm(nullPostfix));

        Assert.Equal(1, result.Statistics.Nullable.LiftedIncrementPrefixSites);
        Assert.Equal(2, result.Statistics.Nullable.LiftedIncrementPostfixSites);
        Assert.Equal(2, result.Statistics.Nullable.LiftedIncrementCompoundSites);
        Assert.Equal(2, result.Statistics.Nullable.TempsOldRole);
        Assert.Equal(2, result.Statistics.Nullable.TempsLifted);
        var siteInfo = Assert.Single(result.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NullableLiftSiteInfo);
        Assert.Contains("5 inc", siteInfo.Message);
        Assert.Contains("2 o", Assert.Single(result.Diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.NullableLiftTempInfo).Message);
    }

    // ── 9. remaining §4.7 bucket tallies on the other fixture files ──

    [Fact]
    public void Oracle_AllFixtureFiles_StatisticsBucketsFilled_AndNoResiduals()
    {
        var expected = new (string File, Action<NullableLiftStatistics> AssertStats)[]
        {
            ("NullableArithmetic.cs", s =>
            {
                Assert.Equal(4, s.LiftedBinarySites);
                Assert.Equal(2, s.LiftedUnarySites);
                Assert.Equal(0, s.TempsLifted);
            }),
            ("NullableRelational.cs", s => Assert.Equal(6, s.LiftedRelationalSites)),
            ("NullableEquality.cs", s =>
            {
                Assert.Equal(2, s.LiftedEqualityBothNullableSites);
                Assert.Equal(3, s.LiftedEqualityOneSidedSites);
                Assert.Equal(2, s.LiftedEqualityNullLiteralSites);
            }),
            ("NullableBoolLogic.cs", s =>
            {
                Assert.Equal(1, s.LiftedBoolLogicAndSites);
                Assert.Equal(1, s.LiftedBoolLogicOrSites);
                Assert.Equal(1, s.LiftedBoolLogicXorSites);
                Assert.Equal(1, s.LiftedBoolLogicNotSites);
            }),
            ("NullableConversions.cs", s =>
            {
                Assert.Equal(1, s.UnwrapConversions);
                Assert.Equal(3, s.LiftedConversions);
            }),
            ("NullableEnumBackstop.cs", s =>
            {
                Assert.Equal(3, s.NullableEnumBackstopSites);
                Assert.Equal(1, s.LiftedConversions);
            }),
        };

        foreach (var (file, assertStats) in expected)
        {
            var result = RewriteFixture(file);
            Assert.True(result.Changed, $"{file} must be rewritten");
            Assert.DoesNotContain(result.Diagnostics,
                d => d.Code == PreprocessDiagnosticCodes.NullableLiftResidual);
            Assert.DoesNotContain(result.Diagnostics,
                d => d.Severity is NumericBoundarySeverity.Error
                    or NumericBoundarySeverity.Warning);
            Assert.Contains(result.Diagnostics,
                d => d.Code == PreprocessDiagnosticCodes.NullableLiftSiteInfo);
            assertStats(result.Statistics.Nullable);
        }
    }
}
