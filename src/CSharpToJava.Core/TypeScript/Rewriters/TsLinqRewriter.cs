using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// LINQ 目标分叉 rewriter（Task 36，spec §2.4 / §4.3.8）：TS 目标不把 LINQ 链改写为
/// 过程化循环（Java 路径的 LinqRewriter），而是改写到 TS 策略：
/// - 简单链（仅 Where/Select 且 lambda 无副作用风险）→ 数组方法链
///   （xs.Where(p).Select(q) → xs.filter(p).map(q)），积极求值，时序差异记入限制
///   （spec §1.4.6）；Where/Select 带索引 lambda 直接复用 JS 回调的 (元素, 索引)。
/// - 含复杂算子（OrderBy/GroupBy/Count/Sum/First/... 或需惰性保证）→ 整链
///   Enumerable.xxx 惰性链（Enumerable.where(xs, p) 等），compat 符号 Enumerable
///   登记到 context.TsCompatSymbols（发射器自动生成 compat 导入）。
/// - 未支持算子（ElementAt/Except/Join 等）→ 整链不改写 + 一次性警告诊断
///   （共享管线会按 Java Stream 形态转换，产物不可编译，由诊断提示）。
/// 链判定：以最外层 LINQ 调用为根向下收集链步骤；同一链所有步骤共享一个决策
/// （简单则全 filter/map，复杂则全 Enumerable），保证惰性语义一致。
/// </summary>
public sealed class TsLinqRewriter : CSharpSyntaxRewriter
{
    private static readonly string[] ComplexMethods =
    {
        "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "GroupBy", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last",
        "LastOrDefault", "Any", "All", "Count", "Sum", "Average", "Min", "Max",
        "ToList", "ToArray", "ToDictionary", "Aggregate", "Zip", "Skip", "Take",
        "Distinct", "Contains",
    };

    private static readonly Dictionary<string, string> SimpleMethods = new()
    {
        { "Where", "filter" },
        { "Select", "map" },
    };

    // Where/Select 同时收录（复杂链中它们也走 Enumerable.where/select）。
    private static readonly Dictionary<string, string> ComplexCamel = ComplexMethods.Concat(SimpleMethods.Keys)
        .ToDictionary(m => m, m => char.ToLowerInvariant(m[0]) + m[1..], System.StringComparer.Ordinal);

    private readonly SemanticModel _semantic;
    private readonly ConversionContext? _context;
    private readonly Dictionary<InvocationExpressionSyntax, string> _decisions = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _warnedMethods = new(System.StringComparer.Ordinal);
    private bool _collected;

    public TsLinqRewriter(SemanticModel semantic, ConversionContext? context = null)
    {
        _semantic = semantic;
        _context = context;
    }

    /// <summary>已改写（简单或复杂）的链数。</summary>
    public int RewrittenChains { get; private set; }

    /// <summary>已改写的链内调用数。</summary>
    public int RewrittenCalls { get; private set; }

    /// <summary>未支持算子导致的整链跳过（诊断消息由 rewriter 直接发出）。</summary>
    public List<string> SkippedLinqChains { get; } = new();

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is CompilationUnitSyntax root && !_collected)
        {
            _collected = true;
            CollectChains(root);
        }
        return base.Visit(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 以原节点引用查决策：base.Visit 子节点改写后本节点可能被 Update 重建为脱离树的
        // 节点，但传入本方法的节点始终是 CollectChains 收集到的原实例。
        var original = node;
        node = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (node.Expression is not MemberAccessExpressionSyntax ma)
        {
            return node;
        }
        if (!_decisions.TryGetValue(original, out var decision))
        {
            return node;
        }

        if (decision.StartsWith("simple:", System.StringComparison.Ordinal))
        {
            // xs.Where(p) → xs.filter(p)、xs.Select(q) → xs.map(q)（接收者已由子节点改写）
            var jsName = decision["simple:".Length..];
            return node.WithExpression(ma.WithName(SyntaxFactory.IdentifierName(jsName)));
        }

        // 复杂链：Enumerable.<camel>(receiver, args...)
        RegisterCompat();
        var camel = decision["complex:".Length..];
        var args = new List<ArgumentSyntax> { SyntaxFactory.Argument(ma.Expression) };
        args.AddRange(node.ArgumentList.Arguments);
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.ParseExpression("Enumerable." + camel),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));
    }

    // ─── 链收集（pass 1） ─────────────────────────────────────────────

    private void CollectChains(CompilationUnitSyntax root)
    {
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (GetLinqMethod(inv) == null || IsNestedLinqInvocation(inv))
            {
                continue;
            }

            var steps = new List<InvocationExpressionSyntax>();
            var current = inv;
            while (true)
            {
                var method = GetLinqMethod(current);
                if (method == null) break;
                steps.Add(current);
                if (current.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression is InvocationExpressionSyntax inner
                    && GetLinqMethod(inner) != null)
                {
                    current = inner;
                    continue;
                }
                break;
            }
            if (steps.Count == 0)
            {
                continue;
            }

            // 未支持算子：整链跳过 + 一次性警告（共享管线会 Java 化，产物不可编译）
            var unsupported = steps.Select(GetLinqMethod!)
                .FirstOrDefault(m => !SimpleMethods.ContainsKey(m.Name) && !ComplexCamel.ContainsKey(m.Name));
            if (unsupported != null)
            {
                WarnUnsupported(inv, unsupported.Name);
                continue;
            }

            var simple = steps.All(s => SimpleMethods.ContainsKey(GetLinqMethod(s)!.Name))
                && steps.All(IsSideEffectFreeChainStep);
            foreach (var step in steps)
            {
                var methodName = GetLinqMethod(step)!.Name;
                var decision = simple
                    ? "simple:" + SimpleMethods[methodName]
                    : "complex:" + ComplexCamel[methodName];
                _decisions[step] = decision;
                RewrittenCalls++;
            }
            RewrittenChains++;
        }
    }

    /// <summary>该调用是否嵌套在另一 LINQ 链调用内部（由其链根统一决策）。</summary>
    private bool IsNestedLinqInvocation(InvocationExpressionSyntax node)
    {
        return node.Parent is MemberAccessExpressionSyntax ma
            && ma.Parent is InvocationExpressionSyntax outer
            && ma.Expression == node
            && GetLinqMethod(outer) != null;
    }

    /// <summary>简单链要求：lambda 无副作用（无调用/赋值/自增自减/对象创建/await/throw）。</summary>
    private static bool IsSideEffectFreeChainStep(InvocationExpressionSyntax node)
    {
        foreach (var argument in node.ArgumentList.Arguments)
        {
            if (argument.Expression is not LambdaExpressionSyntax lambda)
            {
                // 非 lambda 实参（方法组/索引访问等）无法确认无副作用 → 保守走惰性链
                return false;
            }
            if (LambdaHasSideEffects(lambda))
            {
                return false;
            }
        }
        return true;
    }

    private static bool LambdaHasSideEffects(LambdaExpressionSyntax lambda)
    {
        return lambda.Body.DescendantNodesAndSelf().Any(n =>
            n is InvocationExpressionSyntax
            or AssignmentExpressionSyntax
            or PrefixUnaryExpressionSyntax
            or PostfixUnaryExpressionSyntax
            or ObjectCreationExpressionSyntax
            or AwaitExpressionSyntax
            or ThrowExpressionSyntax);
    }

    private IMethodSymbol? GetLinqMethod(InvocationExpressionSyntax node)
    {
        var info = _semantic.GetSymbolInfo(node);
        var method = info.Symbol as IMethodSymbol;
        if (method == null && info.CandidateSymbols.Length > 0)
        {
            method = info.CandidateSymbols[0] as IMethodSymbol;
        }
        if (method == null || !method.IsExtensionMethod)
        {
            return null;
        }
        return method.ContainingType.ToDisplayString() == "System.Linq.Enumerable" ? method : null;
    }

    private void WarnUnsupported(InvocationExpressionSyntax node, string methodName)
    {
        if (_warnedMethods.Add(methodName))
        {
            _context?.Diagnostics.Warning(
                $"LINQ method '{methodName}' is not supported by the TypeScript target yet (Task 36): output may not compile");
        }
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        SkippedLinqChains.Add($"Line {line}: unsupported LINQ method '{methodName}'");
    }

    private void RegisterCompat()
    {
        _context?.TsCompatSymbols.Add("Enumerable");
    }
}
