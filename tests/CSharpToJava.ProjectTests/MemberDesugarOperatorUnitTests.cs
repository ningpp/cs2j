using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Operators;
using CSharpToJava.Core.Transformers.Member;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>C14 运算符去糖 Core 单测矩阵（设计 §7.1）：内联源码 → RewriteFile →
/// 忽略空白断言 + AssertOutputCompiles；harness 复用 C11 属性单测（internal 同程序集）。</summary>
public class MemberDesugarOperatorUnitTests
{
    internal static (string OutputCode, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diagnostics)
        Rewrite(string src, MemberDesugarOptions? options = null, params string[] extra)
        => MemberDesugarPropertyUnitTests.Rewrite(src, options, extra);

    internal static void AssertOutputCompiles(string output, params string[] extra)
        => MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, extra);

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    internal static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    internal static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    // ---- 决策 1/1b：ConvertOperatorName 与 OpSymbolToJavaName 的 PascalCase 逐名对齐 ----

    [Theory]
    [InlineData("op_Addition", "Add")]
    [InlineData("op_Subtraction", "Subtract")]
    [InlineData("op_Multiply", "Multiply")]
    [InlineData("op_Division", "Divide")]
    [InlineData("op_Modulus", "Mod")]
    [InlineData("op_Equality", "ValueEquals")]
    [InlineData("op_Inequality", "NotEquals")]
    [InlineData("op_GreaterThan", "GreaterThan")]
    [InlineData("op_LessThan", "LessThan")]
    [InlineData("op_GreaterThanOrEqual", "GreaterThanOrEqual")]
    [InlineData("op_LessThanOrEqual", "LessThanOrEqual")]
    [InlineData("op_BitwiseAnd", "And")]
    [InlineData("op_BitwiseOr", "Or")]
    [InlineData("op_ExclusiveOr", "Xor")]
    [InlineData("op_LogicalNot", "Not")]
    [InlineData("op_OnesComplement", "OnesComplement")]
    [InlineData("op_LeftShift", "LeftShift")]
    [InlineData("op_RightShift", "RightShift")]
    [InlineData("op_UnsignedRightShift", "UnsignedRightShift")]
    [InlineData("op_UnaryNegation", "Negate")]
    [InlineData("op_UnaryPlus", "Plus")]
    [InlineData("op_CheckedAddition", "AddChecked")]
    [InlineData("op_CheckedSubtraction", "SubtractChecked")]
    [InlineData("op_CheckedMultiply", "MultiplyChecked")]
    public void OperatorNames_PascalCaseMirrorsConverterJavaNames(string metadataName, string expected)
    {
        Assert.Equal(expected, OperatorNames.ConvertOperatorName(metadataName));
        // 零漂移闭环：C# 名经 MethodTransformer.JavaName（首字母小写）== 转换器今发 Java 名
        var isChecked = metadataName.StartsWith("op_Checked", StringComparison.Ordinal);
        var baseMetadata = isChecked
            ? "op_" + metadataName["op_Checked".Length..]
            : metadataName;
        var javaToday = OperatorTransformer.OpSymbolToJavaName[baseMetadata]
                        + (isChecked ? "Checked" : ""); // OperatorTransformer.cs:96-8 后缀
        Assert.Equal(javaToday, char.ToLowerInvariant(expected[0]) + expected[1..]);
    }

    // ---- 决策 3：转换命名镜像表（算法复刻 BuildConversionMethodSuffix）----

    [Theory]
    [InlineData("double", "Double")]
    [InlineData("int", "Int")]
    [InlineData("long", "Long")]
    [InlineData("float", "Float")]
    [InlineData("boolean", "Boolean")]
    [InlineData("String", "String")]
    [InlineData("BigDecimal", "Decimal")]
    [InlineData("byte", "Byte")]
    [InlineData("byte[]", "ByteArray")]
    [InlineData("int[]", "IntArray")]
    [InlineData("double[]", "DoubleArray")]
    [InlineData("List", "")] // 表外 → 空（失败）
    public void OperatorNames_ConversionMirrorSuffix(string javaType, string expectedSuffix)
    {
        var ok = OperatorNames.TryGetConversionBaseSuffix(javaType, out var suffix);
        if (expectedSuffix.Length == 0)
            Assert.False(ok);
        else
        {
            Assert.True(ok);
            Assert.Equal(expectedSuffix, suffix);
        }
    }

    // ---- 决策 8：确定性碰撞升级 ----

    [Fact]
    public void OperatorNames_CollisionUpgradesDeterministically()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "Add" };
        Assert.Equal("AddOperator", OperatorNames.Disambiguate("Add", taken));
        taken.Add("AddOperator");
        Assert.Equal("AddOperator2", OperatorNames.Disambiguate("Add", taken));
        taken.Add("AddOperator2");
        Assert.Equal("AddOperator3", OperatorNames.Disambiguate("Add", taken));
        Assert.Equal("Add", OperatorNames.Disambiguate("Add", new HashSet<string>()));
        var taken2 = new HashSet<string>(StringComparer.Ordinal) { "ToString" };
        Assert.Equal("ToStringOperator", OperatorNames.Disambiguate("ToString", taken2));
    }

    // ---- 规则接线（§4.2）：程序集扫描、Order=40、Kind=Operator ----

    [Fact]
    public void RuleDiscovery_ContainsOperatorRule_Ordered()
    {
        var rules = MemberDesugarRuleDiscovery.Discover();
        var opRule = Assert.Single(rules, r => r.Kind == MemberDesugarKind.Operator);
        Assert.Equal(40, opRule.Order);
        Assert.IsType<OperatorRule>(opRule);
        Assert.True(rules.ToList().IndexOf(rules.First(r => r.Kind == MemberDesugarKind.Property))
                    < rules.ToList().IndexOf(opRule));
    }

    [Fact]
    public void Plan_BuiltForArithmeticDeclaration_AndDesugared()
    {
        var (output, stats, _) = Rewrite("""
            public struct Point
            {
                public int X, Y;
                public Point(int x, int y) { X = x; Y = y; }
                public static Point operator +(Point a, Point b)
                    => new Point(a.X + b.X, a.Y + b.Y);
            }
            """);
        Assert.Equal(1, stats.OperatorsPlanned);
        Assert.Contains("public static Point Add(Point a, Point b)", output);
        Assert.DoesNotContain("operator", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void IncrementDecrementDeclarations_DeferredNotPlanned()
    {
        var (output, stats, _) = Rewrite("""
            public class Counter
            {
                public int V;
                public static Counter operator ++(Counter c) { c.V++; return c; }
                public static Counter operator --(Counter c) { c.V--; return c; }
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Equal(2, stats.IncrementDecrementDeferred);
        Assert.Contains("operator ++", output);
        Assert.Contains("operator --", output);
    }

    [Fact]
    public void GateType_BitwiseAndOr_RetainedForJk()
    {
        var (output, stats, _) = Rewrite("""
            public struct SqlBool
            {
                public static SqlBool operator &(SqlBool a, SqlBool b) => a;
                public static SqlBool operator |(SqlBool a, SqlBool b) => a;
                public static bool operator true(SqlBool x) => true;
                public static bool operator false(SqlBool x) => false;
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Equal(2, stats.GateOperatorsRetained);
        Assert.Contains("operator &", output);
        Assert.Contains("operator |", output);
        Assert.Contains("operator true", output);
    }

    // ---- OD-1/OD-2 声明归约矩阵 ----

    [Fact]
    public void Declaration_FullOperatorMatrix_Desugared()
    {
        var (output, stats, _) = Rewrite("""
            public struct Swiss
            {
                public int V;
                public Swiss(int v) { V = v; }
                public static Swiss operator +(Swiss a, Swiss b) => new Swiss(a.V + b.V);
                public static Swiss operator -(Swiss a, Swiss b) => new Swiss(a.V - b.V);
                public static Swiss operator *(Swiss a, Swiss b) => new Swiss(a.V * b.V);
                public static Swiss operator /(Swiss a, Swiss b) => new Swiss(a.V / b.V);
                public static Swiss operator %(Swiss a, Swiss b) => new Swiss(a.V % b.V);
                public static bool operator ==(Swiss a, Swiss b) => a.V == b.V;
                public static bool operator !=(Swiss a, Swiss b) => a.V != b.V;
                public static bool operator >(Swiss a, Swiss b) => a.V > b.V;
                public static bool operator <(Swiss a, Swiss b) => a.V < b.V;
                public static bool operator >=(Swiss a, Swiss b) => a.V >= b.V;
                public static bool operator <=(Swiss a, Swiss b) => a.V <= b.V;
                public static Swiss operator &(Swiss a, Swiss b) => new Swiss(a.V & b.V);
                public static Swiss operator |(Swiss a, Swiss b) => new Swiss(a.V | b.V);
                public static Swiss operator ^(Swiss a, Swiss b) => new Swiss(a.V ^ b.V);
                public static Swiss operator ~(Swiss a) => new Swiss(~a.V);
                public static Swiss operator <<(Swiss a, int n) => new Swiss(a.V << n);
                public static Swiss operator >>(Swiss a, int n) => new Swiss(a.V >> n);
                public static Swiss operator >>>(Swiss a, int n) => new Swiss(a.V >>> n);
                public static Swiss operator -(Swiss a) => new Swiss(-a.V);
                public static Swiss operator +(Swiss a) => a;
                public static bool operator !(Swiss a) => a.V == 0;
                public override bool Equals(object? o) => o is Swiss s && s.V == V;
                public override int GetHashCode() => V;
            }
            """);
        Assert.Equal(21, stats.OperatorsPlanned);
        foreach (var name in new[] { "Add", "Subtract", "Multiply", "Divide", "Mod",
                     "And", "Or", "Xor", "OnesComplement",
                     "LeftShift", "RightShift", "UnsignedRightShift", "Negate", "Plus" })
            Assert.Contains($"static Swiss {name}(", output);
        foreach (var name in new[] { "ValueEquals", "NotEquals", "GreaterThan", "LessThan",
                     "GreaterThanOrEqual", "LessThanOrEqual", "Not" })
            Assert.Contains($"static bool {name}(", output);
        Assert.DoesNotContain("operator ", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_Conversions_ImplicitAndExplicit()
    {
        var (output, stats, _) = Rewrite("""
            public struct Temperature
            {
                public double C;
                public Temperature(double c) { C = c; }
                public static implicit operator double(Temperature t) => t.C;
                public static explicit operator byte(Temperature t) => (byte)t.C;
                public static implicit operator int(Temperature t) => (int)t.C;
            }
            """);
        Assert.Equal(3, stats.OperatorsPlanned);
        Assert.Contains("public static double ToDouble(Temperature t)", output);
        Assert.Contains("public static byte ToByteExplicit(Temperature t)", output);
        Assert.Contains("public static int ToInt(Temperature t)", output);
        Assert.DoesNotContain("operator", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_ConversionMirrorMiss_ReturnTypeOutsideTable_Skipped5177()
    {
        var (output, stats, diags) = Rewrite("""
            public struct Km
            {
                public double V;
                public static explicit operator Km(double v) => new Km { V = v };
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Contains("explicit operator Km", output);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.ConversionMirrorMiss);
    }

    [Fact]
    public void Declaration_CheckedPair_BothEmitted_CheckedKeywordStripped()
    {
        var (output, stats, _) = Rewrite("""
            public struct Wide
            {
                public long Lo;
                public Wide(long lo) { Lo = lo; }
                public static Wide operator +(Wide a, Wide b) => new Wide(a.Lo + b.Lo);
                public static Wide operator checked +(Wide a, Wide b) => throw new System.OverflowException();
            }
            """);
        Assert.Equal(2, stats.OperatorsPlanned);
        Assert.Contains("public static Wide Add(Wide a, Wide b)", output);
        Assert.Contains("public static Wide AddChecked(Wide a, Wide b)", output);
        Assert.DoesNotContain("checked", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_GenericOperator_BecomesPlainStaticMethod()
    {
        var (output, stats, _) = Rewrite("""
            public struct Vec<T> where T : struct
            {
                public T X, Y;
                public Vec(T x, T y) { X = x; Y = y; }
                public static Vec<T> operator +(Vec<T> a, Vec<T> b) => new Vec<T>(a.X, b.Y);
            }
            """);
        Assert.Equal(1, stats.OperatorsPlanned);
        Assert.Contains("public static Vec<T> Add(Vec<T> a, Vec<T> b)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_CollisionWithUserMethod_UserMemberUntouched()
    {
        var (output, stats, diags) = Rewrite("""
            public class Money
            {
                public decimal Amount;
                public Money(decimal a) { Amount = a; }
                public static Money operator +(Money a, Money b) => new Money(a.Amount + b.Amount);
                public static Money Add(Money a, Money b) => a;
            }
            """);
        Assert.Equal(1, stats.OperatorsPlanned);
        Assert.Contains("public static Money AddOperator(Money a, Money b)", output);
        Assert.Contains("public static Money Add(Money a, Money b) => a;", output);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.OperatorNameDisambiguated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_ConversionToString_CollidesWithObjectMethod_Upgrades()
    {
        var (output, _, _) = Rewrite("""
            public class NameBox
            {
                public string V = "";
                public static implicit operator string(NameBox n) => n.V;
            }
            """);
        Assert.Contains("public static string ToStringOperator(NameBox n)", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_AttributesAndXmlDoc_Preserved()
    {
        var (output, _, _) = Rewrite("""
            using System.Diagnostics;
            public struct Tagged
            {
                public int V;
                public Tagged(int v) { V = v; }
                /// <summary>plus</summary>
                [DebuggerStepThrough]
                public static Tagged operator +(Tagged a, Tagged b) => new Tagged(a.V + b.V);
            }
            """);
        Assert.Contains("/// <summary>plus</summary>", output);
        Assert.Contains("[DebuggerStepThrough]", output);
        Assert.Contains("public static Tagged Add(", output);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void Declaration_ExpressionBodyAndBlockBody_BothSupported()
    {
        var (output, _, _) = Rewrite("""
            public class E
            {
                public int V;
                public static E operator +(E a, E b) => new E { V = a.V + b.V };
                public static E operator -(E a, E b) { return new E { V = a.V - b.V }; }
            }
            """);
        Assert.Contains("public static E Add(E a, E b) =>", output);
        Assert.Contains("public static E Subtract(E a, E b)", output);
        AssertOutputCompiles(output);
    }

    // ---- OC-1/OC-2 二元/一元站点（单文件形态：harness 只改写首文件）----

    [Fact]
    public void BinarySites_Arithmetic_RewrittenToStaticCalls()
    {
        var (output, stats, _) = Rewrite("""
            public struct Point
            {
                public int X, Y;
                public Point(int x, int y) { X = x; Y = y; }
                public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
                public static Point operator *(Point a, int k) => new Point(a.X * k, a.Y * k);
                public static Point operator *(int k, Point a) => new Point(a.X * k, a.Y * k);
                public static double operator *(Point a, Point b) => a.X * b.X + a.Y * b.Y;
            }
            public static class Use
            {
                public static Point Sum(Point p, Point q) => p + q;
                public static Point Scale(Point p) => p * 3;
                public static Point FlipScale(Point p) => 3 * p;
                public static double Dot(Point p, Point q) => p * q;
            }
            """);
        Assert.True(stats.OperatorBinarySitesRewritten >= 4);
        AssertContains(output, "Sum(Point p, Point q) => Point.Add(p, q)");
        AssertContains(output, "Scale(Point p) => Point.Multiply(p, 3)");
        AssertContains(output, "FlipScale(Point p) => Point.Multiply(3, p)");
        AssertContains(output, "Dot(Point p, Point q) => Point.Multiply(p, q)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySites_ComparisonAndBitwise_Rewritten()
    {
        var (output, stats, _) = Rewrite("""
            public struct Cmp
            {
                public int V;
                public Cmp(int v) { V = v; }
                public static bool operator ==(Cmp a, Cmp b) => a.V == b.V;
                public static bool operator !=(Cmp a, Cmp b) => a.V != b.V;
                public static bool operator <(Cmp a, Cmp b) => a.V < b.V;
                public static Cmp operator &(Cmp a, Cmp b) => new Cmp(a.V & b.V);
                public override bool Equals(object? o) => o is Cmp c && c.V == V;
                public override int GetHashCode() => V;
            }
            public static class Use
            {
                public static bool Eq(Cmp a, Cmp b) => a == b;
                public static bool Ne(Cmp a, Cmp b) => a != b;
                public static bool Lt(Cmp a, Cmp b) => a < b;
                public static Cmp Both(Cmp a, Cmp b) => a & b;
            }
            """);
        Assert.True(stats.OperatorBinarySitesRewritten >= 4);
        AssertContains(output, "Eq(Cmp a, Cmp b) => Cmp.ValueEquals(a, b)");
        AssertContains(output, "Ne(Cmp a, Cmp b) => Cmp.NotEquals(a, b)");
        AssertContains(output, "Lt(Cmp a, Cmp b) => Cmp.LessThan(a, b)");
        AssertContains(output, "Both(Cmp a, Cmp b) => Cmp.And(a, b)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void UnarySites_RewrittenWithSymbolDisambiguation()
    {
        var (output, stats, _) = Rewrite("""
            public struct Sign
            {
                public int V;
                public Sign(int v) { V = v; }
                public static Sign operator -(Sign a) => new Sign(-a.V);
                public static Sign operator +(Sign a) => a;
                public static bool operator !(Sign a) => a.V == 0;
                public static Sign operator ~(Sign a) => new Sign(~a.V);
                public static Sign operator -(Sign a, Sign b) => new Sign(a.V - b.V);
                public static Sign operator +(Sign a, Sign b) => new Sign(a.V + b.V);
            }
            public static class Use
            {
                public static Sign Neg(Sign s) => -s;
                public static Sign Pos(Sign s) => +s;
                public static bool Not(Sign s) => !s;
                public static Sign Inv(Sign s) => ~s;
                public static Sign Sub(Sign a, Sign b) => a - b;
                public static Sign Add(Sign a, Sign b) => a + b;
            }
            """);
        Assert.True(stats.OperatorUnarySitesRewritten >= 4);
        AssertContains(output, "Neg(Sign s) => Sign.Negate(s)");
        AssertContains(output, "Pos(Sign s) => Sign.Plus(s)");
        AssertContains(output, "Not(Sign s) => Sign.Not(s)");
        AssertContains(output, "Inv(Sign s) => Sign.OnesComplement(s)");
        AssertContains(output, "Sub(Sign a, Sign b) => Sign.Subtract(a, b)");
        AssertContains(output, "Add(Sign a, Sign b) => Sign.Add(a, b)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySite_StaticQualifier_MinimalDisplay()
    {
        var (output, _, _) = Rewrite("""
            namespace Geom
            {
                public struct Vec2
                {
                    public int X, Y;
                    public Vec2(int x, int y) { X = x; Y = y; }
                    public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
                }
                public static class Use
                {
                    public static Vec2 Add(Vec2 a, Vec2 b) => a + b;
                }
            }
            """);
        AssertContains(output, "Add(Vec2 a, Vec2 b) => Vec2.Add(a, b)");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySites_OperatorBodyPrimitiveOps_Untouched()
    {
        var (output, stats, _) = Rewrite("""
            public struct P
            {
                public int X, Y;
                public P(int x, int y) { X = x; Y = y; }
                public static P operator +(P a, P b) => new P(a.X + b.X, a.Y + b.Y);
                public static P operator *(P a, P b) => new P(a.X * b.X + 3, 0);
            }
            """);
        // 运算符体内 int 原语运算保持原样（对照 OperatorMethodNameCollisionTests 形态）
        Assert.Contains("a.X + b.X", output);
        Assert.Contains("a.X * b.X + 3", output);
        Assert.Equal(0, stats.OperatorBinarySitesRewritten);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BinarySites_EnumDecimalBuiltin_Untouched()
    {
        var (output, stats, _) = Rewrite("""
            public enum E { A, B }
            public static class Use
            {
                public static int M(decimal a, decimal b) => a + b > 0 ? 1 : 0;
                public static bool Cmp(E a, E b) => a == b;
                public static int Ints(int a, int b) => a + b;
            }
            """);
        Assert.Contains("a + b > 0", output);
        Assert.Contains("a == b", output);
        Assert.Contains("a + b", output);
        Assert.Equal(0, stats.OperatorBinarySitesRewritten);
    }

    [Fact]
    public void BinarySites_MetadataOnlyOperator_UntouchedAndCounted()
    {
        var (output, stats, _) = Rewrite("""
            using System;
            public static class Use
            {
                public static DateTime Next(DateTime d) => d + TimeSpan.FromDays(1);
            }
            """);
        Assert.Contains("d + TimeSpan.FromDays(1)", output);
        Assert.True(stats.MetadataOperatorSitesUntouched >= 1);
    }

}
