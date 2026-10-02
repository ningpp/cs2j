using System.Text.RegularExpressions;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Unit tests for C34 (decimal-ops normalization), co-located inside the C15
/// numeric-boundary pass and enabled by default
/// (<see cref="NumericBoundaryOptions.EnableDecimalOps"/> family kill-switch).
/// Covers DC-1~DC-13: arithmetic/comparison/unary/compound reduction, operand
/// promotion, single-evaluation hoisting, const downgrade, expression-tree/
/// dynamic/anchor errors, zero-harm surfaces, idempotency, the kill-switch
/// path and the compat Java surface.
/// </summary>
public class NumericBoundaryDecimalOpUnitTests
{
    private static NumericBoundaryOptions EnabledOptions(bool strict = true) =>
        new() { EnableDecimalOps = true, Strict = strict };

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

        var compilation = CSharpCompilation.Create("c34-decimal-ops-test",
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
        return new NumericBoundaryPreprocessor().Rewrite(src, model, options);
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

        var compilation = CSharpCompilation.Create("c34-output-compiles",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "C34 output must compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private const string UnsignedSnippet = """
        namespace Cs2jUnsigned
        {
            public struct UInt
            {
                private readonly int _bits;
                private UInt(int bits) { _bits = bits; }
                public static implicit operator UInt(int value) => new UInt(value);
                public static explicit operator int(UInt value) => value._bits;
                public static implicit operator decimal(UInt value) => 0m;
            }

            public struct ULong
            {
                private readonly long _bits;
                private ULong(long bits) { _bits = bits; }
                public static implicit operator ULong(long value) => new ULong(value);
                public static explicit operator long(ULong value) => value._bits;
                public static implicit operator decimal(ULong value) => 0m;
            }

            public static class UnsignedMath
            {
                public static decimal ToDecimalUInt32(int value) => (decimal)(value & 0xFFFFFFFFL);
                public static decimal ToDecimalUInt64(long value) => decimal.Parse(((ulong)value).ToString());
            }
        }

        namespace dotnet.System
        {
            // Real E09 support-struct namespace (src/Cs2jUnsigned/UInt.cs). The
            // production structs expose explicit operator int/long for the raw
            // bits; the implicit decimal conversion below is the minimum extra
            // surface that lets a built-in decimal operator bind to a wrapped
            // operand in a valid tree (today the path is defensive — E09 keeps
            // primitives — but C34 must route the fingerprint correctly).
            public struct UInt
            {
                private readonly int _bits;
                private UInt(int bits) { _bits = bits; }
                public static explicit operator int(UInt value) => value._bits;
                public static implicit operator decimal(UInt value) => 0m;
            }

            public struct ULong
            {
                private readonly long _bits;
                private ULong(long bits) { _bits = bits; }
                public static explicit operator long(ULong value) => value._bits;
                public static implicit operator decimal(ULong value) => 0m;
            }
        }
        """;

    // ── DC-1~DC-5: binary arithmetic ──

    [Fact]
    public void BinaryArithmetic_AllFiveOps_ReducedToStaticCalls()
    {
        var src = """
            public class C
            {
                public decimal M(decimal a, decimal b, decimal c, decimal d, decimal e)
                {
                    var v = a + b;
                    v = a - b;
                    v = c * d;
                    v = c / d;
                    return e % a;
                }
            }
            """;

        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.True(result.Changed);
        Assert.Contains("(decimal.Add(a,b))", compact);
        Assert.Contains("(decimal.Subtract(a,b))", compact);
        Assert.Contains("(decimal.Multiply(c,d))", compact);
        Assert.Contains("(decimal.Divide(c,d))", compact);
        Assert.Contains("(decimal.Remainder(e,a))", compact);

        var d2 = result.Statistics.DecimalOps;
        Assert.Equal(1, d2.BinarySitesByOp["add"]);
        Assert.Equal(1, d2.BinarySitesByOp["sub"]);
        Assert.Equal(1, d2.BinarySitesByOp["mul"]);
        Assert.Equal(1, d2.BinarySitesByOp["div"]);
        Assert.Equal(1, d2.BinarySitesByOp["rem"]);
        Assert.Equal(5, d2.TotalBinarySites);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── DC-6/DC-7: comparisons ──

    [Fact]
    public void Comparisons_AllSixDirections_UseDecimalCompareNeverEquals()
    {
        var src = """
            public class C
            {
                public bool M(decimal a, decimal b)
                {
                    return (a == b) || (a != b) || (a < b) || (a <= b) || (a > b) || (a >= b);
                }
            }
            """;

        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.True(result.Changed);
        Assert.Contains("(decimal.Compare(a,b)==0)", compact);
        Assert.Contains("(decimal.Compare(a,b)!=0)", compact);
        Assert.Contains("(decimal.Compare(a,b)<0)", compact);
        Assert.Contains("(decimal.Compare(a,b)<=0)", compact);
        Assert.Contains("(decimal.Compare(a,b)>0)", compact);
        Assert.Contains("(decimal.Compare(a,b)>=0)", compact);
        Assert.DoesNotContain(".Equals", result.OutputCode);
        Assert.DoesNotContain("CompareTo", result.OutputCode);
        Assert.Equal(2, result.Statistics.DecimalOps.EqualityComparisonSites);
        Assert.Equal(4, result.Statistics.DecimalOps.RelationalComparisonSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Equality_DifferentScales_NormalizedThroughCompare()
    {
        var src = "public class C { public bool M() => 1.0m == 1.00m; }";
        var result = Run(src, EnabledOptions());

        var compact = Compact(result.OutputCode);
        Assert.Contains("decimal.Compare(1.0m,1.00m)==0", compact);
        Assert.DoesNotContain(".Equals", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── DC-8: unary ──

    [Fact]
    public void UnaryMinus_BecomesNegate_UnaryPlusStripped()
    {
        var src = "public class C { public decimal M(decimal a) { return -a + +a; } }";
        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.Contains("(decimal.Negate(a))", compact);
        Assert.DoesNotContain("+a", compact.Replace("++", ""));
        Assert.Equal(1, result.Statistics.DecimalOps.UnaryNegateSites);
        Assert.Equal(1, result.Statistics.DecimalOps.UnaryPlusStrippedSites);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── DC-10: simple compound assignments ──

    [Fact]
    public void SimpleCompounds_AllFiveOps_RewrittenInPlace()
    {
        var src = """
            public class C
            {
                public void M(decimal x, decimal y)
                {
                    x += y;
                    x -= y;
                    x *= y;
                    x /= y;
                    x %= y;
                }
            }
            """;

        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.True(result.Changed);
        Assert.Contains("x=(decimal.Add(x,y))", compact);
        Assert.Contains("x=(decimal.Subtract(x,y))", compact);
        Assert.Contains("x=(decimal.Multiply(x,y))", compact);
        Assert.Contains("x=(decimal.Divide(x,y))", compact);
        Assert.Contains("x=(decimal.Remainder(x,y))", compact);
        Assert.Equal(5, result.Statistics.DecimalOps.SimpleCompoundSites);
        Assert.Equal(0, result.Statistics.DecimalOps.ComplexCompoundSites);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── DC-11: complex-target compounds (receiver/index/getter/setter once) ──

    [Fact]
    public void ComplexCompounds_PropertyFieldAndIndexer_HoistReceiverAndArgumentsOnce()
    {
        var src = """
            public class Probe
            {
                public decimal P { get; set; }
                public decimal F;
                public decimal[] Arr = new decimal[4];
                public decimal this[int i]
                {
                    get => Arr[i];
                    set => Arr[i] = value;
                }
            }

            public class C
            {
                public void M(Probe r, decimal x, int i)
                {
                    r.P += x;
                    r.F -= x;
                    r[i] *= x;
                }
            }
            """;

        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.True(result.Changed);
        Assert.Contains("var__cs2j_dec_r=r;", compact);
        Assert.Contains("__cs2j_dec_r.P=(decimal.Add(__cs2j_dec_r.P,x))", compact);
        Assert.Contains("__cs2j_dec_r.F=(decimal.Subtract(__cs2j_dec_r.F,x))", compact);
        Assert.Contains("var__cs2j_dec_a0=i;", compact);
        Assert.Contains("__cs2j_dec_r[__cs2j_dec_a0]=(decimal.Multiply(__cs2j_dec_r[__cs2j_dec_a0],x))", compact);

        var d2 = result.Statistics.DecimalOps;
        Assert.Equal(3, d2.ComplexCompoundSites);
        Assert.Equal(0, d2.SimpleCompoundSites);
        Assert.Equal(3, d2.TempsByRole["receiver"]);
        Assert.Equal(1, d2.TempsByRole["indexArg"]);
        Assert.False(d2.TempsByRole.ContainsKey("value"));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void ComplexCompound_ValueConsumed_HoistsAndExposesValueTemp()
    {
        var src = """
            public class Probe { public decimal P { get; set; } }
            public class C
            {
                public decimal M(Probe r, decimal x) => r.P += x;
            }
            """;

        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.Contains("var__cs2j_dec_r=r;", compact);
        Assert.Contains("__cs2j_dec_r.P=(decimal.Add(__cs2j_dec_r.P,x))", compact);
        Assert.Contains("var__cs2j_dec_t=__cs2j_dec_r.P;", compact);
        Assert.Contains("return__cs2j_dec_t;", compact);
        Assert.Equal(1, result.Statistics.DecimalOps.TempsByRole["value"]);
        AssertOutputCompiles(result.OutputCode);
    }

    [Theory]
    [InlineData("if ((r.P += x) == 0m) { }")]
    [InlineData("System.Func<decimal> f = () => r.P += x;")]
    [InlineData("var o = new Probe { P = r.P += x };")]
    public void ComplexCompounds_WithoutSafeAnchor_Report5365AndRollback(string statement)
    {
        var src = """
            public class Probe { public decimal P { get; set; } }
            public class C
            {
                public void M(Probe r, decimal x)
                {
                    __STMT__
                }
            }
            """.Replace("__STMT__", statement);

        var result = Run(src, EnabledOptions());

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == PreprocessDiagnosticCodes.DecimalOpsNoAnchor);
        Assert.Equal(1, result.Statistics.DecimalOps.DeferredByReason["no-anchor"]);
    }

    // ── DC-9: operand promotion ──

    [Fact]
    public void IntegerOperands_PromotedWithExplicitCast_LiteralsSymmetric()
    {
        var src = """
            public class C
            {
                public decimal M(decimal d, int i, short s, byte b, char c, long l, sbyte sb, ushort us)
                {
                    var a = d + i;
                    a = i + d;
                    a = d + 2;
                    a = 2 * d;
                    a = d + 2u;
                    a = d + s;
                    a = d + b;
                    a = d + c;
                    a = d + l;
                    a = d + sb;
                    return a + us;
                }
            }
            """;

        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.Contains("decimal.Add(d,(decimal)i)", compact);
        Assert.Contains("decimal.Add((decimal)i,d)", compact);
        Assert.Contains("decimal.Add(d,(decimal)2)", compact);
        Assert.Contains("decimal.Multiply((decimal)2,d)", compact);
        Assert.Contains("decimal.Add(d,(decimal)2u)", compact);
        Assert.Contains("decimal.Add(d,(decimal)s)", compact);
        Assert.Contains("decimal.Add(d,(decimal)b)", compact);
        Assert.Contains("decimal.Add(d,(decimal)c)", compact);
        Assert.Contains("decimal.Add(d,(decimal)l)", compact);
        Assert.Equal(11, result.Statistics.DecimalOps.CastPromotionSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void DecimalLiteralsAndOperands_StayUnchanged()
    {
        var src = "public class C { public decimal M(decimal d) => d + 1.5m; }";
        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.Contains("decimal.Add(d,1.5m)", compact);
        Assert.DoesNotContain("(decimal)1.5m", compact);
        Assert.Equal(0, result.Statistics.DecimalOps.CastPromotionSites);
    }

    [Fact]
    public void RealUnsignedOperands_GoThroughToDecimalHelpers()
    {
        // No using directive: UnsignedSnippet is prepended and a compilation-unit
        // using placed after the namespace would be CS1529; reference the
        // wrapper types by their fully qualified names instead.
        var src = """
            public class C
            {
                public decimal M(decimal d, uint u32, ulong u64,
                    Cs2jUnsigned.UInt w32, Cs2jUnsigned.ULong w64,
                    dotnet.System.UInt e32, dotnet.System.ULong e64)
                {
                    var a = d + u32;
                    a = d + u64;
                    a = d + w32;
                    a = d + w64;
                    a = d + e32;
                    a = d + e64;
                    checked
                    {
                        a = d * u32;
                    }
                    return a;
                }
            }
            """;

        var result = Run(UnsignedSnippet + src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.True(result.Changed);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt32((int)u32)", compact);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt64((long)u64)", compact);
        // Both wrapper fingerprints reach the helpers through the explicit
        // int/long bit-extraction operators.
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt32((int)w32)", compact);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt64((long)w64)", compact);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt32((int)e32)", compact);
        Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt64((long)e64)", compact);
        Assert.DoesNotContain("unchecked", result.OutputCode);
        // d+u32 and checked d*u32 are real uint sites; w32/e32 are the two
        // wrapper namespaces (Cs2jUnsigned defensive + real dotnet.System).
        Assert.Equal(4, result.Statistics.DecimalOps.ToDecimalUInt32PromotionSites);
        Assert.Equal(3, result.Statistics.DecimalOps.ToDecimalUInt64PromotionSites);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnsupportedOperand_FloatDouble_Reports5361AndRollsBack()
    {
        // The source itself must compile: a type with a user-defined implicit
        // conversion to decimal binds the built-in decimal operator, but C34
        // cannot normalize the operand domain safely, so it reports CS2J5361.
        var src = """
            public struct Money
            {
                public static implicit operator decimal(Money value) => 0m;
            }
            public class C
            {
                public decimal M(decimal d, Money m) => d + m;
            }
            """;

        var result = Run(src, EnabledOptions());

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == PreprocessDiagnosticCodes.DecimalOpsUnsupportedOperand);
    }

    // ── DC-12: const downgrade ──

    [Fact]
    public void ConstDecimal_Downgraded_WithCascade_PureLiteralsKept()
    {
        var src = """
            public class Other { public const decimal K = 3m * 3m; }

            public class Consts
            {
                public const decimal Pure = 1.5m;
                public const decimal A = 1m;
                public const decimal B = A * 2m;
                public const decimal C = B;
                public const decimal D = A * 2;
                public const decimal E = Other.K;

                public decimal M()
                {
                    const decimal localPure = 3.25m;
                    const decimal localDown = A + 1m;
                    const decimal mix1 = B * 2m, mix2 = 9m;
                    return localPure;
                }
            }
            """;

        var result = Run(src, EnabledOptions());
        var output = result.OutputCode;
        var compact = Compact(output);

        Assert.True(result.Changed);
        Assert.Contains("publicconstdecimalPure=1.5m;", compact);
        Assert.Contains("publicstaticreadonlydecimalA=1m;", compact);
        Assert.Contains("publicstaticreadonlydecimalB=(decimal.Multiply(A,2m));", compact);
        Assert.Contains("publicstaticreadonlydecimalC=B;", compact);
        Assert.Contains("publicstaticreadonlydecimalD=(decimal.Multiply(A,(decimal)2));", compact);
        Assert.Contains("publicstaticreadonlydecimalE=Other.K;", compact);
        // Other.K was public const and must stay accessible across classes.
        Assert.Contains("publicstaticreadonlydecimalK=(decimal.Multiply(3m,3m));", compact);
        Assert.Contains("constdecimallocalPure=3.25m;", compact);
        Assert.DoesNotContain("constdecimallocalDown", compact);
        // Mixed declarator list is split: mix1 downgraded, mix2 keeps const.
        Assert.Contains("decimalmix1=(decimal.Multiply(B,2m));", compact);
        Assert.Contains("constdecimalmix2=9m;", compact);
        Assert.Equal(8, result.Statistics.DecimalOps.ConstantsDowngraded);
        AssertOutputCompiles(output);
    }

    // ── hard invariants: zero-harm surfaces ──

    [Fact]
    public void UserDefinedOperator_IsDeferred_Untouched()
    {
        var src = """
            public readonly struct MyDecimal
            {
                private readonly int _v;
                public MyDecimal(int v) { _v = v; }
                public static MyDecimal operator +(MyDecimal a, MyDecimal b) => new MyDecimal(0);
                public static bool operator ==(MyDecimal a, MyDecimal b) => true;
                public static bool operator !=(MyDecimal a, MyDecimal b) => false;
            }
            public class C { public MyDecimal M(MyDecimal a, MyDecimal b) => a + b; }
            """;

        var result = Run(src, EnabledOptions());

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.DecimalOps.DeferredByReason["user-operator"]);
        Assert.DoesNotContain("decimal.Add", result.OutputCode);
    }

    [Fact]
    public void NullableDecimal_LiftedSites_GuardedByC22_InnerSweptByC34_CompoundStillDeferred()
    {
        // C22 M1 (Order=15, decision 19) explicitizes lifted decimal? sites
        // first: the HasValue guard wraps a BARE inner decimal op which C34
        // (Order=30) then sweeps on the re-bound tree. The lifted compound
        // a += 1m stays M2-owned and is the only site still deferred here.
        var src = """
            public class C
            {
                public decimal? M(decimal? a, decimal? b)
                {
                    var c = a + b;
                    var eq = a == b;
                    a += 1m;
                    return c;
                }
            }
            """;

        var result = Run(src, EnabledOptions());

        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        var compact = Compact(result.OutputCode);
        // Guarded arithmetic with the inner bare op swept to decimal.Add.
        Assert.Contains("a.HasValue&&b.HasValue?(decimal?)((decimal.Add(a.Value,b.Value))):(decimal?)null", compact);
        // Guarded both-nullable equality with the inner compare swept.
        Assert.Contains("a.HasValue?(b.HasValue&&(decimal.Compare(a.Value,b.Value)==0)):!b.HasValue", compact);
        // Lifted compound is NL-11 (M2): passes through and stays deferred.
        Assert.Contains("a+=1m", compact);
        Assert.Equal(1, result.Statistics.DecimalOps.DeferredByReason["lifted"]);
        Assert.Equal(1, result.Statistics.Nullable.LiftedBinarySites);
        Assert.Equal(1, result.Statistics.Nullable.LiftedEqualityBothNullableSites);
        Assert.Equal(0, result.Statistics.Nullable.LiftedIncrementCompoundSites);
    }

    [Fact]
    public void StringConcatenation_IsNotDecimalSite()
    {
        var src = "public class C { public string M(decimal d) => \"v=\" + d; }";
        var result = Run(src, EnabledOptions());

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void DynamicDecimalSite_Reports5362AndRollsBack()
    {
        var src = """
            public class C { public decimal M(dynamic x) => x + 2m; }
            """;

        var result = Run(src, EnabledOptions(), fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == PreprocessDiagnosticCodes.DecimalOpsDynamic);
    }

    [Fact]
    public void ExpressionTreeSite_Reports5363AndRollsBack()
    {
        var src = """
            using System;
            using System.Linq.Expressions;
            public class C
            {
                public Expression<Func<decimal, decimal, decimal>> F() => (a, b) => a + b;
            }
            """;

        var result = Run(src, EnabledOptions(), fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Code == PreprocessDiagnosticCodes.DecimalOpsExpressionTree);
    }

    [Fact]
    public void AliasedDecimal_NameResolvedSemantically()
    {
        var src = """
            using D = System.Decimal;
            public class C { public D M(D a, D b) => a + b; }
            """;

        var result = Run(src, EnabledOptions());
        Assert.True(result.Changed);
        Assert.Contains("decimal.Add(a,b)", Compact(result.OutputCode));
    }

    // ── kill-switch / idempotency / collisions ──

    [Fact]
    public void Disabled_LeavesZeroTrace_ByteIdentical()
    {
        var src = """
            public class C
            {
                public decimal M(decimal a, decimal b)
                {
                    a += b;
                    return -a + (a == b ? 1m : 2m);
                }
            }
            """;

        var result = Run(src, new NumericBoundaryOptions { EnableDecimalOps = false });

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(src, result.OutputCode);
        var d2 = result.Statistics.DecimalOps;
        Assert.Equal(0, d2.TotalBinarySites);
        Assert.Equal(0, d2.TotalCompoundSites);
        Assert.Equal(0, d2.TotalUnarySites);
    }

    [Fact]
    public void CleanFile_WhenEnabled_SkippedCleanUnchanged()
    {
        var src = "public class C { public decimal M(decimal a) => a; }";
        var result = Run(src, EnabledOptions());

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void SecondPass_IsIdempotent()
    {
        var src = """
            public class Probe { public decimal P { get; set; } }
            public class C
            {
                public decimal M(Probe r, decimal a, decimal b, int i)
                {
                    r.P += a + b + i;
                    return a * b;
                }
            }
            """;

        var first = Run(src, EnabledOptions());
        Assert.True(first.Changed);

        var second = Run(first.OutputCode, EnabledOptions());
        Assert.False(second.Changed);
        Assert.Equal(Compact(first.OutputCode), Compact(second.OutputCode));
    }

    [Fact]
    public void TempNames_AvoidUserIdentifierCollisions()
    {
        var src = """
            public class Probe { public decimal P { get; set; } }
            public class C
            {
                public void M(Probe r, decimal x)
                {
                    var __cs2j_dec_r = r;
                    __cs2j_dec_r.P += x;
                }
            }
            """;

        var result = Run(src, EnabledOptions());
        var compact = Compact(result.OutputCode);

        Assert.Contains("var__cs2j_dec_r_1=__cs2j_dec_r;", compact);
        Assert.Contains("__cs2j_dec_r_1.P=(decimal.Add(__cs2j_dec_r_1.P,x))", compact);
        AssertOutputCompiles(result.OutputCode);
    }

    // ── DC-13: residual gate ──

    [Fact]
    public void ResidualGate_BuiltInDecimalOperatorSurviving_Reports5367()
    {
        var src = "public class C { public decimal M(decimal a, decimal b) => a + b; }";
        var (tree, model) = ParseWithSemantics(src);
        var root = tree.GetRoot();
        var stats = new DecimalOpStatistics();
        var diagnostics = new List<NumericBoundaryDiagnostic>();

        DecimalOpRewriter.ApplyResidualGate(model, root, EnabledOptions(), stats, diagnostics);

        Assert.Equal(1, stats.ResidualDecimalOperators);
        Assert.Contains(diagnostics, d =>
            d.Code == PreprocessDiagnosticCodes.DecimalOpsResidualOperator
            && d.Severity == NumericBoundarySeverity.Error);
    }

    // ── compat Java source surface ──

    [Fact]
    public void CompatDecimal_ExposesStaticCompareAndHalfEvenFallback()
    {
        var repoRoot = FindRepoRoot();
        var decimalPath = Path.Combine(
            repoRoot,
            "java", "csharptojava-compat", "src", "main", "java",
            "io", "github", "ningpp", "compat", "Decimal.java");

        Assert.True(File.Exists(decimalPath), $"compat Decimal.java not found from {repoRoot}");
        var source = File.ReadAllText(decimalPath);

        Assert.Contains("public static int compare(Decimal left, Decimal right)", source);
        Assert.Contains("value.divide(divisor.value, MAX_SCALE, RoundingMode.HALF_EVEN)", source);
        Assert.DoesNotContain("RoundingMode.HALF_UP", source);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CSharpToJavaConverter.slnx"))
                && Directory.Exists(Path.Combine(
                    directory.FullName, "java", "csharptojava-compat")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("could not locate repository root (slnx + java compat)");
    }
}
