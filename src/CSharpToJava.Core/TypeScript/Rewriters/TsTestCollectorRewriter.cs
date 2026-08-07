using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 测试类元数据收集器（Task 39，spec §6.1）：扫描语法树中的 xUnit/MSTest 测试类
/// （含 [Fact]/[Theory]/[TestMethod]/[DataTestMethod] 方法的类），把类/方法元数据
/// （显示名、参数名、InlineData/DataRow TS 字面量）登记进
/// <see cref="ConversionContext.TsTestClasses"/>（键 "命名空间.类名"）。
/// 只收集不改写（返回原根）；单文件模式在 SingleFileTsEmitPass 1g 步运行，
/// 项目模式在 ProjectTsSyntaxRewritePass 预收集步运行（跨文件合并按类名累积）。
/// </summary>
public sealed class TsTestCollectorRewriter : CSharpSyntaxRewriter
{
    private static readonly HashSet<string> TestMethodAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "TestMethod", "DataTestMethod",
    };

    private static readonly HashSet<string> InlineDataAttributes = new(StringComparer.Ordinal)
    {
        "InlineData", "DataRow",
    };

    private readonly ConversionContext _context;

    public TsTestCollectorRewriter(ConversionContext context)
    {
        _context = context;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        foreach (var member in node.Members)
        {
            CollectNamespaceOrClass(member, "");
        }
        return node;
    }

    private void CollectNamespaceOrClass(MemberDeclarationSyntax member, string parentNamespace)
    {
        switch (member)
        {
            case FileScopedNamespaceDeclarationSyntax fileScoped:
                CollectMembers(fileScoped.Members, JoinNamespace(parentNamespace, fileScoped.Name.ToString()));
                break;
            case NamespaceDeclarationSyntax block:
                CollectMembers(block.Members, JoinNamespace(parentNamespace, block.Name.ToString()));
                break;
            case ClassDeclarationSyntax cls:
                CollectClass(cls, parentNamespace);
                break;
        }
    }

    private void CollectMembers(SyntaxList<MemberDeclarationSyntax> members, string ns)
    {
        foreach (var member in members)
        {
            if (member is ClassDeclarationSyntax cls)
            {
                CollectClass(cls, ns);
            }
            else if (member is BaseNamespaceDeclarationSyntax nestedNs)
            {
                CollectMembers(nestedNs.Members, JoinNamespace(ns, nestedNs.Name.ToString()));
            }
        }
    }

    private void CollectClass(ClassDeclarationSyntax cls, string ns)
    {
        var testMethods = cls.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(method => TryCollectMethod(method))
            .Where(info => info != null)
            .Select(info => info!)
            .ToList();
        if (testMethods.Count == 0)
        {
            return;
        }

        var key = Key(ns, cls.Identifier.Text);
        _context.TsTestClasses ??= new Dictionary<string, TsTestClassInfo>(StringComparer.Ordinal);
        if (_context.TsTestClasses.TryGetValue(key, out var existing))
        {
            // 项目模式：partial 测试类跨文件合并
            _context.TsTestClasses[key] = new TsTestClassInfo
            {
                ClassName = existing.ClassName,
                Methods = existing.Methods.Concat(testMethods).ToList(),
            };
            return;
        }
        _context.TsTestClasses[key] = new TsTestClassInfo
        {
            ClassName = cls.Identifier.Text,
            Methods = testMethods,
        };
    }

    private TsTestMethodInfo? TryCollectMethod(MethodDeclarationSyntax method)
    {
        var attributes = method.AttributeLists
            .SelectMany(list => list.Attributes)
            .ToList();
        var names = attributes
            .Select(attr => NormalizeAttributeName(attr))
            .ToHashSet(StringComparer.Ordinal);
        if (!names.Overlaps(TestMethodAttributes))
        {
            return null;
        }

        var isTheory = names.Contains("Theory") || names.Contains("DataTestMethod");
        var inlineData = attributes
            .Where(attr => InlineDataAttributes.Contains(NormalizeAttributeName(attr)))
            .Select(attr => CollectInlineRow(attr))
            .Where(row => row != null)
            .Select(row => row!)
            .ToList();
        var parameterNames = method.ParameterList.Parameters
            .Select(parameter => parameter.Identifier.Text)
            .ToList();

        return new TsTestMethodInfo
        {
            MethodName = method.Identifier.Text,
            IsTheory = isTheory,
            ParameterNames = parameterNames,
            InlineData = inlineData,
        };
    }

    /// <summary>特性名归一化：去命名空间限定与 Attribute 后缀（Xunit.Fact → Fact）。</summary>
    private static string NormalizeAttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString();
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }
        return name.EndsWith("Attribute", System.StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    /// <summary>把一条 InlineData/DataRow 参数行转为 TS 字面量文本列表；不支持形态取源文本。</summary>
    private static IReadOnlyList<string>? CollectInlineRow(AttributeSyntax attribute)
    {
        if (attribute.ArgumentList == null)
        {
            return null;
        }
        return attribute.ArgumentList.Arguments
            .Select(argument => LiteralToTs(argument.Expression))
            .ToList();
    }

    /// <summary>
    /// C# 属性参数表达式 → TS 字面量文本。支持：数值（去 C# 后缀 L/d/f/m/u）、
    /// 字符串（含 verbatim @"..."，"" → "）、字符、布尔、null、一元负号数值；
    /// 其余形态（枚举成员/typeof/常量引用）取源文本原样发射（对齐 Java 路径回退策略）。
    /// </summary>
    internal static string LiteralToTs(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return LiteralTokenToTs(literal);
            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression)
                && unary.Operand is LiteralExpressionSyntax numeric
                && numeric.IsKind(SyntaxKind.NumericLiteralExpression):
                return "-" + NormalizeNumericLiteral(numeric.Token.Text);
            default:
                return expression.ToString();
        }
    }

    private static string LiteralTokenToTs(LiteralExpressionSyntax literal)
    {
        switch (literal.Kind())
        {
            case SyntaxKind.NumericLiteralExpression:
                return NormalizeNumericLiteral(literal.Token.Text);
            case SyntaxKind.StringLiteralExpression:
                return StringLiteralToTs(literal.Token.Text);
            case SyntaxKind.CharacterLiteralExpression:
                // C# 字符 'x' → TS 单引号字符串（C# 转义序列与 TS 字符串一致）
                return literal.Token.Text;
            case SyntaxKind.TrueLiteralExpression:
                return "true";
            case SyntaxKind.FalseLiteralExpression:
                return "false";
            case SyntaxKind.NullLiteralExpression:
                return "null";
            default:
                return literal.Token.Text;
        }
    }

    /// <summary>数值字面量去 C# 后缀（100L/1.5f/1.5d/1m/100u → TS 合法数值）。</summary>
    private static string NormalizeNumericLiteral(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        var last = text[^1];
        if (last is 'L' or 'l' or 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'u' or 'U')
        {
            return text[..^1];
        }
        return text;
    }

    /// <summary>字符串字面量 → TS 双引号字符串：普通串原样；verbatim 去 @ 并转义 "" → "。</summary>
    private static string StringLiteralToTs(string text)
    {
        if (text.StartsWith("@", System.StringComparison.Ordinal))
        {
            var inner = text[1..];
            var body = inner[1..^1].Replace("\"\"", "\\\"");
            return $"\"{body}\"";
        }
        return text;
    }

    private static string JoinNamespace(string parent, string child) =>
        string.IsNullOrEmpty(parent) ? child : $"{parent}.{child}";

    private static string Key(string ns, string className) =>
        string.IsNullOrEmpty(ns) ? className : $"{ns}.{className}";
}
