using CSharpToJava.Core;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Round 18：跨文件构造器重载工厂（Create$N）调用点改写复现。
/// 镜像 MSAGL Polyline（4 个公共构造器）在 ConvexHull/CurveFactory 等
/// 其他文件中的 `new Polyline(...)` 调用点：语法层 rewriter 只改写了
/// 声明文件内部调用点，跨文件调用点保留 `new` 形态，而声明端已只剩
/// 私有无参构造器 → tsc TS2673（Constructor of class 'X' is private）。
/// </summary>
public class TsCtorFactoryCrossFileTests
{
    private static ConversionOptions CreateOptions(TargetLanguage target = TargetLanguage.TypeScript)
    {
        var basePath = AppContext.BaseDirectory;
        return new ConversionOptions
        {
            TargetLanguage = target,
            TypeMappingConfigPath = Path.Combine(basePath, "config", "TypeMappings.json"),
            TypeScriptMappingConfigPath = Path.Combine(basePath, "config", "typescript", "TypeMappings.json"),
            TypeScriptMemberMappingPath = Path.Combine(basePath, "config", "typescript", "MemberMappings.json"),
        };
    }

    private static async Task<List<ConversionResult>> ConvertProjectAsync(params (string FilePath, string Content)[] files)
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        return await pipeline.ConvertProjectAsync(files
            .Select(file => new SourceFile { FilePath = file.FilePath, Content = file.Content }));
    }

    [Fact]
    public async Task Ctor_Factory_Call_Sites_Are_Rewritten_Across_Files()
    {
        var results = await ConvertProjectAsync(
            ("Polyline.cs", """
using System.Collections.Generic;
namespace App
{
    public class Polyline
    {
        public Polyline() { }
        public Polyline(IEnumerable<Point> points) { }
    }

    public class Point
    {
        public Point(double x, double y) { }
    }
}
"""),
            ("Use.cs", """
using System.Collections.Generic;
namespace App
{
    public class Use
    {
        public Polyline Make(IEnumerable<Point> points)
        {
            return new Polyline(points);
        }

        public Polyline Empty()
        {
            return new Polyline();
        }
    }
}
"""));

        var useResult = Assert.Single(results, result => result.FileName == "Use.ts");
        Assert.True(useResult.Success, string.Join("; ", useResult.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return Polyline.Create$2(points);", useResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Polyline.Create$1();", useResult.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Polyline(", useResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Struct_Implicit_Ctor_Call_Sites_Become_Factories()
    {
        // 镜像 MSAGL Point（struct，仅 1 个显式构造器 + 隐式无参）：
        // 语法层只见到 1 个显式构造器 → 不参与语法重载组；IR 层 TsCtorFactoryRewriter
        // 按含隐式构造器的集合工厂化 → 声明端只剩私有无参构造器。调用点（含同文件）
        // 必须改写为 Create$N，否则 TS2673（Constructor of class 'Point' is private）。
        var results = await ConvertProjectAsync(
            ("Point.cs", """
namespace App
{
    public struct Point
    {
        public double X;
        public double Y;

        public Point(double x, double y) { X = x; Y = y; }

        public static Point Zero() { return new Point(); }
        public static Point Make(double x, double y) { return new Point(x, y); }
    }
}
"""),
            ("Use.cs", """
namespace App
{
    public class Use
    {
        public Point A() { return new Point(); }
        public Point B() { return new Point(1.0, 2.0); }
    }
}
"""));

        var pointResult = Assert.Single(results, result => result.FileName == "Point.ts");
        Assert.True(pointResult.Success, string.Join("; ", pointResult.Diagnostics.Select(d => d.Message)));
        // 同文件调用点同样改写
        Assert.Contains("return Point.Create$1();", pointResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Point.Create$2(x, y);", pointResult.GeneratedCode, StringComparison.Ordinal);

        var useResult = Assert.Single(results, result => result.FileName == "Use.ts");
        Assert.True(useResult.Success, string.Join("; ", useResult.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return Point.Create$1();", useResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Point.Create$2(1.0, 2.0);", useResult.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Point(", useResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Object_Initializer_Creations_Become_Factories()
    {
        // 镜像 MSAGL ConvexHull.CreateConvexHullAsClosedPolyline：
        // return new Polyline(points) { Closed = true }; —— 语法层因 initializer
        // 跳过改写；IR 层必须产出 var _obj2 = Polyline.Create$2(points); _obj2.Closed = true;
        var results = await ConvertProjectAsync(
            ("Polyline.cs", """
using System.Collections.Generic;
namespace App
{
    public class Polyline
    {
        public bool Closed;
        public Polyline() { }
        public Polyline(IEnumerable<Point> points) { }
    }

    public class Point
    {
        public Point(double x, double y) { }
    }
}
"""),
            ("Maker.cs", """
using System.Collections.Generic;
namespace App
{
    public class Maker
    {
        public Polyline Make(IEnumerable<Point> points)
        {
            return new Polyline(points) { Closed = true };
        }

        public Polyline Empty()
        {
            return new Polyline() { Closed = false };
        }
    }
}
"""));

        var makerResult = Assert.Single(results, result => result.FileName == "Maker.ts");
        Assert.True(makerResult.Success, string.Join("; ", makerResult.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Polyline.Create$2(points)", makerResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Polyline.Create$1()", makerResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".Closed = true;", makerResult.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Polyline(", makerResult.GeneratedCode, StringComparison.Ordinal);
    }
}
