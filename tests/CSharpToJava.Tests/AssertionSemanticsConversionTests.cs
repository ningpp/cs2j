using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the AssertionError / XmlException /
/// UnsupportedOperationException class:
/// byte scalar sign extension, C# struct value equality marker, MSTest
/// DeploymentItem / ClassInitialize / ExpectedException, erased IList member
/// conformance and conditional struct value-copy insertion.
/// </summary>
public class AssertionSemanticsConversionTests
{
    // 1. C# byte 是无符号标量，映射到 Java int；赋值点不能再套有符号 (byte)。
    //    之前 Color.A/R/G/B 的 128..255 变成 -128..-1，Color/DebugCurve/GraphToString
    //    多处断言与 "#ff0000" 输出错误。
    [Fact]
    public void ByteScalarAssignment_DoesNotEmitSignedByteCast()
    {
        var result = Convert("""
            class Sample
            {
                private byte a;
                private byte[] blob = new byte[] { 200 };

                public void Set(byte value)
                {
                    a = value;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        Assert.Contains("private int a;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(byte) (value)", code, StringComparison.Ordinal);
        Assert.Contains("a = value;", code, StringComparison.Ordinal);
        // byte[] stays Java byte[] and still needs the literal narrowing.
        Assert.Contains("(byte) (200)", code, StringComparison.Ordinal);
    }

    // 2. C# struct → Java class 必须带运行时值类型标记，compat Assert 才能对
    //    没有源码 Equals 的 struct 做逐字段比较（NUnit ValueType.Equals 语义）。
    [Fact]
    public void ConvertedStruct_IsMarkedAsCompatValueType()
    {
        var result = Convert("""
            public struct Box
            {
                public double Left;
                public double Right;
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        Assert.Contains("import io.github.ningpp.compat.CompatValueType;", code, StringComparison.Ordinal);
        Assert.Contains("@CompatValueType", code, StringComparison.Ordinal);
    }

    // 3. MSTest [DeploymentItem] 之前被丢弃，DgmlParserTests 的部署目录里没有
    //    Dgml/*.dgml，7 个用例全部 XmlException(FileNotFoundException)。
    [Fact]
    public void MstestDeploymentItem_EmitsRuntimeDeploymentAnnotationAndExtension()
    {
        var result = Convert("""
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            [TestClass]
            [DeploymentItem("Resources/Dgml", "Dgml")]
            public class DgmlTests
            {
                public TestContext TestContext { get; set; }

                [TestMethod]
                public void Parse() { }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        Assert.Contains(
            "@MSTestDeploymentItem(source = \"Resources/Dgml\", outputDirectory = \"Dgml\")",
            code, StringComparison.Ordinal);
        Assert.Contains("@ExtendWith(MSTestExtension.class)", code, StringComparison.Ordinal);
        // 注解本体必须同时出现在 import 中，否则 javac 报“找不到符号: 类 ExtendWith”。
        Assert.Contains("import org.junit.jupiter.api.extension.ExtendWith;", code, StringComparison.Ordinal);
        Assert.Contains(
            "import Microsoft.VisualStudio.TestTools.UnitTesting.MSTestDeploymentItem;",
            code, StringComparison.Ordinal);
    }

    // 4. [ClassInitialize] 未转换为 @BeforeAll，BugsRegressionTest 的静态参考数据
    //    从未加载（crossValidation_ReferenceOraclesAgree 断言 null）。
    [Fact]
    public void MstestClassInitialize_BecomesBeforeAllWithExtension()
    {
        var result = Convert("""
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            [TestClass]
            public class BugTests
            {
                private static object reference;

                [ClassInitialize]
                public static void Init(TestContext context)
                {
                    reference = new object();
                }

                [TestMethod]
                public void Works() { }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        Assert.Contains("import org.junit.jupiter.api.BeforeAll;", code, StringComparison.Ordinal);
        Assert.Contains("@BeforeAll", code, StringComparison.Ordinal);
        Assert.Contains("@ExtendWith(MSTestExtension.class)", code, StringComparison.Ordinal);
        Assert.Contains("import org.junit.jupiter.api.extension.ExtendWith;", code, StringComparison.Ordinal);
    }

    // 5. [ExpectedException(typeof(NotImplementedException))] 未转换，Ellipse.curvature
    //    抛出的 UnsupportedOperationException 直接让测试报错而不是通过。
    [Fact]
    public void MstestExpectedException_BecomesAssertThrows()
    {
        var result = Convert("""
            using System;
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            [TestClass]
            public class EllipseTests
            {
                [TestMethod]
                [ExpectedException(typeof(NotImplementedException))]
                public void Curvature()
                {
                    throw new NotImplementedException();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        Assert.Contains(
            "org.junit.jupiter.api.Assertions.assertThrows(UnsupportedOperationException.class, () -> {",
            code, StringComparison.Ordinal);
    }

    // 6. IList<T> 的强类型 set/CopyTo 与 compat 接口 Object 形参同擦除却不覆盖：
    //    经 CSharpIList 静态类型调用会落到接口 default（丢行为/不抛异常）。
    [Fact]
    public void IListTypedIndexerAndCopyTo_AreErasedToObjectParameter()
    {
        var result = Convert("""
            using System;
            using System.Collections;
            using System.Collections.Generic;

            class Node { }

            class NodeCollection : IList<Node>
            {
                private List<Node> nodes = new List<Node>();

                public Node this[int index]
                {
                    get { return nodes[index]; }
                    set { nodes[index] = value; }
                }

                public int Count => nodes.Count;
                public bool IsReadOnly => false;
                public void Add(Node item) { nodes.Add(item); }
                public void Clear() { nodes.Clear(); }
                public bool Contains(Node item) { return nodes.Contains(item); }
                public void CopyTo(Node[] array, int arrayIndex) { throw new NotImplementedException(); }
                public bool Remove(Node item) { return nodes.Remove(item); }
                public int IndexOf(Node item) { throw new NotImplementedException(); }
                public void Insert(int index, Node item) { throw new NotImplementedException(); }
                public void RemoveAt(int index) { throw new NotImplementedException(); }
                public IEnumerator<Node> GetEnumerator() { return nodes.GetEnumerator(); }
                IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        Assert.Contains("set(int index, Object", code, StringComparison.Ordinal);
        Assert.Contains("copyTo(Object", code, StringComparison.Ordinal);
        Assert.Contains("indexOf(Object", code, StringComparison.Ordinal);
        Assert.Contains("insert(int index, Object", code, StringComparison.Ordinal);
    }

    // 7. 属性 setter 与同名用户方法（C# 里是不同成员）映射到同一 Java 名时，
    //    之前的去重会丢掉有真实副作用的方法（Obstacle.SetConvexHull 没清 Clump，
    //    导致 Rectilinear 的 9 个 "overlapped obstacles cannot be in a convex hull"）。
    [Fact]
    public void PropertySetterAndUserMethodCollision_RenamesUserMethod()
    {
        var result = Convert("""
            class Hull { }

            class Obstacle
            {
                public Hull ConvexHull { get; private set; }
                public bool IsRectangle { get; set; }

                internal void SetConvexHull(Hull hull)
                {
                    this.ConvexHull = hull;
                    this.IsRectangle = false;
                }

                public void Use(Obstacle other, Hull hull)
                {
                    other.SetConvexHull(hull);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        // Bean accessor keeps its generated name; the real method gets a suffix.
        Assert.Contains("setConvexHull(Hull value)", code, StringComparison.Ordinal);
        Assert.Contains("public void setConvexHull_2(Hull hull)", code, StringComparison.Ordinal);
        Assert.Contains("setConvexHull_2(hull)", code, StringComparison.Ordinal);
    }

    // 8. 条件表达式按分支插入 value-copy clone：新鲜的分支（CreateAnEmptyBox()）
    //    不能再被用户 Clone() 重建（Rectangle.Clone 从角点重建会把空盒变成非空，
    //    导致 Node.boundingBox_WithNullBoundaryCurve_ReturnsEmptyBox 失败）。
    [Fact]
    public void ConditionalStructReturn_ClonesOnlyNonFreshBranch()
    {
        var result = Convert("""
            using Microsoft.Msagl.Core.Geometry;

            public struct Rect
            {
                public double Left;
                public double Right;

                public Rect Clone() { return new Rect { Left = Left, Right = Right }; }

                public static Rect CreateAnEmptyBox() { return new Rect { Left = 0, Right = -1 }; }
            }

            class Curve
            {
                public Rect BoundingBox { get { return Rect.CreateAnEmptyBox(); } }
            }

            class Node
            {
                private Curve curve;

                public Rect BoundingBox
                {
                    get { return curve != null ? curve.BoundingBox : Rect.CreateAnEmptyBox(); }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode!;

        Assert.DoesNotContain("createAnEmptyBox().clone()", code, StringComparison.Ordinal);
        Assert.Contains(".clone()", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "AssertionSemanticsSample.cs",
            Options = new ConversionOptions(),
        });
    }
}
