using CSharpToJava.Core;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Round 20：构造器工厂（Create$N）调用点残余缺口（TS2673 369，MSAGL 全库）。
/// Round 18 覆盖了结构化表达式/对象初始化器路径；本轮两个形态仍保留 `new` 形态：
/// ① 结构体/用户定义 struct 字段无初始化器时的 Java 默认零值填充
///    （FieldTransformer 直产 `new X()` 原始文本，从未经过工厂改写）；
/// ② 复杂 LINQ 链（lambda 内含对象创建 → 非"无副作用"）经 TsLinqRewriter
///    改写为 Enumerable.select(xs, x => new X(...)) 后的 lambda 体内调用点。
/// </summary>
public class TsCtorFactoryRound20Tests : TsConversionTestBase
{
    [Fact]
    public void Struct_Field_Default_Zero_Initializer_Becomes_Factory()
    {
        // 镜像 MSAGL ConvexHull.pivot（`Point pivot;` 无初始化器，struct 字段
        // Java 默认零值填充为 new Point()，但 Point 已被工厂化只剩私有无参
        // 构造器 → TS2673。正确形态：Point.Create$1()（零参工厂恒为 Create$1）。
        var result = Convert("""
namespace App
{
    public struct Point
    {
        public double X;
        public double Y;

        public Point(double x, double y) { X = x; Y = y; }
    }

    public class Use
    {
        private Point pivot;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private pivot: Point = Point.Create$1();", result.GeneratedCode);
        Assert.DoesNotContain("pivot: Point = new Point();", result.GeneratedCode);
    }

    [Fact]
    public void Auto_Property_Backing_Field_Default_Zero_Becomes_Factory()
    {
        // 镜像 MSAGL Arrowhead.TipPosition（`public Point TipPosition { get; set; }`
        // 无初始化器自动属性）：PropertyTransformer 为结构体自动属性后备字段填充
        // `new Point()`，同样需改写为 Point.Create$1()。
        var result = Convert("""
namespace App
{
    public struct Point
    {
        public double X;
        public double Y;

        public Point(double x, double y) { X = x; Y = y; }
    }

    public class Use
    {
        public Point TipPosition { get; set; }
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public TipPosition: Point = Point.Create$1();", result.GeneratedCode);
        Assert.DoesNotContain("TipPosition: Point = new Point();", result.GeneratedCode);
    }

    [Fact]
    public void Struct_Field_Default_Zero_Initializer_With_Only_Implicit_Ctor_Stays_New()
    {
        // 守卫：仅隐式构造器（无重载组）的 struct 不会被工厂化，`new X()` 保持合法。
        var result = Convert("""
namespace App
{
    public struct Plain
    {
        public double X;
    }

    public class Use
    {
        private Plain p;
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("p: Plain = new Plain();", result.GeneratedCode);
    }

    [Fact]
    public void Raw_Linq_Lambda_Object_Creation_Becomes_Factory()
    {
        // 镜像 MSAGL SplineRouter `obstacleShapes.Select(s => new DebugCurve(1, s.BoundaryCurve))`：
        // lambda 内含对象创建 → 非"无副作用"→ 复杂链 Enumerable.select(xs, x => new X(...))。
        // 该 lambda 体内调用点必须改写为 DebugCurve.Create$N(...)。
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;
namespace App
{
    public interface ICurve { }

    public class DebugCurve
    {
        public DebugCurve(ICurve curve) { }
        public DebugCurve(double pen, ICurve curve) { }
        public DebugCurve(double width, string color, ICurve curve) { }
    }

    public class Use
    {
        public IEnumerable<DebugCurve> Make(IEnumerable<ICurve> xs)
        {
            return xs.Select(x => new DebugCurve(1, "red", x));
        }
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("DebugCurve.Create$3(1, \"red\", x)", result.GeneratedCode);
        Assert.DoesNotContain("new DebugCurve(1, \"red\", x)", result.GeneratedCode);
    }

    [Fact]
    public void Struct_Ctor_In_Raw_Linq_Lambda_Becomes_Factory()
    {
        // 镜像 MSAGL `new LineSegment(...)`（struct 1 显式构造器 + 隐式无参）出现在
        // 复杂 LINQ 链 lambda 体内（Round 18 只覆盖了结构化路径，raw lambda 内
        // 保持 new 形态 → TS2673）。
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;
namespace App
{
    public struct LineSegment
    {
        public double A;
        public double B;

        public LineSegment(double a, double b) { A = a; B = b; }
    }

    public interface ICurve { }

    public class DebugCurve
    {
        public DebugCurve(double pen, ICurve curve) { }
        public DebugCurve(double width, string color, ICurve curve) { }
    }

    public class Use
    {
        public IEnumerable<DebugCurve> Make(IEnumerable<ICurve> xs)
        {
            return xs.Select(x => new DebugCurve(1, "red", new LineSegment(1.0, 2.0)));
        }
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("DebugCurve.Create$2(1, \"red\", LineSegment.Create$2(1.0, 2.0))", result.GeneratedCode);
        Assert.DoesNotContain("new LineSegment(1.0, 2.0)", result.GeneratedCode);
    }

    [Fact]
    public void Same_Arity_Overloads_Disambiguated_By_Literal_Kinds()
    {
        // 镜像 MSAGL DebugCurve 同参数量重载组（4 参两组：
        // (byte,double,string,ICurve) vs (double,string,ICurve,object)）：
        // LINQ 重写产物链内语义模型不可解析 → 回退实参数量 + 字面量种类匹配
        // （数值字面量只匹配数值形参、字符串字面量只匹配字符串形参）。
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;
namespace App
{
    public interface ICurve { }

    public class DebugCurve
    {
        public DebugCurve(byte transparency, double width, string color, ICurve curve) { }
        public DebugCurve(double width, string color, ICurve curve, object id) { }
    }

    public class Use
    {
        public IEnumerable<DebugCurve> Make(IEnumerable<ICurve> xs)
        {
            return xs.Select(x => new DebugCurve(100, 1, "brown", x));
        }
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // 4 参两组：实参 (number, number, string, ?) → 第二组 (double,string,ICurve,object)
        // 第二位 number vs string 失配 → 唯一零失配者为第一组 → Create$1
        Assert.Contains("DebugCurve.Create$1(100, 1, \"brown\", x)", result.GeneratedCode);
        Assert.DoesNotContain("new DebugCurve(100, 1, \"brown\", x)", result.GeneratedCode);
    }

    [Fact]
    public void Same_Arity_Ambiguous_Stays_New_With_No_Literal_Evidence()
    {
        // 守卫：同参数量候选均无字面量失配（实参全为标识符）→ 歧义 → 不改写
        // （保持 new 形态，不做语义猜测；未工厂化场景 new 合法）。
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;
namespace App
{
    public interface ICurve { }

    public class DebugCurve
    {
        public DebugCurve(byte transparency, double width, string color, ICurve curve) { }
        public DebugCurve(double width, string color, ICurve curve, object id) { }
    }

    public class Use
    {
        public IEnumerable<DebugCurve> Make(IEnumerable<ICurve> xs, string c, object o)
        {
            return xs.Select(x => new DebugCurve(1.0f, c, x, o));
        }
    }
}
""");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("new DebugCurve(1.0, c, x, o)", result.GeneratedCode);
        Assert.DoesNotContain("DebugCurve.Create$", result.GeneratedCode);
    }
}
