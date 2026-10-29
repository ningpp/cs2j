using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C14 复合赋值站点回归：`x += y` 绑定用户定义 operator+（Point 结构体）时，
/// 使用点重写器必须改写为运算符方法调用（`x = Point.Add(x, y)`；与用户方法
/// 同名时消歧为 `AddOperator`）；此前一个属性目标的
/// 复合站点会让整个运算符 plan 原子跳过（CS2J5179），连带把同类型的全部二元
/// 使用点留在 C# 层，生成 Java 残留 `Point += Point`
/// （javac「二元运算符 '+' 的操作数类型错误」）。
/// </summary>
public class MemberDesugarOperatorCompoundAssignmentTests
{
    private const string Source = """
using System;

namespace Geometry
{
    public struct Point
    {
        public double X;
        public double Y;

        public Point(double x, double y) { X = x; Y = y; }

        public static Point operator +(Point a, Point b) { return new Point(a.X + b.X, a.Y + b.Y); }
        public static Point operator -(Point a, Point b) { return new Point(a.X - b.X, a.Y - b.Y); }
        public static Point operator *(Point a, double c) { return new Point(a.X * c, a.Y * c); }
        public static Point operator /(Point a, double c) { return new Point(a.X / c, a.Y / c); }
        public static Point operator %(Point a, Point b) { return new Point(a.X % b.X, a.Y % b.Y); }
        public static Point operator &(Point a, Point b) { return new Point(a.X + b.X, a.Y + b.Y); }
        public static Point operator |(Point a, Point b) { return new Point(a.X + b.X, a.Y + b.Y); }
        public static Point operator ^(Point a, Point b) { return new Point(a.X + b.X, a.Y + b.Y); }
    }

    public class Holder
    {
        public Point Prop { get; set; }
    }

    public class Segment
    {
        private Point a;
        private Holder holder;

        public Point Prop { get; set; }

        public void Shift(Point delta, Point mask, double factor)
        {
            a += delta;
            a -= delta;
            a *= factor;
            a /= factor;
            a %= mask;
            a &= mask;
            a |= mask;
            a ^= mask;

            holder.Prop += delta;
            Prop -= mask;

            Point[] pts = new Point[2];
            pts[0] += delta;
            pts[1] -= mask;
        }
    }
}
""";

    [Fact]
    public void CompoundAssignment_UserDefinedOperators_RewrittenToMethodCalls()
    {
        var output = Compact(Rewrite(Source));

        // 字段目标的复合赋值：内联为 x = T.Op(x, v)（左值单次求值安全）。
        Assert.Contains("a=Point.Add(a,delta)", output, StringComparison.Ordinal);
        Assert.Contains("a=Point.Subtract(a,delta)", output, StringComparison.Ordinal);
        Assert.Contains("a=Point.Multiply(a,factor)", output, StringComparison.Ordinal);
        Assert.Contains("a=Point.Divide(a,factor)", output, StringComparison.Ordinal);
        Assert.Contains("a=Point.Mod(a,mask)", output, StringComparison.Ordinal);
        Assert.Contains("a=Point.And(a,mask)", output, StringComparison.Ordinal);
        Assert.Contains("a=Point.Or(a,mask)", output, StringComparison.Ordinal);
        Assert.Contains("a=Point.Xor(a,mask)", output, StringComparison.Ordinal);

        // 属性目标：先由运算符 pass 内联，属性 pass 再展开 get_/set_。
        Assert.Contains(
            "holder.set_Prop(Point.Add(holder.get_Prop(),delta))", output, StringComparison.Ordinal);
        Assert.Contains("set_Prop(Point.Subtract(get_Prop(),mask))", output, StringComparison.Ordinal);

        // 数组元素目标：语句位内联（写入沿用数组索引语义）。
        Assert.Contains("pts[0]=Point.Add(pts[0],delta)", output, StringComparison.Ordinal);
        Assert.Contains("pts[1]=Point.Subtract(pts[1],mask)", output, StringComparison.Ordinal);

        // 不得残留任何用户运算符的复合赋值语法。
        Assert.DoesNotContain("a+=delta", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a-=delta", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a*=factor", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a/=factor", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a%=mask", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a&=mask", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a|=mask", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a^=mask", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Prop+=delta", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Prop-=mask", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 单值消费位（实参）的复合站点：属性目标的接收者带副作用时不能内联两次，
    /// 分析期必须整 plan 跳过而不是产生双求值改写（保持安全闸门）。
    /// </summary>
    [Fact]
    public void CompoundAssignment_NonStatementComplexReceiver_PlanSkipped()
    {
        const string source = """
namespace Geometry
{
    public struct Point
    {
        public double X;
        public static Point operator +(Point a, Point b) { return new Point { X = a.X + b.X }; }
    }

    public class Holder
    {
        public Point Prop { get; set; }
    }

    public class Segment
    {
        private Holder holder = new Holder();

        private static Holder Get() { return new Holder(); }

        public void Consume(Point delta)
        {
            System.Console.WriteLine(Get().Prop += delta);
            System.Console.WriteLine(holder.Prop += delta);
        }
    }
}
""";
        var output = Compact(Rewrite(source));

        // 不可提升的站点：整个 op_Addition plan 原子跳过（声明与站点都不动）。
        Assert.Contains("operator+(Pointa,Pointb)", output, StringComparison.Ordinal);
        Assert.Contains("Get().Prop+=delta", output, StringComparison.Ordinal);
    }

    /// <summary>去掉全部空白：重写器产物在最终 Formatter 前 token 间距不稳定。</summary>
    private static string Compact(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", "");

    private static string Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview), "Compound.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-compound",
            new[] { tree },
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diagnostics = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(tree), stats, diagnostics);
        return result.OutputCode;
    }
}
