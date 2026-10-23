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

    // 尾槽是方法调用（a?.P?.M()）：中间属性槽同样必须提升；尾槽为非空值类型
    // 属性时，get_Value() 返回不可空 struct，其后的 ?. 属于 CS0023 非法形态，
    // 必须折叠为普通点接（首个 ?. 已短路整条尾链，语义等价）。
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
        AssertContains(outCode, "o.get_Info()?.get_Value().CompareTo(0)");
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

    // 语料回归（csharp-sdk regular-expressions RegexNode.Ch）：数组初始化器内
    // 的属性读站点此前被 `node.Parent is not InitializerExpressionSyntax` 挡掉，
    // 声明已降形但 `new char[1] { Ch }` 残留 CS1061。
    [Fact]
    public void PropertyRead_InsideInitializer_IsRewritten()
    {
        var src = """
            internal sealed class C
            {
                public char Ch { get; set; }

                public char[] M()
                {
                    return new char[1] { Ch };
                }
            }
            """;

        var (outCode, _, _) = Rewrite(src);

        AssertContains(outCode, "new char[1] { get_Ch() }");
        AssertOutputCompiles(outCode);
    }

    // 语料回归（csharp-sdk regular-expressions RegexNode.Str）：`Str += cond ? a : b`
    // 的 RHS 条件表达式作为二元右操作数时必须补括号，否则重解析为
    // `(Str + cond) ? a : b`（CS0029）。
    [Fact]
    public void CompoundAssignment_ConditionalRhs_Parenthesized()
    {
        var src = """
            internal sealed class C
            {
                public string Str { get; set; } = "";

                public void M(bool cond, string other)
                {
                    Str += cond ? "x" : other;
                }
            }
            """;

        var (outCode, _, _) = Rewrite(src);

        AssertContains(outCode, "set_Str(get_Str() + (cond ? \"x\" : other))");
        AssertOutputCompiles(outCode);
    }

    // 语料回归（csharp-sdk regular-expressions Capture.Index）：访问器
    // `private protected set` 是复合可访问性，降形为方法时必须整体平移，
    // 只取首个 token 会误降为 private（派生类调用 CS0122）。
    [Fact]
    public void SetAccessor_PrivateProtected_PreservesCompoundAccessibility()
    {
        var src = """
            internal class B
            {
                public int Index { get; private protected set; }
            }

            internal sealed class D : B
            {
                internal void Set(int value)
                {
                    Index = value;
                }
            }
            """;

        var (outCode, _, _) = Rewrite(src);

        AssertContains(outCode, "private protected void set_Index(int value)");
        AssertOutputCompiles(outCode);
    }

    // CS0029 回归（regular-expressions Regex.CacheSize / MatchTimeout）：表达式体
    // 成员/lambda 的体直接是属性赋值时，赋值结果（= 赋入值）必须保留；setter 返回
    // void 前不能直接作返回值，块化为求值一次 + setter + return 临时。
    [Fact]
    public void ExpressionBodiedAssignment_PreservesAssignedValue()
    {
        var src = """
            using System;

            internal sealed class C
            {
                public int P { get; set; }

                internal object M() => P = 5;

                internal void L()
                {
                    Func<object> f = () => P = 3;
                    _ = f;
                }
            }
            """;

        var (outCode, stats, _) = Rewrite(src);
        var compact = Normalize(outCode);

        // 赋值结果作为返回值（方法 + lambda 各一），setter 各只调用一次。
        AssertContains(outCode, "var __cs2j_pval0 = 5; set_P(__cs2j_pval0); return __cs2j_pval0;");
        AssertContains(outCode, "var __cs2j_pval1 = 3; set_P(__cs2j_pval1); return __cs2j_pval1;");
        Assert.Equal(2, compact.Split("set_P(__cs2j_pval").Length - 1);
        Assert.Equal(2, compact.Split("return__cs2j_pval").Length - 1);
        Assert.True(stats.WriteSitesRewritten >= 2);
        AssertOutputCompiles(outCode);
    }

    // 回归（xml-tiny SchemaElementDecl.cs / SchemaAttDef.cs，2026-10-23）：属性首个
    // 修饰符 token 携带属性的前导 trivia（#endregion + 注释块）时，同一 token 被复制到
    // 该属性的每个生成方法（get_X/set_X 共用 property.Modifiers），#endregion 随之
    // 重复 → CS1038 → 本地解析校验失败 → 整文件回滚，继承属性使用点（attdef.Presence
    // → attdef.get_Presence()）改写一并丢失，最终阶段 '==' enum 操作数 fail-fast。
    [Fact]
    public void PropertyExpansion_LeadingTriviaBlock_IsNotDuplicatedOnEachAccessor()
    {
        var src = """
            internal enum Use
            {
                None,
                Required,
            }

            internal class Base
            {
                private Use _presence;

                #region PresenceRegion
                internal Use Marker { get; set; }
                #endregion

                internal Use Presence
                {
                    get { return _presence; }
                    set { _presence = value; }
                }
            }

            internal class Derived
            {
                internal bool Check(Base b) => b.Presence == Use.Required;
            }
            """;

        var (outCode, _, _) = Rewrite(src);

        // #region/#endregion 指令必须保持 1:1，绝不能随修饰符 token 复制到每个访问器方法。
        Assert.Equal(
            CountOccurrences(src, "#endregion"),
            CountOccurrences(outCode, "#endregion"));
        AssertOutputCompiles(outCode);

        // 声明展开后继承属性使用点必须同步改写为 getter 调用。
        AssertContains(outCode, "b.get_Presence() == Use.Required");
        AssertNotContains(outCode, "b.Presence");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
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
