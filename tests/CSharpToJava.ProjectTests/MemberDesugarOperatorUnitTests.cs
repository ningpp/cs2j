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

    /// <summary>跨文件 harness（镜像 C11 UseSites_CrossFile）：整编译分析 → 逐文件 RewriteFile。</summary>
    internal static (string[] Outputs, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diags)
        RewriteAll(MemberDesugarOptions? options, params string[] files)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f, parseOptions)).ToArray();
        var refs = new List<MetadataReference>();
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
            foreach (var assembly in trusted.Split(Path.PathSeparator))
                if (File.Exists(assembly)) refs.Add(MetadataReference.CreateFromFile(assembly));
        var compilation = CSharpCompilation.Create("md-multifile", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var pp = new MemberDesugarPreprocessor(options ?? new MemberDesugarOptions());
        pp.BindCompilation(compilation);
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var outputs = new string[trees.Length];
        for (var i = 0; i < trees.Length; i++)
            outputs[i] = pp.RewriteFile(files[i], compilation.GetSemanticModel(trees[i]), stats, diags)
                .OutputCode;
        return (outputs, stats, diags);
    }

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

    // ---- OC-4/OC-5 转换站点（决策 4；多文件形态：站点与声明分居两文件）----

    [Fact]
    public void ConversionSites_ExplicitCast_RewrittenToMethodCall()
    {
        var (outputs, stats, _) = RewriteAll(null, """
            public struct Temperature
            {
                public double C;
                public Temperature(double c) { C = c; }
                public static implicit operator double(Temperature t) => t.C;
                public static explicit operator int(Temperature t) => (int)t.C;
            }
            """, """
            public static class Use
            {
                public static double Celsius(Temperature t) => t;   // 隐式站点
                public static int Rounded(Temperature t) => (int)t; // 显式 cast 站点
            }
            """);
        Assert.True(stats.OperatorConversionSitesRewritten >= 2);
        AssertContains(outputs[0], "public static double ToDouble(Temperature t)");
        AssertContains(outputs[0], "public static int ToIntExplicit(Temperature t)");
        AssertContains(outputs[1], "Celsius(Temperature t) => Temperature.ToDouble(t)");
        AssertContains(outputs[1], "Rounded(Temperature t) => Temperature.ToIntExplicit(t)");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void ConversionSites_ToNonTableType_MirrorMiss_DeclarationAndSitesUntouched()
    {
        // 决策 3 v1 边界：目标类型在命名镜像表外（此处为用户类型 Temperature）
        // → 整 plan Infeasible（CS2J5177），声明与全部站点原子不动。
        var (outputs, stats, diags) = RewriteAll(null, """
            public struct Temperature
            {
                public double C;
                public Temperature(double c) { C = c; }
                public static implicit operator double(Temperature t) => t.C;
                public static explicit operator Temperature(double c) => new Temperature(c);
            }
            """, """
            public static class Use
            {
                public static double Celsius(Temperature t) => t;
                public static Temperature FromCelsius(double c) => (Temperature)c;
            }
            """);
        // 同类型的 implicit→double plan 照常计划执行，仅「到用户类型」的显式方向整 plan 跳过
        Assert.Equal(1, stats.OperatorConversionSitesRewritten);
        AssertContains(outputs[0], "public static double ToDouble(Temperature t)");
        AssertContains(outputs[0], "explicit operator Temperature");
        AssertContains(outputs[1], "Celsius(Temperature t) => Temperature.ToDouble(t)");
        AssertContains(outputs[1], "(Temperature)c");
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.ConversionMirrorMiss);
    }

    [Fact]
    public void ConversionSites_ImplicitSiteCatalogue_AllRewritten()
    {
        var (outputs, stats, _) = RewriteAll(null, """
            using System.Collections.Generic;
            public struct Wrapper
            {
                public int V;
                public Wrapper(int v) { V = v; }
                public static implicit operator int(Wrapper w) => w.V;
                public static implicit operator long(Wrapper w) => w.V;
            }
            """, """
            using System.Collections.Generic;
            public static class Use
            {
                private static Wrapper _f = new Wrapper(7);
                public static int FieldTarget() { int x = _f; return x; }
                public static int ReturnSite() => _f;
                public static int ArgSite(Wrapper w) => Take(w);
                public static int Take(int i) => i;
                public static int BranchSite(bool c, Wrapper w) => c ? w : 5;
                public static long CoalesceSite(Wrapper? w) => w ?? new Wrapper(3);
                public static List<int> CollectionSite(Wrapper w) => new List<int> { w, 2 };
                public static System.Collections.Generic.IEnumerable<int> YieldSite(Wrapper w)
                {
                    yield return w;
                    yield break;
                }
            }
            """);
        Assert.True(stats.OperatorConversionSitesRewritten >= 7);
        AssertContains(outputs[1], "int x = Wrapper.ToInt(_f)");
        AssertContains(outputs[1], "ReturnSite() => Wrapper.ToInt(_f)");
        AssertContains(outputs[1], "Take(Wrapper.ToInt(w))");
        AssertContains(outputs[1], "c ? Wrapper.ToInt(w) : 5");
        AssertContains(outputs[1], "Wrapper.ToLong(w ?? new Wrapper(3))");
        AssertContains(outputs[1], "new List<int> { Wrapper.ToInt(w), 2 }");
        AssertContains(outputs[1], "yield return Wrapper.ToInt(w)");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void ConversionSites_ExplicitOperatorPlusStandardOuter_ResidualCastPreserved()
    {
        var (outputs, _, _) = RewriteAll(null, """
            public struct Src
            {
                public int V;
                public Src(int v) { V = v; }
                public static explicit operator int(Src s) => s.V;
                public static implicit operator long(Src s) => s.V;
            }
            """, """
            public static class Use
            {
                public static byte Narrow(Src s) => (byte)s;   // 用户显式→int，再显式标准 int→byte
                public static long Widen(Src s) => s;          // 用户隐式→long
                public static int Direct(Src s) => (int)s;     // 用户显式→int（无外层）
                public static object Box(Src s) { object o = s; return o; } // 装箱非用户转换：不动
            }
            """);
        AssertContains(outputs[1], "Narrow(Src s) => (byte)Src.ToIntExplicit(s)");
        AssertContains(outputs[1], "Widen(Src s) => Src.ToLong(s)");
        AssertContains(outputs[1], "Direct(Src s) => Src.ToIntExplicit(s)");
        AssertContains(outputs[1], "object o = s;");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void ConversionSites_BoolImplicit_InIfCondition_Rewritten()
    {
        var (outputs, _, _) = RewriteAll(null, """
            public struct Flag
            {
                public bool B;
                public Flag(bool b) { B = b; }
                public static implicit operator bool(Flag f) => f.B;
            }
            """, """
            public static class Use
            {
                public static int Check(Flag f)
                {
                    if (f) return 1;
                    return 0;
                }
            }
            """);
        AssertContains(outputs[1], "if (Flag.ToBoolean(f))");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void ConversionSites_ExplicitCastOfUserConversion_InExpression()
    {
        var (outputs, _, _) = RewriteAll(null, """
            public struct Money
            {
                public decimal Amount;
                public Money(decimal a) { Amount = a; }
                public static implicit operator decimal(Money m) => m.Amount;
            }
            """, """
            public static class Use
            {
                public static string Print(Money m) => ((decimal)m).ToString("F2");
            }
            """);
        AssertContains(outputs[1], "Money.ToDecimal(m)");
        AssertNotContains(outputs[1], "(decimal)m");
        AssertContains(outputs[1], ".ToString(\"F2\")");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    // ---- OC-3 复合赋值（决策 2b）----

    [Fact]
    public void CompoundAssignment_LocalAndFieldTargets_Inlined()
    {
        var (outputs, stats, _) = RewriteAll(null, """
            public struct Acc
            {
                public int V;
                public Acc(int v) { V = v; }
                public static Acc operator +(Acc a, Acc b) => new Acc(a.V + b.V);
                public static Acc operator *(Acc a, Acc b) => new Acc(a.V * b.V);
            }
            """, """
            public class Holder
            {
                public Acc Total;
            }
            public static class Use
            {
                public static Acc Bump(Acc x, Acc v, Holder h)
                {
                    x += v;
                    x *= v;
                    h.Total += v;
                    this_none();
                    return x;
                }
                private static void this_none() { }
            }
            """);
        Assert.True(stats.OperatorCompoundSitesRewritten >= 3);
        AssertContains(outputs[1], "x = Acc.Add(x, v)");
        AssertContains(outputs[1], "x = Acc.Multiply(x, v)");
        AssertContains(outputs[1], "h.Total = Acc.Add(h.Total, v)");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void CompoundAssignment_SideEffectReceiver_LiftedOnceToTemp()
    {
        var (outputs, stats, _) = RewriteAll(null, """
            public struct Acc
            {
                public int V;
                public Acc(int v) { V = v; }
                public static Acc operator +(Acc a, Acc b) => new Acc(a.V + b.V);
            }
            """, """
            public class Holder
            {
                public Acc Total;
                public static Holder Instance() => new Holder();
            }
            public static class Use
            {
                public static void Bump(Acc v)
                {
                    Holder.Instance().Total += v;
                }
            }
            """);
        Assert.True(stats.OperatorCompoundSitesRewritten >= 1);
        AssertContains(outputs[1], "var __cs2j_recv0 = Holder.Instance();");
        AssertContains(outputs[1], "__cs2j_recv0.Total = Acc.Add(__cs2j_recv0.Total, v);");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void CompoundAssignment_PropertyTarget_SkipsPlanAtomically()
    {
        var (outputs, stats, diags) = RewriteAll(null, """
            public struct Acc
            {
                public int V;
                public Acc(int v) { V = v; }
                public static Acc operator +(Acc a, Acc b) => new Acc(a.V + b.V);
            }
            """, """
            public class Box
            {
                public Acc P { get; set; }
            }
            public static class Use
            {
                public static void Bump(Box b, Acc v)
                {
                    b.P += v;
                }
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned); // plan Infeasible → 计数不涨
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.CompoundTargetUnsupported);
        AssertContains(outputs[0], "operator +(Acc a, Acc b)"); // 声明原子不动
        // C11 属性族照常消费该复合站点（合成 set_P(get_P() + v)，二元仍绑未动的运算符）
        AssertContains(outputs[1], "b.set_P(b.get_P() + v);");
        AssertNotContains(outputs[1], "Acc.Add(");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void CompoundAssignment_BackConversionUserDefined_ChainsExplicit()
    {
        // op+ 返回 Money（用户类型）；目标 decimal 由用户隐式转换 declared 在 Money 上提供
        var (outputs, stats, _) = RewriteAll(null, """
            public struct Money
            {
                public decimal V;
                public Money(decimal v) { V = v; }
                public static Money operator +(decimal a, Money b) => new Money(a + b.V);
                public static implicit operator decimal(Money m) => m.V;
            }
            """, """
            public static class Use
            {
                public static decimal Bump(decimal d, Money m)
                {
                    d += m;
                    return d;
                }
            }
            """);
        Assert.True(stats.OperatorCompoundSitesRewritten >= 1);
        AssertContains(outputs[1], "d = Money.ToDecimal(Money.Add(d, m));");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void CompoundAssignment_EventAdd_NotTouched()
    {
        var (output, stats, _) = Rewrite("""
            using System;
            public class Pub
            {
                public event EventHandler? E;
                public void On() => E?.Invoke(this, EventArgs.Empty);
            }
            public static class Use
            {
                public static void Wire(Pub p, EventHandler h) { p.E += h; }
            }
            """);
        Assert.Contains("p.E += h", output);
        Assert.Equal(0, stats.OperatorCompoundSitesRewritten);
    }

    // ---- OC-6 提升公式（决策 5）----

    [Fact]
    public void LiftedConversion_NullableToNullable_Expanded()
    {
        var (outputs, stats, _) = RewriteAll(null, """
            public struct Snow
            {
                public int V;
                public Snow(int v) { V = v; }
                public static implicit operator double(Snow s) => s.V;
            }
            """, """
            public static class Use
            {
                public static double? F(Snow? s) => s;
            }
            """);
        Assert.True(stats.OperatorLiftedSitesExpanded >= 1);
        AssertContains(outputs[1],
            "s.HasValue ? Snow.ToDouble(s.GetValueOrDefault()) : (double?)null");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void LiftedArithmetic_ExpandedWithHasValueGuard()
    {
        var (outputs, stats, _) = RewriteAll(null, """
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
            }
            """, """
            public static class Use
            {
                public static N? F(N? a, N? b) => a + b;
            }
            """);
        Assert.True(stats.OperatorLiftedSitesExpanded >= 1);
        AssertContains(outputs[1],
            "a.HasValue & b.HasValue ? N.Add(a.Value, b.Value) : (N?)null");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void LiftedRelational_ExpandedToBoolNullable()
    {
        var (outputs, _, _) = RewriteAll(null, """
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static bool operator >(N a, N b) => a.V > b.V;
                public static bool operator <(N a, N b) => a.V < b.V;
            }
            """, """
            public static class Use
            {
                public static bool? F(N? a, N? b) => a > b;
            }
            """);
        AssertContains(outputs[1],
            "a.HasValue & b.HasValue ? N.GreaterThan(a.Value, b.Value) : (bool?)null");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void LiftedEquality_ThreeValuedTable()
    {
        var (outputs, _, _) = RewriteAll(null, """
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static bool operator ==(N a, N b) => a.V == b.V;
                public static bool operator !=(N a, N b) => a.V != b.V;
                public override bool Equals(object? o) => o is N n && n.V == V;
                public override int GetHashCode() => V;
            }
            """, """
            public static class Use
            {
                public static bool? Eq(N? a, N? b) => a == b;
                public static bool? Ne(N? a, N? b) => a != b;
            }
            """);
        AssertContains(outputs[1],
            "!a.HasValue && !b.HasValue ? true : a.HasValue && b.HasValue ? N.ValueEquals(a.Value, b.Value) : false");
        AssertContains(outputs[1],
            "!a.HasValue && !b.HasValue ? false : a.HasValue && b.HasValue ? N.NotEquals(a.Value, b.Value) : true");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void LiftedUnary_Expanded()
    {
        var (outputs, _, _) = RewriteAll(null, """
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator -(N a) => new N(-a.V);
            }
            """, """
            public static class Use
            {
                public static N? F(N? a) => -a;
            }
            """);
        AssertContains(outputs[1], "a.HasValue ? N.Negate(a.Value) : (N?)null");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void LiftedCompound_WritesBackNullWhenLeftNull()
    {
        var (outputs, _, _) = RewriteAll(null, """
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
            }
            """, """
            public static class Use
            {
                public static void F(N? x, N v)
                {
                    x += v;
                }
            }
            """);
        AssertContains(outputs[1], "x = x.HasValue ? N.Add(x.Value, v) : (N?)null");
        AssertOutputCompiles(outputs[0], outputs[1]);
    }

    [Fact]
    public void LiftedSite_NonSimpleOperand_SkipsWith5176()
    {
        var (outputs, stats, diags) = RewriteAll(null, """
            public struct N
            {
                public int V;
                public N(int v) { V = v; }
                public static N operator +(N a, N b) => new N(a.V + b.V);
                public static N Make(N b) => b;
            }
            """, """
            public static class Use
            {
                public static N? F(N? a, N b) => a + N.Make(b);
            }
            """);
        Assert.Equal(0, stats.OperatorsPlanned);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.UnbuildableLift);
        AssertContains(outputs[0], "operator +(N a, N b)");
        AssertContains(outputs[1], "a + N.Make(b)");
    }

}
