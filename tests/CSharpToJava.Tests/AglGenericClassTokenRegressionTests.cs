using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the MSAGL conversion (agl-master).
/// Three generic-method / Class-token bugs surfaced in the first mvn build:
/// 1. Interface methods (ICurve.Derivative) got spurious &lt;P, TA&gt; type parameters and
///    Class&lt;TA&gt;/Class&lt;P&gt; tokens drained from the GLOBAL pending set after
///    RectangleNodeUtils generic methods registered their own type parameters.
/// 2. Static methods in a generic class (RTree.AddNodeToTreeRecursive using default(T))
///    got a Class&lt;?&gt; clazz parameter but call sites never pass it.
/// 3. Erasure-renamed generic overloads (CrossRectangleNodes_2tp) are called by their
///    original name at call sites.
/// </summary>
public class AglGenericClassTokenRegressionTests
{
    [Fact]
    public async Task InterfaceMethod_DoesNotDrainGlobalPendingClassTypeParams()
    {
        var results = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Point.cs", Content = """
                namespace Demo
                {
                    public struct Point
                    {
                        public double X;
                        public double Y;
                        public Point(double x, double y) { X = x; Y = y; }
                    }
                }
                """ },
            new SourceFile { FilePath = "ICurve.cs", Content = """
                namespace Demo
                {
                    public interface ICurve
                    {
                        Point Derivative(double t);
                    }
                }
                """ },
            new SourceFile { FilePath = "RectangleNode.cs", Content = """
                namespace Demo
                {
                    public class RectangleNode<T, P>
                    {
                        public T UserData;
                        public P Rect;
                        public RectangleNode<T, P> Left;
                        public RectangleNode<T, P> Right;
                        public RectangleNode(T userData, P rect) { UserData = userData; Rect = rect; }
                    }
                }
                """ },
            new SourceFile { FilePath = "RectangleNodeUtils.cs", Content = """
                namespace Demo
                {
                    internal static class RectangleNodeUtils
                    {
                        internal static void CrossRectangleNodes<TA, P>(RectangleNode<TA, P> a, RectangleNode<TA, P> b, System.Action<TA, TA> action)
                        {
                            if (a.Left == null && b.Left == null)
                                action(a.UserData, b.UserData);
                            else if (a.Left == null)
                            {
                                CrossRectangleNodes<TA, P>(a, b.Left, action);
                                CrossRectangleNodes<TA, P>(a, b.Right, action);
                            }
                            else
                            {
                                if (b.Left != null)
                                {
                                    CrossRectangleNodes<TA, P>(a.Left, b.Left, action);
                                    CrossRectangleNodes<TA, P>(a.Left, b.Right, action);
                                    CrossRectangleNodes<TA, P>(a.Right, b.Left, action);
                                    CrossRectangleNodes<TA, P>(a.Right, b.Right, action);
                                }
                            }
                        }
                    }
                }
                """ },
        });

        var iCurve = Assert.Single(results, r => r.FileName == "ICurve.java");
        Assert.True(iCurve.Success, string.Join("\n", iCurve.Diagnostics));
        Assert.DoesNotContain("<P, TA>", iCurve.GeneratedCode);
        Assert.DoesNotContain("_cs2j_", iCurve.GeneratedCode);
        Assert.Contains("Point derivative(double t);", iCurve.GeneratedCode);
    }

    [Fact]
    public async Task StaticMethodInGenericClass_DefaultT_DoesNotGetOrphanClassParameter()
    {
        var results = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Point.cs", Content = """
                namespace Demo
                {
                    public struct Point
                    {
                        public double X;
                        public double Y;
                        public Point(double x, double y) { X = x; Y = y; }
                    }
                }
                """ },
            new SourceFile { FilePath = "RectangleNode.cs", Content = """
                namespace Demo
                {
                    public class RectangleNode<T, P>
                    {
                        public T UserData;
                        public P Rect;
                        public RectangleNode<T, P> Left;
                        public RectangleNode<T, P> Right;
                        public RectangleNode(T userData, P rect) { UserData = userData; Rect = rect; }
                    }
                }
                """ },
            new SourceFile { FilePath = "RTree.cs", Content = """
                namespace Demo
                {
                    public class RTree<T, P>
                    {
                        RectangleNode<T, P> _rootNode;

                        public void Add(RectangleNode<T, P> node)
                        {
                            AddNodeToTreeRecursive(node, _rootNode);
                        }

                        static void AddNodeToTreeRecursive(RectangleNode<T, P> newNode, RectangleNode<T, P> existingNode)
                        {
                            if (existingNode.Left == null)
                            {
                                existingNode.Left = newNode;
                                existingNode.UserData = default(T);
                            }
                            else
                            {
                                AddNodeToTreeRecursive(newNode, existingNode.Left);
                            }
                        }
                    }
                }
                """ },
        });

        var rTree = Assert.Single(results, r => r.FileName == "RTree.java");
        Assert.True(rTree.Success, string.Join("\n", rTree.Diagnostics));
        Assert.DoesNotContain("Class<?> clazz", rTree.GeneratedCode);
        Assert.Contains("addNodeToTreeRecursive(newNode, existingNode.Left);", rTree.GeneratedCode);
        Assert.Contains("addNodeToTreeRecursive(node, _rootNode);", rTree.GeneratedCode);
    }


    [Fact]
    public async Task NestedEnumMemberAccess_DoesNotDoubleQualifyNamespace()
    {
        // Regression for agl-master msagltests EdgeLabelPlacementTest: `Label.PlacementSide.Top`
        // (Label from a using'd namespace) was emitted as
        // `Microsoft.Msagl.Core.Layout.Microsoft.Msagl.Core.Layout.Label.PlacementSide.Top`
        // — the namespace package was prepended to an already fully-qualified reference.
        var results = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Label.cs", Content = """
                namespace Demo.Layout
                {
                    public class Label
                    {
                        public enum PlacementSide { Top, Bottom }
                    }
                }
                """ },
            new SourceFile { FilePath = "Foo.cs", Content = """
                using Demo.Layout;
                namespace Demo.UnitTests
                {
                    public class Foo
                    {
                        public int GetPossibleSides(Label.PlacementSide side) { return 0; }

                        public void Bar()
                        {
                            GetPossibleSides(Label.PlacementSide.Top);
                        }
                    }
                }
                """ },
        });

        var foo = Assert.Single(results, r => r.FileName == "Foo.java");
        Assert.True(foo.Success, string.Join("\n", foo.Diagnostics));
        Assert.DoesNotContain("Demo.Layout.Demo.Layout", foo.GeneratedCode);
        Assert.DoesNotContain("Layout.Demo", foo.GeneratedCode);
        // The reference must be qualified at most once.
        Assert.Contains("Label.PlacementSide.Top", foo.GeneratedCode);
        Assert.Contains("Label.PlacementSide side", foo.GeneratedCode);
    }

    [Fact]
    public async Task RuntimeClassField_IsInstanceField_NotStatic()
    {
        // Regression for agl-master QUT.Gppg.PushdownPrefixState<T>: the runtime
        // Class token field for a class-level type parameter was emitted as STATIC,
        // so instantiating the generic class twice with different type arguments
        // clobbered the shared token. In ShiftReduceParser, valueStack<ValueType>
        // and locationStack<LexLocation> then both grew with the LAST-created
        // class token, producing
        //   "arraycopy: type mismatch: can not copy ValueType[] into LexLocation[]".
        // The field must be per-instance.
        var results = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Box.cs", Content = """
                namespace Demo
                {
                    public class Box<T>
                    {
                        public T[] Empty() { return new T[0]; }
                        public Box() { }
                        public Box(int seed) { }
                    }
                }
                """ },
            new SourceFile { FilePath = "Use.cs", Content = """
                namespace Demo
                {
                    public class Use
                    {
                        public void Go()
                        {
                            var a = new Box<Integer>(1);
                            var b = new Box<String>(2);
                            a.Empty();
                            b.Empty();
                        }
                    }
                }
                """ },
        });

        var box = Assert.Single(results, r => r.FileName == "Box.java");
        Assert.True(box.Success, string.Join("\n", box.Diagnostics));
        Assert.DoesNotContain("static Class<?>", box.GeneratedCode);
        Assert.Contains("private Class<?> tClass;", box.GeneratedCode);
        // Every constructor must receive and store the token per instance.
        Assert.Contains("this.tClass = tClass;", box.GeneratedCode);
    }
    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
