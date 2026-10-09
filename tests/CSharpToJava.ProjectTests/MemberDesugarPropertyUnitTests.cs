using System.IO;
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C11 property 去糖 Core 单测矩阵（设计 §7.1）：内联源码 → CSharpCompilation →
/// RewriteFile → 忽略空白断言 + AssertOutputCompiles。
/// </summary>
public class MemberDesugarPropertyUnitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "md-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    /// <summary>残差闸门类测试用的语义解析入口（ExcludePlans 流程需要自持 model）。</summary>
    internal static SemanticModel ParseWithSemanticsPublic(string src)
        => ParseWithSemantics(src);

    internal static (string OutputCode, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diagnostics) Rewrite(
        string src, MemberDesugarOptions? options = null, params string[] extra)
    {
        var model = ParseWithSemantics(src, extra);
        var pp = new MemberDesugarPreprocessor(options ?? new MemberDesugarOptions());
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
    }

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

    internal static void AssertOutputCompiles(string output, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create("md-verify",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    // C06 回归：特性命名实参 (Mode = "z") 左侧标识符绑定到特性类自身被
    // 去糖的属性时，PropertyUseSiteRewriter 不得把它改写成 get_Mode()——
    // 那会把 NameEquals 的 Name 槽塞成 InvocationExpression，默认
    // VisitNameEquals 强转即抛 InvalidCastException。
    [Fact]
    public void PropertyUseSite_AttributeNamedArgument_IsNotRewrittenToGetter()
    {
        var src = """
            using System;

            [AttributeUsage(AttributeTargets.Method)]
            internal sealed class MarkerAttribute : Attribute
            {
                public string Mode { get; set; } = "x";
            }

            internal class C
            {
                [Marker(Mode = "z")]
                public void Work()
                {
                }
            }
            """;

        var rewritten = Rewrite(src);
        var output = rewritten.OutputCode;
        var diags = rewritten.Diagnostics;

        // 命名实参必须原样保留，绝不能出现 get_Mode() 形态的特性参数。
        Assert.Contains("Mode = \"z\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Mode = get_", output, StringComparison.Ordinal);

        // 特性类属性去糖后命名实参无合法落点：残差诊断必须上报（管线层
        // 据此拦截，绝不静默放行坏码），而不是 InvalidCastException 崩树。
        Assert.True(diags.Count > 0, "expected residual diagnostics for attribute property with named-argument use.");
    }

    // 语料回归（csharp-sdk json: WriteStack.cs:207 / ReadStack.cs:325，
    // 2026-10-09 member-desugar 全量画像）：链式 null 条件访问
    // `o.Info?.MemberInfo?.Name` 的中间 MemberBinding 是被规划属性时，旧实现
    // 只改写尾槽并对内层 ConditionalAccess 走 base，base 访问裸 binding 的
    // Name 槽时 VisitIdentifierName 返回 InvocationExpression，触发
    // InvalidCastException（回退轮重解析必崩，首轮还会留下
    // `?.JsonParameterInfo?.get_X()` 形态的坏码）。
    [Fact]
    public void ChainedNullConditional_IntermediateProperty_BothSlotsRewritten()
    {
        var src = """
            internal sealed class Leaf { public string Name { get; set; } = ""; }
            internal sealed class Middle { public Leaf MemberInfo { get; set; } }
            internal sealed class Outer
            {
                public Middle Info { get; set; }
                public string? Get(Outer o) => o.Info?.MemberInfo?.Name;
            }
            """;

        var (outCode, stats, diags) = Rewrite(src);

        AssertContains(outCode, "o.get_Info()?.get_MemberInfo()?.get_Name()");
        Assert.DoesNotContain("?.MemberInfo?.", outCode, StringComparison.Ordinal);
        Assert.True(stats.ReadSitesRewritten >= 3);
        Assert.True(diags.All(d => d.Severity != MemberDesugarSeverity.Error),
            "unexpected error diagnostics: " + string.Join(";", diags));
        AssertOutputCompiles(outCode);
    }

    // 尾槽是方法调用（a?.P?.M()）：中间属性槽同样必须提升，尾槽原样保留。
    [Fact]
    public void ChainedNullConditional_IntermediateProperty_TailMethodPreserved()
    {
        var src = """
            internal sealed class Middle { public int Value { get; set; } }
            internal sealed class Outer
            {
                public Middle Info { get; set; }
                public string? Get(Outer o) => o.Info?.ToString();
                public int? TailMethod(Outer o) => o.Info?.Value?.CompareTo(0);
            }
            """;

        var (outCode, _, _) = Rewrite(src);

        AssertContains(outCode, "o.get_Info()?.ToString()");
        AssertContains(outCode, "o.get_Info()?.get_Value()?.CompareTo(0)");
        AssertOutputCompiles(outCode);
    }

    // 旧路径回归：单段 a?.X（接收者/头部不是被规划属性）仍只改写尾槽。
    [Fact]
    public void NullConditional_FieldHead_PropertyTail_StillRewritesTail()
    {
        var src = """
            internal sealed class Leaf { public string Name { get; set; } = ""; }
            internal sealed class C
            {
                public Leaf Field;
                public string? Get() => Field?.Name;
            }
            """;

        var (outCode, _, _) = Rewrite(src);

        AssertContains(outCode, "Field?.get_Name()");
        AssertOutputCompiles(outCode);
    }

    // 崩溃现场形态：局部函数内、嵌套两段链式条件访问初始化局部变量。
    [Fact]
    public void ChainedNullConditional_InsideLocalFunction_RewritesAndCompiles()
    {
        var src = """
            using System;

            internal sealed class Leaf { public string Name { get; set; } = ""; }
            internal sealed class Middle { public Leaf MemberInfo { get; set; } }
            internal sealed class Outer
            {
                public Middle Info { get; set; }

                public string? Get(Outer o)
                {
                    Func<string?> local = () => o.Info?.MemberInfo?.Name;
                    return local();
                }
            }
            """;

        var (outCode, stats, _) = Rewrite(src);

        AssertContains(outCode, "o.get_Info()?.get_MemberInfo()?.get_Name()");
        Assert.True(stats.ReadSitesRewritten >= 3);
        AssertOutputCompiles(outCode);
    }

    // 语料回归（csharp-sdk json，2026-10-09 member-desugar 全量画像）：
    // PD-8 对象初始化器提升把 new 表达式离树克隆后整体 Visit，克隆内标识符
    // 触发 TryGetPlan 的 GetSymbolInfo 时抛 "Syntax node is not within syntax
    // tree"。构造实参/保留初始化项必须先在原树节点上完成 Visit 再组装。
    [Fact]
    public void InitializerLift_PropertyReadInCtorArgument_VisitedBeforeDetach()
    {
        var src = """
            internal sealed class D
            {
                public D(object tag) { }
                public int Y { get; set; }
            }

            internal sealed class C
            {
                public object Prop { get; set; }
                public void M()
                {
                    var d = new D(Prop) { Y = 1 };
                }
            }
            """;

        var (outCode, _, diags) = Rewrite(src);

        AssertContains(outCode, "new D(get_Prop())");
        AssertContains(outCode, "set_Y(1)");
        Assert.True(diags.All(d => d.Severity != MemberDesugarSeverity.Error),
            "unexpected error diagnostics: " + string.Join(";", diags));
        AssertOutputCompiles(outCode);
    }

    // 嵌套提升路径：外层属性赋值的右值是待提升 new，其构造实参引用被规划属性。
    [Fact]
    public void InitializerLift_NestedCreationPropertyInCtorArgument_VisitedBeforeDetach()
    {
        var src = """
            internal sealed class D
            {
                public D(object tag) { }
                public int Y { get; set; }
            }

            internal sealed class C
            {
                public object Prop { get; set; }
                public D D { get; set; }
                public void M()
                {
                    var x = new C { D = new D(Prop) { Y = 1 } };
                }
            }
            """;

        var (outCode, stats, _) = Rewrite(src);

        AssertContains(outCode, "new D(get_Prop())");
        Assert.True(stats.InitializersRewritten >= 2);
        AssertOutputCompiles(outCode);
    }

    private static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(),
            StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    private static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(),
            StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");
}
