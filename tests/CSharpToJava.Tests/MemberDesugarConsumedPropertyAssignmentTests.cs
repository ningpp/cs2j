using CSharpToJava.CLI;
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// PD-6 值消费回归：属性简单赋值的值被外层表达式消费时（if 条件比较、方法实参、
/// 局部初始化、return、一元操作数、字符串插值等），setter 降形产物（void）不得
/// 占据值位（真实链 XPathDocumentBuilder.cs 第 873 行 CS0019）。正确形态：
/// rhs 先存临时（单次求值），setter 调用提升为宿主语句之前的语句，值位改用临时。
/// 复刻 CLI 适配器纪律：整树绑定一次，逐文件在原始编译上 RewriteFile。
/// </summary>
public class MemberDesugarConsumedPropertyAssignmentTests
{
    private const string Source = @"
class PageInfo
{
    public int NodeCount { get; set; }
}

class C
{
    private PageInfo _p = new PageInfo();
    private int[] _page = new int[16];

    private void Sink(int value) { }
    private int Src() => 1;

    int IfConditionCompare()
    {
        if ((_p.NodeCount = _p.NodeCount + 1) >= _page.Length)
        {
            return 1;
        }
        return 0;
    }

    void MethodArgument()
    {
        Sink(_p.NodeCount = Src());
    }

    int LocalInitializer()
    {
        int slots = (_p.NodeCount = Src()) + 1;
        return slots;
    }

    int ReturnConsumed()
    {
        return (_p.NodeCount = _p.NodeCount + 1);
    }

    int ReturnNested()
    {
        return (_p.NodeCount = Src()) + 100;
    }

    int UnaryOperand()
    {
        return -(_p.NodeCount = Src());
    }

    string Interpolation()
    {
        return $""{(_p.NodeCount = Src())}"";
    }

    void ConsumedInIfBody(bool flag)
    {
        if (flag)
            Sink(_p.NodeCount = Src());
    }

    void ChainedStillWorks(C other)
    {
        other._p.NodeCount = _p.NodeCount = Src();
    }

    void StatementPositionUnchanged()
    {
        _p.NodeCount = Src();
    }
}";

    [Fact]
    public void IfConditionComparison_IsHoistedBeforeIf()
    {
        var code = Rewrite();

        // void setter 调用不得充当比较操作数。
        Assert.DoesNotContain("set_NodeCount() + 1) >= _page.Length", code, StringComparison.Ordinal);
        Assert.Contains("set_NodeCount(__cs2j_pval", code, StringComparison.Ordinal);

        // 提升顺序：临时声明 → setter 调用 → 消费临时的 if（单次求值 + 先 setter 后用值）。
        var tempDecl = code.IndexOf("var __cs2j_pval", StringComparison.Ordinal);
        var setterCall = code.IndexOf("set_NodeCount(__cs2j_pval", StringComparison.Ordinal);
        Assert.True(tempDecl >= 0 && setterCall > tempDecl, "临时声明必须先于 setter 调用");

        // RHS 中的属性读同步降形且只求值一次：临时声明初始化器是 get_NodeCount()
        // 的二元加法（提升后 if 条件直接消费临时，不再重复读属性）。
        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        var condition = root.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Select(i => i.Condition)
            .FirstOrDefault(c => c.ToString().Contains("_page.Length"));
        Assert.NotNull(condition);
        var binary = Assert.IsType<BinaryExpressionSyntax>(Unwrap(condition));
        var operand = Assert.IsType<IdentifierNameSyntax>(Unwrap(binary.Left));
        Assert.StartsWith("__cs2j_pval", operand.Identifier.ValueText, StringComparison.Ordinal);
        var declarator = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == operand.Identifier.ValueText);
        var initializer = Assert.IsType<BinaryExpressionSyntax>(
            Unwrap(declarator.Initializer!.Value));
        Assert.Contains(initializer.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => MethodName(invocation) == "get_NodeCount");
    }

    [Fact]
    public void AllConsumedContexts_SetterCallsAreStatements()
    {
        var code = Rewrite();

        // 全量结构断言：所有 set_ 调用必须直接充当表达式语句（void 值位即非法）。
        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        var offenders = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => SetterName(i).StartsWith("set_", StringComparison.Ordinal))
            .Where(i => i.Parent is not ExpressionStatementSyntax)
            .ToList();
        Assert.True(offenders.Count == 0,
            "setter 调用出现在值位：" + string.Join("; ", offenders.Select(o => o.ToString())));

        // 各消费上下文均完成降形（临时 + setter）。
        Assert.Contains("set_NodeCount(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnConsumed_ReturnsTempValue()
    {
        var code = Rewrite();

        // return 的值是赋入值：`return __cs2j_pvalN;`，setter 已先行调用。
        Assert.Contains("return __cs2j_pval", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return _p.set_NodeCount(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return set_NodeCount(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalInitializer_UsesHoistedTemp()
    {
        var code = Rewrite();

        // 局部初始化位不得直接内联 setter 调用。
        Assert.DoesNotContain("slots = _p.set_NodeCount(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("slots = set_NodeCount(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StatementPosition_PlainSetterCallKept()
    {
        var code = Rewrite();

        // 语句位常规路径不受影响：`_p.set_NodeCount(Src());`。
        Assert.Contains("_p.set_NodeCount(Src())", code, StringComparison.Ordinal);

        // 链式赋值路径不受泛化影响：双 setter、无嵌套 void 实参。
        Assert.DoesNotContain("set_NodeCount(set_", code, StringComparison.Ordinal);
        Assert.DoesNotContain("set_NodeCount(_p.set_", code, StringComparison.Ordinal);
    }

    private static string MethodName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            _ => string.Empty,
        };

    private static string SetterName(InvocationExpressionSyntax invocation)
        => MethodName(invocation);

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private static string Rewrite()
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            Source, new CSharpParseOptions(LanguageVersion.Latest), path: "ConsumedAssign.cs");
        var compilation = CSharpCompilation.Create(
            "consumed-assign-tests",
            new[] { tree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor(new MemberDesugarOptions());
        preprocessor.BindCompilation(compilation);
        var statistics = new MemberDesugarStatistics();
        var diagnostics = new System.Collections.Generic.List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            tree.ToString(), compilation.GetSemanticModel(tree), statistics, diagnostics);

        Assert.True(result.Changed, "属性未被展开：" + string.Join("; ", diagnostics));
        return result.OutputCode;
    }
}
