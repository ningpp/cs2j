using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SameTypeErasureConflictTests
{
    [Fact]
    public void NonGenericOverloads_SameErasedParams_RenamesSecond()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void MkEdgeStmt(IList<string> src, IList<string> dst)
    {
    }

    void MkEdgeStmt(IList<string> src, IList<IList<string>> edges)
    {
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // Both methods must appear
        Assert.Contains("mkEdgeStmt(CSharpGenericIList<String> src, CSharpGenericIList<String> dst)", code);
        Assert.Contains("mkEdgeStmt_erasure_2(CSharpGenericIList<String> src, CSharpGenericIList<CSharpGenericIList<String>> edges)", code);

        // Nested generic correctly mapped
        Assert.Contains("CSharpGenericIList<CSharpGenericIList<String>>", code);
    }

    [Fact]
    public void NonGenericOverloads_DifferentErasedParameterTypes_NoConflict()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void MkEdgeStmt(IList<string> src, IList<string> dst)
    {
    }

    void MkEdgeStmt(IList<string> src, string name)
    {
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);

        var code = result.GeneratedCode;
        // Both should have the same name (no conflict — different erased types)
        Assert.Contains("void mkEdgeStmt(CSharpGenericIList<String> src, CSharpGenericIList<String> dst)", code);
        Assert.Contains("void mkEdgeStmt(CSharpGenericIList<String> src, String name)", code);
        Assert.DoesNotContain("_erasure_", code);
    }

    [Fact]
    public void ThreeWayConflict_SameTypeParamCount_RenamesSequentially()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void Process(IList<string> a) { }
    void Process(IList<int> a) { }
    void Process(IList<IList<string>> a) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // First keeps original name, second gets _erasure_2, third gets _erasure_3
        Assert.Contains("void process(CSharpGenericIList<String> a)", code);
        Assert.Contains("void process_erasure_2(CSharpGenericIList<Integer> a)", code);
        Assert.Contains("void process_erasure_3(CSharpGenericIList<CSharpGenericIList<String>> a)", code);
    }

    [Fact]
    public void DifferentTypeParamCount_UsesExistingNtpSuffix()
    {
        // Existing behavior must stay unchanged
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    void M(object x) { }
    void M<T>(T x) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;
        // Method with 0 type params (fewer) gets _0tp
        Assert.Contains("void m_0tp", code);
        // Method with 1 type param (more) keeps original name without suffix.
        // Java syntax puts type parameters before the return type: <T> void m(T x)
        Assert.Contains("<T> void m", code);
    }

    [Fact]
    public async Task SameSyntaxParams_DifferentGenericArity_AllOverloadsSurvive()
    {
        // JsonSerializer has three Deserialize(string, ...) overloads where the
        // public non-generic (string, Type, options) and the private generic
        // Deserialize<TValue>(string, Type, options) share identical parameter
        // SYNTAX types. The partial-type member dedup key must include the
        // type-parameter count, otherwise the later overload is dropped while
        // call sites still reference it (prepending the Class token).
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-arity-dedup-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Sample.cs"), @"
class Sample
{
    public static TValue Deserialize<TValue>(string json, object options = null)
    {
        return default(TValue);
    }

    public static object Deserialize(string json, object returnType, object options = null)
    {
        return Deserialize<object>(json, returnType, options);
    }

    private static TValue Deserialize<TValue>(string json, object returnType, object options)
    {
        return Deserialize<TValue>(json, returnType, options);
    }
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);
            var sample = Assert.Single(results);
            Assert.True(sample.Success, string.Join("\n", sample.Diagnostics));

            var code = sample.GeneratedCode;

            // All three overloads must survive.
            Assert.Contains("public static <TValue> TValue deserialize(Class<TValue> _cs2j_TValue, String json, Object options)", code);
            Assert.Contains("public static Object deserialize_0tp(String json, Object returnType, Object options)", code);
            Assert.Contains("private static <TValue> TValue deserialize(Class<TValue> _cs2j_TValue, String json, Object returnType, Object options)", code);

            // Call sites must match the surviving private definition (Class token first).
            Assert.Contains("return deserialize(Object.class, json, returnType, options);", code);
            Assert.Contains("return deserialize(_cs2j_TValue, json, returnType, options);", code);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SameNameNestedDelegates_DifferentArity_BothSurviveWithRename()
    {
        // JsonClassInfo declares two delegates named ParameterizedConstructorDelegate
        // with different type-parameter counts. Java forbids two nested types with
        // the same simple name, so the later arity must be renamed (Name_arity) at
        // both the declaration and all reference sites.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-delegate-arity-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Sample.cs"), @"
class Sample
{
    public delegate T Del<T>(object[] args);
    public delegate T Del<T, TArg0, TArg1>(TArg0 arg0, TArg1 arg1);

    public Del<T> Create<T>() => null;
    public Del<T, TArg0, TArg1> Create2<T, TArg0, TArg1>() => null;
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);
            var sample = Assert.Single(results);
            Assert.True(sample.Success, string.Join("\n", sample.Diagnostics));

            var code = sample.GeneratedCode;

            // Both delegate interfaces must be emitted with distinct names.
            Assert.Contains("interface Del<T>", code);
            Assert.Contains("interface Del_3<T, TArg0, TArg1>", code);

            // The renamed delegate must be referenced under its Java name.
            Assert.Contains("Del_3<T, TArg0, TArg1> create2", code);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConstructorErasureFactory_PreservesOriginalConstructorBody()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Rectangle { }
class Point { }

class Box
{
    public int Count;

    public Box(IEnumerable<Point> points)
    {
        Count = 100;
        foreach (var p in points)
            Count++;
    }

    public Box(IEnumerable<Rectangle> rectangles)
    {
        Count = 200;
        foreach (var r in rectangles)
            Count++;
    }

    public static Box FromRectangles(IEnumerable<Rectangle> rectangles)
    {
        return new Box(rectangles);
    }
}",
            FileName = "Box.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        Assert.Contains("public static Box createFrom_CSharpGenericIterable_Rectangle(CSharpGenericIterable<Rectangle> rectangles)", code);
        Assert.Contains("Box __inst = new Box();", code);
        Assert.Contains("__inst.Count = 200;", code);
        Assert.Contains("for (Rectangle r : rectangles)", code);
        Assert.Contains("__inst.Count++;", code);
        Assert.Contains("return __inst;", code);
        Assert.Contains("return Box.createFrom_CSharpGenericIterable_Rectangle(rectangles);", code);
    }

    [Fact]
    public void ConstructorErasureFactory_UsesSimpleTypeSuffixForNamespacedArgument()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

namespace Geometry
{
    public class Rectangle { }
    public class Point { }
}

namespace Layout
{
    using Geometry;

    class Box
    {
        public Box(IEnumerable<Point> points) { }
        public Box(IEnumerable<Rectangle> rectangles) { }

        public static Box FromRectangles(IEnumerable<Rectangle> rectangles)
        {
            return new Box(rectangles);
        }
    }
}",
            FileName = "Box.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        Assert.Contains("public static Box createFrom_CSharpGenericIterable_Rectangle(CSharpGenericIterable<Rectangle> rectangles)", code);
        Assert.Contains("return Box.createFrom_CSharpGenericIterable_Rectangle(rectangles);", code);
        Assert.DoesNotContain("createFrom_CSharpGenericIterable_Geometry", code);
    }
}
