using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// BCL 成员映射 rewriter（Task 36，spec §3.1.2）：发射器实际消费 MemberMappings.json。
/// 在 C# 语法树 + Roslyn 语义模型上按 C# 全名（System.String 等，带 1 泛型 arity）+ 成员名
/// 查 TsMemberMappingTable，命中后在共享 visitor 之前改写到 TS 形态（避免 Java 化命名）：
/// - tsMethod 含 '.'（如 CsString.split / Math.trunc / console.log）→ 静态形态
///   	sMethod(receiver?, args...)：实例调用接收者前移为首参（arity 感知分解，spec §3.1.2
///   配置表 signature=static 语义）；imports 含 @csharptotypescript/compat 时登记符号
///   （CsString 等）到 context.TsCompatSymbols（发射器据此自动生成 compat 导入）。
/// - 纯改名（如 ToUpper → toUpperCase、List.Add → push）→ 替换成员名标识符（实例调用）。
/// - 属性访问（signature "property"，如 String.Length → length）→ 替换成员名；
///   属性也可配置为静态形态（如 Dictionary.Count → CsDictionary.count(dict)）。
/// 仅改写在引用程序集（BCL）上解析成功的成员调用，用户类型不受影响。
/// </summary>
public sealed class TsMemberMappingRewriter : CSharpSyntaxRewriter
{
    private const string CompatPackage = "@csharptotypescript/compat";

    private readonly SemanticModel _semantic;
    private readonly ConversionContext? _context;
    private readonly TsMemberMappingTable _mappings;

    public TsMemberMappingRewriter(SemanticModel semantic, TsMemberMappingTable mappings, ConversionContext? context = null)
    {
        _semantic = semantic;
        _mappings = mappings;
        _context = context;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 语义解析必须基于原始节点：base 访问会改写子节点并重写其父指针，改写后的
        // 子节点已脱离原树，此时 GetSymbolInfo 会抛 "Syntax node is not within syntax tree"
        // （Task 36 探针实证：words.GroupBy(w => w.Length) 中 w.Length 被改名后，
        // 对 words.GroupBy 的 GetSymbolInfo 即抛出）。
        IMethodSymbol? method = null;
        string? fullName = null;
        if (node.Expression is MemberAccessExpressionSyntax originalMa)
        {
            var info = _semantic.GetSymbolInfo(originalMa);
            if (info.Symbol is IMethodSymbol m && IsReferenceAssemblySymbol(m))
            {
                method = m;
                fullName = GetCSharpFullName(m.ContainingType);
            }
        }

        node = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        if (method == null || fullName == null || node.Expression is not MemberAccessExpressionSyntax ma)
        {
            return node;
        }

        if (!_mappings.TryMapMethod(fullName, method.Name, out var tsMethod, out var imports))
        {
            return node;
        }

        if (tsMethod.IndexOf('.') >= 0)
        {
            // 静态形态：tsMethod(receiver?, args...)。实例调用（s.Split(...)）接收者前移
            // 为首参（CsString.split(s, ...)）；静态调用（string.Join(...)）参数原样。
            // 接收者/参数取 base 访问后的节点（接收者本身可能已被嵌套改写）。
            var args = new List<ArgumentSyntax>();
            if (!method.IsStatic)
            {
                args.Add(SyntaxFactory.Argument(ma.Expression));
            }
            args.AddRange(node.ArgumentList.Arguments);
            RegisterCompatImports(tsMethod, imports);
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.ParseExpression(tsMethod),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));
        }

        // 纯改名（实例成员：List.Add → push、ToUpper → toUpperCase）
        return node.WithExpression(ma.WithName(SyntaxFactory.IdentifierName(tsMethod)));
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // Method-call form (s.Split(...)) is handled uniformly by VisitInvocationExpression;
        // here we only handle pure property accesses (s.Length / dict.Count / g.Key).
        if (node.Parent is InvocationExpressionSyntax inv && inv.Expression == node)
        {
            return base.VisitMemberAccessExpression(node);
        }

        // Semantic resolution must be based on the original node: base.Visit rewrites child
        // nodes (nested BCL call mappings, e.g. s.Split(' ') -> CsString.split(s, ' ')) and
        // the rewritten children are detached from the original tree.
        var propSymbol = _semantic.GetSymbolInfo(node).Symbol;
        var property = propSymbol as IPropertySymbol;
        var isMapped = property != null && IsReferenceAssemblySymbol(property);
        var fullName = isMapped ? GetCSharpFullName(property!.ContainingType) : null;

        // Visit children FIRST (rewriting nested calls/properties), then apply the property
        // mapping on the rebuilt node; otherwise nested rewrites of node.Expression are
        // dropped by WithName (Task 36 probe: return s.Split(' ').Length lost the Split
        // mapping).
        node = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        if (!isMapped || fullName == null)
        {
            return node;
        }

        // Properties may be configured as static forms (signature "static"): e.g.
        // Dictionary.Count -> CsDictionary.count(dict), so the shared pipeline does not
        // Java-ize the BCL property into size().
        if (_mappings.TryMapMethod(fullName, property!.Name, out var tsMethod, out var imports)
            && tsMethod.IndexOf('.') >= 0)
        {
            RegisterCompatImports(tsMethod, imports);
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.ParseExpression(tsMethod),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(node.Expression))));
        }

        // Property rename (signature "property"): String.Length -> length (unmapped
        // property names pass through the shared visitor's Property fallback as-is).
        if (_mappings.TryMapMember(fullName, property!.Name, out var tsMember))
        {
            return node.WithName(SyntaxFactory.IdentifierName(tsMember));
        }

        return node;
    }

    /// <summary>登记 tsMethod 首段符号（CsString 等）到 compat 符号集（imports 含 compat 包时）。</summary>
    private void RegisterCompatImports(string tsMethod, IReadOnlyList<string> imports)
    {
        if (_context == null || imports.Count == 0) return;
        var hasCompat = false;
        foreach (var import in imports)
        {
            if (import == CompatPackage) { hasCompat = true; break; }
        }
        if (!hasCompat) return;
        var dot = tsMethod.IndexOf('.');
        if (dot <= 0) return;
        _context.TsCompatSymbols.Add(tsMethod[..dot]);
    }

    /// <summary>仅改写在引用程序集（BCL）解析成功的成员；同编译程序集的用户类型不动。</summary>
    private bool IsReferenceAssemblySymbol(ISymbol symbol)
    {
        var containing = symbol.ContainingAssembly;
        var compilationAssembly = _semantic.Compilation.Assembly;
        return containing != null
            && compilationAssembly != null
            && !SymbolEqualityComparer.Default.Equals(containing, compilationAssembly);
    }

    /// <summary>C# 全名：System.String / System.Collections.Generic.List1（泛型带 arity）。</summary>
    private static string GetCSharpFullName(INamedTypeSymbol type)
    {
        var original = type.OriginalDefinition;
        var ns = original.ContainingNamespace;
        var nsName = ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() : string.Empty;
        return nsName.Length == 0 ? original.MetadataName : nsName + "." + original.MetadataName;
    }
}
