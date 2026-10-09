using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// M4 受限局部判定（C04 §9 M4 / §4.6）。Roslyn 实测规则矩阵（net10）：
/// <list type="bullet">
/// <item>ref struct（Span 族）/ref local/指针局部只要不跨 yield 挂起点即合法
/// （CS4007/CS9217 是 liveness 判定，不是作用域判定：循环体内每轮新建、
/// 挂起前已死的局部都允许）；</item>
/// <item>一旦局部在某次挂起之后仍可能存活，即 CS4007/CS9217，镜像为 CS2J0403。</item>
/// </list>
/// 状态机类是普通 class，受限局部不能提升为字段；不跨边界的局部必须保留为
/// MoveNext 的真局部，跨边界的成员由 analyzer 拒绝。
/// </summary>
internal static class IteratorLocalEscaping
{
    public enum RestrictedKind
    {
        RefStruct,
        RefLocal,
        Pointer,
    }

    public readonly record struct RestrictedLocal(
        VariableDeclaratorSyntax Declarator,
        ILocalSymbol Symbol,
        RestrictedKind Kind);

    public static bool IsRestricted(ILocalSymbol local)
        => local.Type.IsRefLikeType
            || local.RefKind != RefKind.None
            || local.Type.TypeKind == TypeKind.Pointer;

    public static RestrictedKind Classify(ILocalSymbol local)
    {
        if (local.Type.TypeKind == TypeKind.Pointer) return RestrictedKind.Pointer;
        if (local.RefKind != RefKind.None) return RestrictedKind.RefLocal;
        return RestrictedKind.RefStruct;
    }

    /// <summary>
    /// 枚举方法体内声明在普通局部语句/for 头/fixed 语句中的受限局部。
    /// using 语句与 using 声明的资源由 IteratorResourceNormalizer 与 analyzer 的
    /// 专用检查负责，foreach 迭代变量由枚举器检查负责，均不在此列。
    /// </summary>
    public static IReadOnlyList<RestrictedLocal> FindRestrictedLocals(
        MethodDeclarationSyntax method,
        SemanticModel model)
    {
        var result = new List<RestrictedLocal>();
        foreach (var declarator in method.Body!.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>())
        {
            var belongs = declarator.Parent switch
            {
                VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax localDecl }
                    when localDecl.UsingKeyword.RawKind == 0 => true,
                VariableDeclarationSyntax { Parent: ForStatementSyntax } => true,
                VariableDeclarationSyntax { Parent: FixedStatementSyntax } => true,
                _ => false,
            };
            if (!belongs) continue;
            if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol local) continue;
            if (IsRestricted(local))
            {
                result.Add(new RestrictedLocal(declarator, local, Classify(local)));
            }
        }
        return result;
    }

    /// <summary>
    /// 保守词法活性判定：受限局部是否在任一 yield 挂起后仍可能存活。
    /// 保守指偏向判"跨"（拒绝罕见合法形态优于放行非法形态）；非严格模式下
    /// CS2J0403 仅为 Warning，误伤可降级。
    /// </summary>
    public static bool EscapesAcrossYield(
        RestrictedLocal restricted,
        MethodDeclarationSyntax method,
        SemanticModel model)
    {
        var decl = restricted.Declarator;
        var yields = method.Body!.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Where(y => y.IsKind(YieldReturnStatement) || y.IsKind(YieldBreakStatement))
            .OrderBy(y => y.SpanStart)
            .ToList();
        if (yields.Count == 0) return false;

        var references = method.Body.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => model.GetSymbolInfo(id).Symbol is ILocalSymbol referenced
                && SymbolEqualityComparer.Default.Equals(referenced, restricted.Symbol))
            .Select(id => id.Span)
            .ToList();

        // 条件 1：声明之后存在 yield，且局部在该 yield 词法之后仍被引用
        // （yield 表达式自身内的读取发生在挂起求值前，不算跨；必须严格在其后）。
        foreach (var yield in yields)
        {
            if (yield.SpanStart <= decl.SpanStart) continue;
            if (references.Any(r => r.Start >= yield.Span.End)) return true;
        }

        // 条件 2：局部声明在某个含 yield 循环的循环体之外（含 for 头变量），
        // 却在该循环内被引用——跨迭代存活（声明在循环体内则每轮重建，合法）。
        foreach (var loop in EnumerateLoops(method.Body))
        {
            if (!loop.Node.DescendantNodes().OfType<YieldStatementSyntax>().Any()) continue;
            var bodySpan = loop.Body.Span;
            var declaredInsideBody = decl.SpanStart >= bodySpan.Start && decl.Span.End <= bodySpan.End;
            if (declaredInsideBody) continue;
            if (references.Any(r => r.Start >= loop.Node.SpanStart && r.End <= loop.Node.Span.End))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(StatementSyntax Node, StatementSyntax Body)> EnumerateLoops(
        BlockSyntax body)
    {
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case ForEachStatementSyntax fe:
                    yield return (fe, fe.Statement);
                    break;
                case ForEachVariableStatementSyntax fev:
                    yield return (fev, fev.Statement);
                    break;
                case ForStatementSyntax forStmt:
                    yield return (forStmt, forStmt.Statement);
                    break;
                case WhileStatementSyntax whileStmt:
                    yield return (whileStmt, whileStmt.Statement);
                    break;
                case DoStatementSyntax doStmt:
                    yield return (doStmt, doStmt.Statement);
                    break;
            }
        }
    }
}
