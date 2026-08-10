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
        System.IO.File.WriteAllText(@"D:\code\cs2j\.scratch-a3\test-out\rtree.java", rTree.GeneratedCode);
        Assert.True(rTree.Success, string.Join("\n", rTree.Diagnostics));
        Assert.DoesNotContain("Class<?> clazz", rTree.GeneratedCode);
        Assert.Contains("addNodeToTreeRecursive(newNode, existingNode.Left);", rTree.GeneratedCode);
        Assert.Contains("addNodeToTreeRecursive(node, _rootNode);", rTree.GeneratedCode);
    }

    [Fact]
    public async Task ErasureRenamedGenericOverload_CallSiteUsesRenamedName()
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
            new SourceFile { FilePath = "RectangleNodeUtils.cs", Content = """
                namespace Demo
                {
                    internal static class RectangleNodeUtils
                    {
                        internal static void CrossRectangleNodes<TA, TB, P>(RectangleNode<TA, P> a, RectangleNode<TB, P> b, System.Action<TA, TB> action)
                        {
                            if (a.Left == null && b.Left == null)
                                action(a.UserData, b.UserData);
                            else
                            {
                                CrossRectangleNodes<TA, TB, P>(a.Left, b.Left, action);
                                CrossRectangleNodes<TA, TB, P>(a.Right, b.Right, action);
                            }
                        }

                        internal static void CrossRectangleNodes<TA, P>(RectangleNode<TA, P> a, RectangleNode<TA, P> b, System.Action<TA, TA> action)
                        {
                            if (a.Left == null && b.Left == null)
                                action(a.UserData, b.UserData);
                            else
                            {
                                CrossRectangleNodes<TA, P>(a.Left, b.Left, action);
                                CrossRectangleNodes<TA, P>(a.Right, b.Right, action);
                            }
                        }
                    }
                }
                """ },
            new SourceFile { FilePath = "UseIt.cs", Content = """
                namespace Demo
                {
                    public class UseIt
                    {
                        public void N()
                        {
                            var a = new RectangleNode<int, Point>(1, new Point(0, 0));
                            var b = new RectangleNode<int, Point>(2, new Point(1, 1));
                            // Explicit two type arguments bind to the 2tp overload
                            // (a bare call with both nodes of type RectangleNode<int, Point>
                            // would be ambiguous in C# itself ? CS0121).
                            // Lambda body must avoid System.Console ? the test compilation
                            // references only core libs, and a broken lambda body makes the
                            // whole call unresolvable (CS1069 -> OverloadResolutionFailure).
                            RectangleNodeUtils.CrossRectangleNodes<int, Point>(a, b, (x, y) => { var s = x + y; s.ToString(); });
                        }
                    }
                }
                """ },
        });

        var utils = Assert.Single(results, r => r.FileName == "RectangleNodeUtils.java");
        System.IO.File.WriteAllText(@"D:\code\cs2j\.scratch-a3\test-out\utils.java", utils.GeneratedCode);
        System.IO.File.WriteAllText(@"D:\code\cs2j\.scratch-a3\test-out\useit.java", Assert.Single(results, r => r.FileName == "UseIt.java").GeneratedCode);
        Assert.True(utils.Success, "UTILS:\n" + utils.GeneratedCode + "\n" + string.Join("\n", utils.Diagnostics));
        Assert.Contains("crossRectangleNodes_2tp", utils.GeneratedCode);
        // Both overloads got _cs2j_ params; the 3tp declaration keeps the base name,
        // the 2tp one is erasure-renamed.
        Assert.Contains("void crossRectangleNodes(Class<TA> _cs2j_TA, Class<TB> _cs2j_TB, Class<P> _cs2j_P,", utils.GeneratedCode);
        Assert.Contains("void crossRectangleNodes_2tp(Class<TA> _cs2j_TA, Class<P> _cs2j_P,", utils.GeneratedCode);
        // Recursive calls pass tokens in the same order as the signature declares them.
        Assert.Contains("crossRectangleNodes(_cs2j_TA, _cs2j_TB, _cs2j_P, a.Left, b.Left, action);", utils.GeneratedCode);
        Assert.Contains("crossRectangleNodes_2tp(_cs2j_TA, _cs2j_P, a.Left, b.Left, action);", utils.GeneratedCode);
        var callSite = Assert.Single(results, r => r.FileName == "UseIt.java");
        Assert.True(callSite.Success, "CALLSITE:\n" + callSite.GeneratedCode + "\n" + string.Join("\n", callSite.Diagnostics));
        // The call site must use the same erasure-renamed name as the declaration,
        // with Class tokens matching the signature order.
        Assert.Contains("crossRectangleNodes_2tp(int.class, Point.class, a, b,", callSite.GeneratedCode);
        Assert.DoesNotContain("crossRectangleNodes(a, b,", callSite.GeneratedCode);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
