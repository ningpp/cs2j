using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Core 单测：残余集合、shim 决策/合成（设计 §7.1-9）、变异方法闭包、
/// VS-1/2/4 站点枚举与改写（M3）、overrides。
/// </summary>
public class StructValueSemanticsUnitTests
{

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static CSharpCompilation BuildCompilation(params string[] sources)
    {
        var trees = sources.Select(s => (SyntaxTree)CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        return CSharpCompilation.Create(
            "svs-test",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private sealed class PlanRun
    {
        public required StructValueSemanticsPreprocessor Preprocessor { get; init; }
        public required CSharpCompilation Compilation { get; init; }
        public required StructValueSemanticsStatistics Statistics { get; init; }
        public required List<StructValueSemanticsDiagnostic> Diagnostics { get; init; }
        public required List<string> Outputs { get; init; }

        public string Output => Assert.Single(Outputs);
    }

    /// <summary>BindCompilation + 逐文件 RewriteFile（M2 不改写，仅触发计划期产物合并）。</summary>
    private static PlanRun Analyze(string source, StructValueSemanticsOptions? options = null)
        => Analyze(new[] { source }, options);

    private static PlanRun Analyze(string[] sources, StructValueSemanticsOptions? options = null)
    {
        var compilation = BuildCompilation(sources);
        var pp = new StructValueSemanticsPreprocessor(options ?? new StructValueSemanticsOptions());
        pp.BindCompilation(compilation);

        var stats = new StructValueSemanticsStatistics();
        var diags = new List<StructValueSemanticsDiagnostic>();
        var outputs = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var source = tree.GetText().ToString();
            var result = pp.RewriteFile(source, compilation.GetSemanticModel(tree), stats, diags);
            outputs.Add(result.Changed ? result.OutputCode : source);
        }

        return new PlanRun
        {
            Preprocessor = pp,
            Compilation = compilation,
            Statistics = stats,
            Diagnostics = diags,
            Outputs = outputs,
        };
    }

    private static void AssertOutputCompiles(string output)
    {
        var errors = BuildCompilation(output).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten output failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private static ClonePlan Plan(PlanRun run, string qualifiedName)
        => Assert.Single(run.Preprocessor.Plans.Where(p => p.StructQualifiedName == qualifiedName));

    /// <summary>把每个激活 plan 的合成 shim 注入主声明，编译验证（设计 §4.4 可编译硬不变式）。</summary>
    private static void AssertShimsCompile(string source, PlanRun run)
    {
        var root = CSharpSyntaxTree.ParseText(source, ParseOptions).GetRoot();
        var generatedByType = run.Preprocessor.Plans
            .Where(p => p.IsActive && p.GeneratedCloneMethod != null)
            .ToDictionary(p => p.StructShortName, p =>
            {
                var members = new List<MemberDeclarationSyntax>();
                if (p.GeneratedCopyConstructor != null) members.Add(p.GeneratedCopyConstructor);
                members.Add(p.GeneratedCloneMethod!);
                return members;
            });

        root = root.ReplaceNodes(
            root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(t => generatedByType.ContainsKey(t.Identifier.Text)).ToList(),
            (node, _) =>
            {
                var members = generatedByType[node.Identifier.Text];
                return node switch
                {
                    StructDeclarationSyntax s => s.AddMembers(members.ToArray()),
                    RecordDeclarationSyntax r => r.AddMembers(members.ToArray()),
                    _ => node,
                };
            });

        var output = root.NormalizeWhitespace().ToFullString();
        var verify = BuildCompilation(output);
        var errors = verify.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Synthesized shim failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    // ---------- 残余集合与 shim 基本形态 ----------

    [Fact]
    public void MutableStruct_BecomesActivePlan_WithGeneratedShim()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public int Y;
                public void Move(int dx, int dy) { X += dx; Y += dy; }
            }
            """);

        var plan = Plan(run, "Lib.Point");
        Assert.True(plan.IsActive);
        Assert.Equal(ShimKind.GenerateCopyCtorAndClone, plan.ShimKind);
        Assert.NotNull(plan.GeneratedCopyConstructor);
        Assert.NotNull(plan.GeneratedCloneMethod);
        Assert.Contains(plan.PrimaryDeclarationFile, plan.TouchedFiles);
        AssertShimsCompile("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public int Y;
                public void Move(int dx, int dy) { X += dx; Y += dy; }
            }
            """, run);
        Assert.Equal(1, run.Statistics.StructsPlanned);
        Assert.Equal(1, run.Statistics.ShimsGenerated);
    }

    [Fact]
    public void ReadonlyStruct_RefStruct_AndClass_AreExcluded()
    {
        var run = Analyze("""
            namespace Lib;
            public readonly struct R { public int X => 1; }
            public ref struct Buf { public System.Span<int> S; }
            public class C { public int X; }
            public enum E { A, B }
            """);

        Assert.Empty(run.Preprocessor.Plans);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV011);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV012);
    }

    [Fact]
    public void EmptyStruct_StillGetsShim()
    {
        var run = Analyze("namespace Lib; public struct Marker { }");
        var plan = Plan(run, "Lib.Marker");
        Assert.True(plan.IsActive);
        Assert.NotNull(plan.GeneratedCopyConstructor);
        AssertShimsCompile("namespace Lib; public struct Marker { }", run);
    }

    // ---------- §4.4 字段复制矩阵 ----------

    [Fact]
    public void CopyCtor_HandlesFiveFieldKinds_AndRecursiveClone()
    {
        var source = """
            namespace Lib;
            public struct Inner { public int V; public void Bump() { V++; } }
            public readonly struct Fixed { public int V { get; } }
            public struct Outer
            {
                public int Primitive;
                public string? Text;
                public int[]? Array;
                public Inner Mutable;
                public Fixed Immutable;
                public readonly int ReadOnlyField;
            }
            """;
        var run = Analyze(source);
        var outer = Plan(run, "Lib.Outer");
        var ctor = outer.GeneratedCopyConstructor!.ToFullString();

        Assert.Contains("Primitive = other.Primitive;", ctor);
        Assert.Contains("Text = other.Text;", ctor);
        Assert.Contains("Array = other.Array;", ctor);
        Assert.Contains("Mutable = other.Mutable.Clone();", ctor);
        Assert.Contains("Immutable = other.Immutable;", ctor);
        Assert.DoesNotContain("Immutable = other.Immutable.Clone();", ctor);
        Assert.Contains("ReadOnlyField = other.ReadOnlyField;", ctor);
        AssertShimsCompile(source, run);
    }

    [Fact]
    public void CopyCtor_CopiesAutoPropertyBackingFields_ViaProperty()
    {
        var source = """
            namespace Lib;
            public struct Bag
            {
                public int GetSet { get; set; }
                public int GetOnly { get; }
                public int InitOnly { get; init; }
                public Bag(int v) { GetSet = v; GetOnly = v; InitOnly = v; }
            }
            """;
        var run = Analyze(source);
        var ctor = Plan(run, "Lib.Bag").GeneratedCopyConstructor!.ToFullString();

        Assert.Contains("GetSet = other.GetSet;", ctor);
        Assert.Contains("GetOnly = other.GetOnly;", ctor);
        Assert.Contains("InitOnly = other.InitOnly;", ctor);
        AssertShimsCompile(source, run);
    }

    [Fact]
    public void GenericStruct_ShimCompiles_WithTypeArguments_AndRecursiveClone()
    {
        var source = """
            namespace Lib;
            public struct Box<T>
            {
                public T Payload;
                public void Touch() { Payload = default!; }
            }
            public struct Node<T>
            {
                public T Value;
                public Box<T> Inner;
            }
            """;
        var run = Analyze(source);
        var node = Plan(run, "Lib.Node<T>");
        var ctor = node.GeneratedCopyConstructor!.ToFullString();
        // 泛型体内自引用必须带类型实参（CS0305）。
        Assert.Contains("private Node(Node<T> other)", ctor);
        // 构造泛型字段 Box<T> 经 OriginalDefinition 命中残余 plan → 递归 Clone。
        Assert.Contains("Inner = other.Inner.Clone();", ctor);
        Assert.Contains("Value = other.Value;", ctor);
        var clone = node.GeneratedCloneMethod!.ToFullString();
        Assert.Contains("public Node<T> Clone() => new Node<T>(this);", clone);
        AssertShimsCompile(source, run);
    }

    [Fact]
    public void RequiredMembers_GetSetsRequiredMembersOnCopyCtor()
    {
        var source = """
            namespace Lib;
            public struct Box
            {
                public required int Must;
                public Box() { Must = 0; }
            }
            """;
        var run = Analyze(source);
        var ctor = Plan(run, "Lib.Box").GeneratedCopyConstructor!.ToFullString();
        Assert.Contains("SetsRequiredMembers", ctor);
        AssertShimsCompile(source, run);
    }

    // ---------- shim 决策：复用/冲突/事件/既有拷贝构造 ----------

    [Fact]
    public void ExistingPublicParameterlessClone_IsReused()
    {
        var source = """
            namespace Lib;
            public struct Point
            {
                public int X;
                public Point Clone() => new Point { X = X };
            }
            """;
        var run = Analyze(source);
        var plan = Plan(run, "Lib.Point");
        Assert.True(plan.IsActive);
        Assert.Equal(ShimKind.ReuseExistingClone, plan.ShimKind);
        Assert.Null(plan.GeneratedCloneMethod);
        Assert.Null(plan.GeneratedCopyConstructor);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV010);
        Assert.Equal(1, run.Statistics.StructsReusedClone);
    }

    [Fact]
    public void CloneReturningObject_IsInfeasible_SV050()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public object Clone() => new object();
            }
            """);
        var plan = Plan(run, "Lib.Point");
        Assert.Equal(ClonePlanOutcome.Infeasible, plan.Outcome);
        Assert.Equal(StructValueSemanticsDiagnosticCode.SV050, plan.FailCode);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV050);
        Assert.Equal(1, run.Statistics.PlansSkipped);
    }

    [Fact]
    public void PrivateClone_IsInfeasible_SV050()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                private Point Clone() => this;
            }
            """);
        var plan = Plan(run, "Lib.Point");
        Assert.Equal(ClonePlanOutcome.Infeasible, plan.Outcome);
        Assert.Equal(StructValueSemanticsDiagnosticCode.SV050, plan.FailCode);
    }

    [Fact]
    public void FieldStyleEvent_IsInfeasible_SV051()
    {
        var run = Analyze("""
            namespace Lib;
            using System;
            public struct Publisher
            {
                public int X;
                public event Action? Changed;
            }
            """);
        var plan = Plan(run, "Lib.Publisher");
        Assert.Equal(ClonePlanOutcome.Infeasible, plan.Outcome);
        Assert.Equal(StructValueSemanticsDiagnosticCode.SV051, plan.FailCode);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV051);
    }

    [Fact]
    public void ExistingCopyCtor_GeneratesCloneOnly()
    {
        var source = """
            namespace Lib;
            public struct Point
            {
                public int X;
                public Point(Point other) { X = other.X; }
            }
            """;
        var run = Analyze(source);
        var plan = Plan(run, "Lib.Point");
        Assert.True(plan.IsActive);
        Assert.Null(plan.GeneratedCopyConstructor);
        Assert.NotNull(plan.GeneratedCloneMethod);
        AssertShimsCompile(source, run);
    }

    // ---------- 变异方法集（直接 + 传递闭包）----------

    [Fact]
    public void MutatingMethods_IncludeDirectAndTransitiveDelegates()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Rect
            {
                public int X;
                public void Move(int d) { X += d; }
                public void Shift(int d) { Move(d); Move(d); }
                public int Peek() => X;
            }
            """);
        var plan = Plan(run, "Lib.Rect");
        Assert.Contains(plan.MutatingMethods, m => m.Name == "Move");
        Assert.Contains(plan.MutatingMethods, m => m.Name == "Shift");
        Assert.DoesNotContain(plan.MutatingMethods, m => m.Name == "Peek");
    }

    // ---------- VS-1/2/4 站点枚举 ----------

    [Fact]
    public void Sites_LocalInit_Assign_Return_Argument_ArrayStore_AreEnumerated()
    {
        var run = Analyze("""
            namespace Lib;
            public static class Helper
            {
                public struct S { public int X; }
                public static S Take(S p) { return p; }
                public static void Use(S[] arr, S e)
                {
                    S a = e;
                    a = e;
                    arr[0] = e;
                    Take(e);
                }
            }
            """);

        var plan = Plan(run, "Lib.Helper.S");
        var kinds = plan.Sites.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(1, kinds[SiteKind.LocalInit]);
        Assert.Equal(1, kinds[SiteKind.Assign]);
        Assert.Equal(1, kinds[SiteKind.ArrayStore]);
        Assert.Equal(1, kinds[SiteKind.Argument]);
        Assert.Equal(1, kinds[SiteKind.Return]); // Take(S p) { return p; }
    }

    [Fact]
    public void ChainedAssignment_IsOneStatementPositionSite()
    {
        var run = Analyze("""
            namespace Lib;
            public static class Helper
            {
                public struct S { public int X; }
                public static void Use(S a, S b, S c) { a = b = c; }
            }
            """);
        var plan = Plan(run, "Lib.Helper.S");
        var chained = Assert.Single(plan.Sites.Where(s => s.Kind == SiteKind.ChainedAssign));
        Assert.True(chained.IsStatementPosition);
    }

    [Fact]
    public void RefLocalAliasAssignment_IsNotEnumerated()
    {
        var run = Analyze("""
            namespace Lib;
            public static class Helper
            {
                public struct S { public int X; }
                public static ref S Pick(ref S a, ref S b) => ref a;
                public static void Use(ref S a, ref S b, S c)
                {
                    ref S alias = ref a;
                    alias = c;
                    Pick(ref a, ref b) = c;
                }
            }
            """);
        var plan = Plan(run, "Lib.Helper.S");
        // ref local 初始化被排除；两处别名写入（ref local、ref-return 方法）均不得建值赋值站点。
        Assert.DoesNotContain(plan.Sites, s => s.Kind == SiteKind.Assign);
    }

    [Fact]
    public void SkipOverride_PlansSkippedWith_SV020()
    {
        var options = new StructValueSemanticsOptions
        {
            SkipQualifiedNames = new HashSet<string>(StringComparer.Ordinal) { "Lib.Point" },
        };
        var run = Analyze("namespace Lib; public struct Point { public int X; }", options);
        var plan = Plan(run, "Lib.Point");
        Assert.Equal(ClonePlanOutcome.Skipped, plan.Outcome);
        Assert.Equal(StructValueSemanticsDiagnosticCode.SV020, plan.FailCode);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV020);
        Assert.False(plan.IsActive);
    }

    // ---------- M3：站点改写（VS-1/2/4）+ shim 注入 ----------

    [Fact]
    public void Rewrite_LocalInit_InsertsCloneAndShim_OutputCompiles()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; public int Y; }
            public static class Helper
            {
                public static void Use(Point e) { Point a = e; a.X = 1; }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("Point a = e.Clone();", output);
        Assert.Contains("public Point Clone()", output);
        Assert.Contains("private Point(Point other)", output);
        Assert.Equal(1, run.Statistics.LocalInitSites);
        Assert.Equal(1, run.Statistics.SitesRewritten);
        Assert.Equal(1, run.Statistics.FilesTransformed);
    }

    [Fact]
    public void Rewrite_Assign_ArrayStore_Return_AreWrapped()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public Point Echo() { return this; }
            }
            public static class Helper
            {
                public static Point[] Store = new Point[2];
                public static void Use(Point e)
                {
                    Point a;
                    a = e;
                    Store[0] = e;
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("a = e.Clone();", output);
        Assert.Contains("Store[0] = e.Clone();", output);
        Assert.Contains("return this.Clone();", output);
        Assert.Equal(1, run.Statistics.AssignSites);
        Assert.Equal(1, run.Statistics.ArrayStoreSites);
        Assert.Equal(1, run.Statistics.ReturnSites);
    }

    [Fact]
    public void Rewrite_FreshValues_AreNotCloned()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static Point Use()
                {
                    Point a = new Point();
                    Point b = default;
                    return new Point();
                }
            }
            """);

        Assert.DoesNotContain(".Clone()", run.Output.Replace("new Point(this)", ""));
        Assert.Equal(0, run.Statistics.SitesRewritten);
    }

    [Fact]
    public void Rewrite_Argument_MutatingCalleeCloned_ReadOnlyFreshPropertySkipped()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public static Point Origin => default;
                public static void Consume(Point p) { p.X = 1; }
                public static void Print(Point p) { System.Console.Write(p.X); }
                public static void Use(Point s)
                {
                    Consume(s);
                    Print(s);
                    Consume(new Point());
                    Consume(Origin);
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("Consume(s.Clone());", output);
        Assert.Contains("Print(s);", output);
        Assert.Contains("Consume(new Point());", output);
        Assert.Contains("Consume(Origin);", output);
        Assert.Equal(1, run.Statistics.ArgumentSites);
    }

    [Fact]
    public void Rewrite_Argument_CalleeInvokingMutatingMethod_IsNotReadOnly()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Move(int d) { X += d; }
                public static void Consume(Point p) { p.Move(1); }
                public static void Use(Point s) { Consume(s); }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        // p.Move(...) 命中 Point 变异方法闭包 → 形参非只读 → 调用点必须 clone。
        Assert.Contains("Consume(s.Clone());", output);
        Assert.Equal(1, run.Statistics.ArgumentSites);
    }

    [Fact]
    public void Rewrite_ArrayElementLoad_AlwaysCloned_W4Regression()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; public static void Consume(Point p) { p.X.ToString(); } }
            public static class Helper
            {
                public static void Use(Point[] arr)
                {
                    Point a = arr[0];
                    Point.Consume(arr[0]);
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        // Consume 体内 p.X.ToString() 不构成变异 → 形参只读判定通过，
        // 但数组元素读取在 C# 侧是副本边界，消费点仍必须插（W4 回归）。
        Assert.Contains("Point a = arr[0].Clone();", output);
        Assert.Contains("Point.Consume(arr[0].Clone());", output);
        Assert.Equal(2, run.Statistics.ArrayLoadSites);
    }

    [Fact]
    public void Rewrite_ChainedAssignment_HoistsTempAndClonesInReverse()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static void Use(Point a, Point b, Point c) { a = b = c; }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("var __cs2jSc0 = c;", output);
        Assert.Contains("b = __cs2jSc0.Clone();", output);
        Assert.Contains("a = __cs2jSc0.Clone();", output);
        Assert.Equal(1, run.Statistics.ChainedAssignSites);
    }

    [Fact]
    public void Rewrite_ExistingClone_SelfReturnNotCloned_OtherReturnsAre()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public Point Clone() { return this; }
                public Point Echo() { return this; }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        // 复用既有 Clone（不再生成 shim）；Clone() 自身 return this 防自递归。
        Assert.Equal(ShimKind.ReuseExistingClone, Plan(run, "Lib.Point").ShimKind);
        Assert.Contains("public Point Clone() { return this; }", output);
        Assert.Contains("return this.Clone();", output);
    }

    [Fact]
    public void Rewrite_ComplexExpression_IsParenthesizedBeforeClone()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static Point Use(Point a, Point b, bool flag)
                {
                    Point c = flag ? a : b;
                    return c;
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("Point c = (flag ? a : b).Clone();", output);
    }

    [Fact]
    public void Rewrite_IsIdempotent_SecondRunProducesNoDiff()
    {
        var first = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static Point Take(Point p) { p.X.ToString(); return p; }
                public static void Use(Point[] arr, Point e)
                {
                    Point a = e;
                    arr[0] = a;
                    Take(arr[1]);
                }
            }
            """);
        AssertOutputCompiles(first.Output);

        var second = Analyze(first.Output);
        Assert.Equal(first.Output, second.Output);
        Assert.Equal(0, second.Statistics.SitesRewritten);
        Assert.Equal(0, second.Statistics.FilesTransformed);
    }

    // ---------- M4-a：VS-3 防御性接收者（§7.1-6）----------

    [Fact]
    public void Rewrite_DefensiveReceiver_InParameter_IsHoisted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Move(int d) { X += d; }
                public static void Use(in Point p) { p.Move(1); }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("var __cs2jVc0 = p.Clone();", output);
        Assert.Contains("__cs2jVc0.Move(1);", output);
        Assert.Equal(1, run.Statistics.DefensiveReceiverSites);
    }

    [Fact]
    public void Rewrite_DefensiveReceiver_ReadOnlyField_IsHoisted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Move(int d) { X += d; }
            }
            public static class Helper
            {
                private static readonly Point Origin = new();
                public static void Use() { Origin.Move(1); }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("var __cs2jVc0 = Origin.Clone();", output);
        Assert.Contains("__cs2jVc0.Move(1);", output);
        Assert.Equal(1, run.Statistics.DefensiveReceiverSites);
    }

    [Fact]
    public void Rewrite_DefensiveReceiver_ForeachVariable_IsHoisted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Move(int d) { X += d; }
            }
            public static class Helper
            {
                public static void Use(Point[] points)
                {
                    foreach (var p in points) { p.Move(1); }
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("var __cs2jVc0 = p.Clone();", output);
        Assert.Contains("__cs2jVc0.Move(1);", output);
        Assert.Equal(1, run.Statistics.DefensiveReceiverSites);
    }

    [Fact]
    public void Rewrite_DefensiveReceiver_RvalueInvocation_IsHoisted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Move(int d) { X += d; }
                public static Point Get() => default;
                public static void Use() { Get().Move(1); }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("var __cs2jVc0 = Get().Clone();", output);
        Assert.DoesNotContain("(Get())", output);
        Assert.Contains("__cs2jVc0.Move(1);", output);
        Assert.Equal(1, run.Statistics.DefensiveReceiverSites);
    }

    [Fact]
    public void Rewrite_DefensiveReceiver_QueryMethod_AndWritableLocal_AreNotHoisted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Move(int d) { X += d; }
                public int Peek() => X;
                public static void Use(in Point p, Point s)
                {
                    System.Console.Write(p.Peek());
                    Point a = s;
                    a.Move(1);
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.DoesNotContain("__cs2jVc", output);
        Assert.Equal(0, run.Statistics.DefensiveReceiverSites);
        // 可写局部 a 从 s 初始化，仍是 VS-1a 副本点。
        Assert.Contains("Point a = s.Clone();", output);
    }

    [Fact]
    public void Rewrite_DefensiveReceiver_ExpressionPosition_EmitsSV043_NoHoist()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public int Bump() { X++; return X; }
                private static readonly Point Origin = new();
                public static int Use() { var y = Origin.Bump(); return y; }
            }
            """);

        AssertOutputCompiles(run.Output);
        Assert.DoesNotContain("__cs2jVc", run.Output);
        Assert.Contains(run.Diagnostics, d =>
            d.Code == StructValueSemanticsDiagnosticCode.SV043
            && d.Severity == StructValueSemanticsSeverity.Error);
        Assert.Equal(0, run.Statistics.DefensiveReceiverSites);
    }

    // ---------- M4-b：VS-5 装箱/接口/拆箱（§7.1-8）----------

    [Fact]
    public void Rewrite_Boxing_Assignment_Argument_AndCast_AreCloned()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public static void TakeObj(object o) { }
                public static void Use(Point s, object o)
                {
                    object boxed = s;
                    TakeObj(s);
                    Point back = (Point)o;
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("object boxed = s.Clone();", output);
        Assert.Contains("TakeObj(s.Clone());", output);
        Assert.Contains("Point back = ((Point)o).Clone();", output);
        Assert.Equal(2, run.Statistics.BoxingSites);
        Assert.Equal(1, run.Statistics.UnboxSites);
    }

    [Fact]
    public void Rewrite_Boxing_HarmlessFormattingSinks_AreNotCloned()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static void Use(Point s)
                {
                    System.Console.Write(s);
                    string.Concat(s);
                    string.Format("{0} {1}", 1, s);
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.DoesNotContain("s.Clone()", output);
        Assert.Equal(0, run.Statistics.BoxingSites);
    }

    [Fact]
    public void Rewrite_Boxing_SerializerFamilyGenericSink_IsCloned()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class JsonSerializer
            {
                public static void Serialize<T>(T value) { }
            }
            public static class Helper
            {
                public static void Use(Point s) { JsonSerializer.Serialize(s); }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("JsonSerializer.Serialize(s.Clone());", output);
        Assert.Equal(1, run.Statistics.BoxingSites);
    }

    [Fact]
    public void Rewrite_DynamicTarget_IsSkippedWith_SV014()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static void Use(Point s)
                {
                    dynamic d = s;
                    d.Move();
                }
            }
            """);

        AssertOutputCompiles(run.Output);
        Assert.DoesNotContain("s.Clone()", run.Output);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV014);
        Assert.Equal(0, run.Statistics.BoxingSites);
    }

    [Fact]
    public void Rewrite_InterfaceConversion_ClonedOnlyWhenMutatingMemberExists()
    {
        var run = Analyze("""
            namespace Lib;
            public interface IMut { void Apply(); }
            public interface IQuery { int Get(); }
            public struct Mut : IMut
            {
                public int X;
                public void Apply() { X++; }
            }
            public struct View : IQuery
            {
                public int X;
                public int Get() => X;
            }
            public static class Helper
            {
                public static void Use(Mut m, View v)
                {
                    IMut i = m;
                    IQuery q = v;
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        Assert.Contains("IMut i = m.Clone();", output);
        Assert.DoesNotContain("v.Clone()", output);
        Assert.Equal(1, run.Statistics.InterfaceSites);
    }

    [Fact]
    public void Rewrite_IsPatternUnbox_BindingIsWritableLocal_MutatedInPlace()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Move(int d) { X += d; }
            }
            public static class Helper
            {
                public static void Use(object o)
                {
                    if (o is Point p) { p.Move(1); }
                }
            }
            """);

        var output = run.Output;
        AssertOutputCompiles(output);
        // is-pattern 声明局部是普通可写局部（拆箱本身已产生独立副本），变异
        // 必须就地生效；它不是 foreach 迭代变量那种只读槽，不提升防御副本。
        Assert.Contains("if (o is Point p) { p.Move(1); }", output.Replace("\r\n", "\n"));
        Assert.DoesNotContain("p.Clone()", output);
        Assert.Equal(0, run.Statistics.DefensiveReceiverSites);
    }

    [Fact]
    public void Rewrite_ExpressionTreeLambda_IsSkippedWith_SV013()
    {
        var run = Analyze("""
            namespace Lib;
            using System;
            using System.Linq.Expressions;
            public struct Point { public int X; }
            public static class Helper
            {
                public static Point Id(Point p) => p;
                public static void Use(Point s)
                {
                    Expression<Func<Point>> e = () => Id(s);
                }
            }
            """);

        AssertOutputCompiles(run.Output);
        Assert.DoesNotContain("s.Clone()", run.Output);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV013);
        Assert.Equal(0, run.Statistics.ArgumentSites);
    }

    [Fact]
    public void BoxingSites_RespectInsertAtBoxingSitesOption()
    {
        var options = new StructValueSemanticsOptions { InsertAtBoxingSites = false };
        var run = Analyze("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static void Use(Point s, object o)
                {
                    object boxed = s;
                    Point back = (Point)o;
                }
            }
            """, options);

        AssertOutputCompiles(run.Output);
        Assert.Equal(0, run.Statistics.BoxingSites);
        Assert.Equal(0, run.Statistics.UnboxSites);
    }

    // ---------- M4-c：SV040/SV041/SV042 逃逸（§7.1-10）----------

    private static void AssertInfeasible(
        PlanRun run, string qualifiedName, StructValueSemanticsDiagnosticCode code)
    {
        var plan = Plan(run, qualifiedName);
        Assert.Equal(ClonePlanOutcome.Infeasible, plan.Outcome);
        Assert.False(plan.IsActive);
        Assert.Equal(code, plan.FailCode);
        Assert.Contains(run.Diagnostics, d =>
            d.Code == code && d.Severity == StructValueSemanticsSeverity.Error);
        // plan 不激活 → 无本类型 shim、无任何站点改写。
        Assert.Null(plan.GeneratedCloneMethod);
        Assert.Null(plan.GeneratedCopyConstructor);
        Assert.Empty(plan.Sites);
        Assert.DoesNotContain($"public {plan.StructShortName} Clone()", run.Output);
        Assert.DoesNotContain($"new {plan.StructShortName}(this)", run.Output);
        Assert.Equal(0, run.Statistics.SitesRewritten);
    }

    [Fact]
    public void Escape_RefThisArgument_IsSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Hand(ref Box other) { other.X++; }
                public void Escape() { Hand(ref this); }
            }
            """);
        AssertInfeasible(run, "Lib.Box", StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void Escape_RefReturnMutableField_IsSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Inner { public int X; }
            public struct Box
            {
                private Inner _f;
                public ref Inner Foo() => ref _f;
            }
            """);
        AssertInfeasible(run, "Lib.Box", StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void Escape_EscapingLambdaCapturingState_IsRewrittenToThisHolder()
    {
        // SV040-c/d 根因修复：CS1673 形态由方法级 this 持有者 copy-in/copy-out 消解，
        // 不再判无解（设计 §11：C32/C45 已承接普通局部捕获）。
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Arm() { System.Action a = () => X++; }
            }
            """);

        var plan = Plan(run, "Lib.Box");
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisCaptureRewrites);
        Assert.True(plan.ThisCaptureRewrites[0].HasWrites);
        Assert.Equal(1, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.Contains("__cs2jTc0", run.Output);
        Assert.Contains("this = __cs2jTc0", run.Output);
        AssertOutputCompiles(run.Output);
        Assert.DoesNotContain(run.Diagnostics, d =>
            d.Severity == StructValueSemanticsSeverity.Error
            && d.Code == StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void Escape_IteratorInstanceMethod_PlansAndCompiles()
    {
        // C# 迭代器状态机按值快照 this（C04 显式降级同为按值），不再是 SV040。
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public System.Collections.Generic.IEnumerable<int> Values()
                {
                    yield return X;
                    yield return X + 1;
                }
            }
            """);

        var plan = Plan(run, "Lib.Box");
        Assert.True(plan.IsActive);
        Assert.Empty(plan.ThisCaptureRewrites);
        AssertOutputCompiles(run.Output);
        Assert.DoesNotContain("__cs2jTc", run.Output);
        Assert.DoesNotContain(run.Diagnostics, d =>
            d.Severity == StructValueSemanticsSeverity.Error
            && d.Code == StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void Escape_ThisAssignment_InMethod_BecomesHolderCopyInCopyOut()
    {
        // SV041 根因修复：实例方法整体赋值 this 走方法级持有者 copy-in/copy-out
        // （复用 SV040-d 机制），不再判 Infeasible；构造函数/访问器/base 方法与
        // 无解形态由 CSharpToJava.Tests 的 StructValueSemanticsThisAssignmentTests 覆盖。
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Reset() { this = default; }
            }
            """);
        var plan = Plan(run, "Lib.Box");
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisCaptureRewrites);
        AssertOutputCompiles(run.Output);
        Assert.Contains("__cs2jTc0 = default;", run.Output);
        Assert.Contains("this = __cs2jTc0;", run.Output);
        Assert.DoesNotContain(run.Diagnostics, d =>
            d.Severity == StructValueSemanticsSeverity.Error
            && d.Code == StructValueSemanticsDiagnosticCode.SV041);
    }

    [Fact]
    public void Escape_PointerField_IsShallowCopied_NotRejected()
    {
        // SV042-a 根因修复：指针实例字段持有地址值，struct 值拷贝逐位复制该地址，
        // 拷贝构造 Ptr = other.Ptr 与 C# 值复制恒等（引用/数组字段同族浅拷贝），
        // 不再判 Infeasible；成员体 fixed/stackalloc/取址仍报 SV042。
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Box
            {
                public int* Ptr;
            }
            """);
        var plan = Plan(run, "Lib.Box");
        Assert.True(plan.IsActive);
        AssertOutputCompiles(run.Output);
        Assert.Contains("Ptr = other.Ptr;", run.Output);
        Assert.DoesNotContain(run.Diagnostics, d =>
            d.Severity == StructValueSemanticsSeverity.Error
            && d.Code == StructValueSemanticsDiagnosticCode.SV042);
    }

    [Fact]
    public void Escape_SpanOfSelfParameter_IsSV042()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Fill(System.Span<Point> xs) { xs[0] = this; }
            }
            """);
        AssertInfeasible(run, "Lib.Point", StructValueSemanticsDiagnosticCode.SV042);
    }

    [Fact]
    public void Escape_RefStructDeclaration_IsExcludedVia_SV011_NotSV042()
    {
        var run = Analyze("""
            namespace Lib;
            public ref struct Buf
            {
                public System.Span<byte> Bytes;
            }
            """);
        Assert.Empty(run.Preprocessor.Plans);
        Assert.Contains(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV011);
        Assert.DoesNotContain(run.Diagnostics, d => d.Code == StructValueSemanticsDiagnosticCode.SV042);
    }

    // ---------- §7.2 装箱可达矩阵（BoxingReachabilityAnalyzer 根集合/成员图传播）----------

    /// <summary>直接跑 BoxingReachabilityAnalyzer，返回可达源内 struct 的显示名集合。</summary>
    private static HashSet<string> BoxingReach(params string[] sources)
        => BoxingReachabilityAnalyzer
            .ComputeBoxingReachableStructs(BuildCompilation(sources))
            .Select(s => s.ToDisplayString())
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void BoxingReach_DirectRoots_AssignmentArgumentReturnAndCast()
    {
        var reachable = BoxingReach("""
            namespace Lib;
            public struct A { public int X; }
            public struct B { public int X; }
            public struct C { public int X; }
            public struct D { public int X; }
            public static class Helper
            {
                public static object GetA() { return new A(); }
                public static void Take(object o) { }
                public static void Use(B b, C c, D d)
                {
                    object x = b;          // object 赋值根
                    Take(c);               // object 实参根（用户方法，非格式化 sink）
                    object y = (object)d;  // 显式 cast 根
                }
            }
            """);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "Lib.A", "Lib.B", "Lib.C", "Lib.D",
        }, reachable);
    }

    [Fact]
    public void BoxingReach_HarmlessFormattingSinks_AndInterfaceConversions_AreNotRoots()
    {
        var console = BoxingReach("""
            namespace Lib;
            public struct P { public int X; }
            public static class Helper
            {
                public static void Use(P p) { System.Console.WriteLine(p); }
            }
            """);
        var iface = BoxingReach("""
            namespace Lib;
            public interface IFoo { void Apply(); }
            public struct Q : IFoo { public int X; public void Apply() { } }
            public static class Helper
            {
                public static void Use(Q q) { IFoo i = q; }
            }
            """);

        // 注：共享分析器对 string.Concat/Format/Join 的无害豁免受 ToDisplayString()
        // 关键字风格（"string"）影响而偏保守（会计为可达）——属 E13 共享代码的既有
        // 近似，C07 站点侧用 SpecialType 兜底（见 Rewrite_Boxing_HarmlessFormattingSinks
        // _AreNotCloned），此处只锁 Console 分支与接口转换两个确定负根。
        Assert.Empty(console);
        Assert.Empty(iface);
    }

    [Fact]
    public void BoxingReach_GenericTypeArguments_PropagateThroughBclContainers()
    {
        var reachable = BoxingReach("""
            using System.Collections.Generic;
            namespace Lib;
            public struct Point { public int X; }
            public struct Payload { public int V; }
            public static class Bags
            {
                public static object Points() { return new List<Point>(); }
                public static object Map() { return new Dictionary<int, Payload>(); }
                public static object Nested() { return new Dictionary<string, List<Point?>>(); }
            }
            """);

        // List<Point> / Dictionary<int,Payload> / 嵌套 List<Point?>（Nullable 解包）
        // 经「任意可达类型的类型实参传播」全部暴露；基元 int/string 不入集合。
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "Lib.Point", "Lib.Payload",
        }, reachable);
    }

    [Fact]
    public void BoxingReach_SourceMemberGraph_PropagatesFieldsAndProperties()
    {
        var reachable = BoxingReach("""
            using System.Collections.Generic;
            namespace Lib;
            public struct Leaf { public int X; }
            public struct Other { public int X; }
            public class Middle
            {
                public Leaf Leaf;
                public List<Other> Others = new();
            }
            public class Bag
            {
                public Middle M = new();
                public Leaf[] Leaves = new Leaf[0];
                public int Tag;
            }
            public static class Helper
            {
                public static object GetBag() { return new Bag(); }
            }
            """);

        // 装箱的源内 Bag → 其实例成员（字段/属性）→ Middle/Leaf[] → Middle 成员图 →
        // Leaf、List<Other> 类型实参 → Other。BCL 成员图本身不被遍历（防爆炸）。
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "Lib.Leaf", "Lib.Other",
        }, reachable);
    }

    [Fact]
    public void BoxingReach_ArrayRoot_UnwrapsElementType()
    {
        var reachable = BoxingReach("""
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                public static object Get() { return new Point[2]; }
            }
            """);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "Lib.Point" }, reachable);
    }

    [Fact]
    public void BoxingReach_NonBoxedGenericContainerUse_DoesNotReach()
    {
        var reachable = BoxingReach("""
            using System.Collections.Generic;
            namespace Lib;
            public struct Point { public int X; }
            public static class Helper
            {
                private static readonly List<Point> Points = new();
                public static void Add(Point p) { Points.Add(p); }
                public static Point Get(int i) => Points[i];
            }
            """);

        // 无 object/dynamic/serializer sink：仅泛型容器使用不构成装箱根，
        // BCL 成员图不被遍历，集合必须为空（成员图不爆炸）。
        Assert.Empty(reachable);
    }

    [Fact]
    public void BoxingReach_ReachableStruct_IsTakenOverByC07_WithClonedBoxingSite()
    {
        // E13 因装箱可达而跳过只读化的残余可变 struct，C07 必须接管：
        // plan 激活、直接装箱站点插 Clone、产物可编译（设计 §1.3/§7.2）。
        var run = Analyze("""
            using System.Collections.Generic;
            namespace Lib;
            public struct Point
            {
                public int X;
                public void Bump() { X++; }
            }
            public static class Bags
            {
                public static object Snapshot(Point p)
                {
                    var map = new Dictionary<string, Point>();
                    object boxed = p;
                    return map;
                }
            }
            """);

        var plan = Plan(run, "Lib.Point");
        Assert.True(plan.IsActive);
        AssertOutputCompiles(run.Output);
        Assert.Contains("object boxed = p.Clone();", run.Output);
        Assert.Equal(1, run.Statistics.BoxingSites);
    }
}
