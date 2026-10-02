using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R9：ref struct <c>using</c> 站点归一/拒绝（2026-09-29 裁决，设计 D9、
/// §4 组件表）。拥有全部 ref struct using 站点（using declaration 与
/// using block 两形态）：
/// <list type="bullet">
/// <item>白名单 <c>Span&lt;T&gt;</c>/<c>ReadOnlySpan&lt;T&gt;</c>：v1
/// <b>空操作归一</b>——剥除 using、局部保留；Span 无资源释放语义，故
/// 不产生 Dispose 调用；</item>
/// <item>非白名单 ref struct 的 using → UF055 Error；</item>
/// <item><c>await using</c> 不在本规则（归 C38：不重写不诊断）；
/// 引用类型/普通 struct 的常规 using 归 C44（不重写不诊断）；
/// yield+using 归 C04。</item>
/// </list>
/// 本规则是 pass 基座（随 pass 生效，不挂里程碑开关）；判定全部基于
/// SemanticModel/ISymbol。剥除节点带注释，二次跑不再被识别（幂等）。
/// </summary>
internal sealed class RefStructUsingRewriter : CSharpSyntaxRewriter
{
    public static readonly SyntaxAnnotation UsingReplacedAnnotation =
        new("c06-refstruct-using-replaced");

    private readonly SemanticModel _model;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    public RefStructUsingRewriter(
        SemanticModel model,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver)
    {
        _model = model;
        _statistics = statistics;
        _resolver = resolver;
    }

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    // ---- using declaration：using Span<T> x = ...; ----

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (!node.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) return node;
        if (node.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)) return node;

        var localType = TryGetDeclaredLocalType(node.Declaration.Variables[0]);
        if (localType == null) return node;

        // 非 ref-like 的常规 using 归 C44，不越界。
        if (!localType.IsRefLikeType) return node;

        if (StackAllocElementSafety.IsSpanLike(localType, out _))
        {
            var stripped = StripUsingKeyword(node)
                .WithAdditionalAnnotations(UsingReplacedAnnotation);
            _statistics.SpanUsingNoOps++;
            return stripped;
        }

        AddError(
            $"'using' on non-whitelisted ref struct '{localType.Name}': only System.Span<T> and " +
            "System.ReadOnlySpan<T> using sites receive the no-op normalization (they own no resources " +
            "to release). Other ref structs may have custom disposal semantics that Java cannot model; " +
            "rewrite the using site manually.",
            node);
        return node;
    }

    // ---- using block：using (Span<T> x = ...) { ... } ----

    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        if (node.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)) return node;

        ITypeSymbol? resourceType = null;
        ExpressionSyntax? originalExpression = null;

        if (node.Declaration != null)
        {
            resourceType = TryGetDeclaredLocalType(node.Declaration.Variables[0]);
        }
        else if (node.Expression != null)
        {
            originalExpression = node.Expression;
            var original = _resolver.Resolve(node.Expression);
            resourceType = original == null ? null : _model.GetTypeInfo(original).Type;
        }

        if (resourceType == null || !resourceType.IsRefLikeType)
        {
            // 常规 IDisposable using（C44）或不可证站点：不越界。
            return base.VisitUsingStatement(node);
        }

        if (!StackAllocElementSafety.IsSpanLike(resourceType, out _))
        {
            AddError(
                $"'using' block on non-whitelisted ref struct '{resourceType.Name}': only " +
                "System.Span<T>/System.ReadOnlySpan<T> using sites receive the no-op normalization. " +
                "Rewrite the using site manually.",
                node);
            return node;
        }

        _statistics.SpanUsingNoOps++;

        // 空操作归一：剥除 using，把声明（及表达式站点，保留副作用）与
        // 原块体语句放进普通 Block；不发任何 Dispose 调用。
        var statements = new List<StatementSyntax>();
        if (node.Declaration != null)
        {
            statements.Add(BuildPlainLocal(node));
        }
        else if (originalExpression != null)
        {
            statements.Add(ExpressionStatement(originalExpression.WithoutTrivia())
                .WithAdditionalAnnotations(UsingReplacedAnnotation));
        }

        var body = (StatementSyntax?)base.Visit(node.Statement);
        if (body is BlockSyntax bodyBlock)
        {
            var merged = bodyBlock.WithStatements(
                bodyBlock.Statements.InsertRange(0, statements));
            return merged.WithLeadingTrivia(node.GetLeadingTrivia())
                .WithAdditionalAnnotations(UsingReplacedAnnotation);
        }

        statements.Add(body ?? EmptyBlock());
        return Block(statements)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithAdditionalAnnotations(UsingReplacedAnnotation);
    }

    /// <summary>
    /// 剥除 using keyword 并把其 trivia（缩进/空格）整体移到声明语句前导，
    /// 避免丢排版。
    /// </summary>
    private LocalDeclarationStatementSyntax StripUsingKeyword(LocalDeclarationStatementSyntax node)
    {
        var movedTrivia = node.UsingKeyword.LeadingTrivia
            .Concat(node.UsingKeyword.TrailingTrivia);

        var stripped = node.WithUsingKeyword(default);
        return stripped.WithLeadingTrivia(movedTrivia.Concat(stripped.GetLeadingTrivia()));
    }

    /// <summary>using block 的声明形态 → 无 using 的局部声明（trivia 平移）。</summary>
    private LocalDeclarationStatementSyntax BuildPlainLocal(UsingStatementSyntax node)
    {
        var movedTrivia = node.UsingKeyword.LeadingTrivia
            .Concat(node.UsingKeyword.TrailingTrivia);

        var plain = LocalDeclarationStatement(
            default, default, default, node.Declaration!, Token(SyntaxKind.SemicolonToken));
        return plain.WithLeadingTrivia(movedTrivia.Concat(plain.GetLeadingTrivia()))
            .WithAdditionalAnnotations(UsingReplacedAnnotation);
    }

    /// <summary>
    /// 取 using 局部变量的声明类型符号（经 resolver 映射回原始树）；
    /// 含 <c>using var</c>/<c>var</c> 形态（ILocalSymbol.Type）。
    /// </summary>
    private ITypeSymbol? TryGetDeclaredLocalType(VariableDeclaratorSyntax declarator)
    {
        var original = _resolver.Resolve(declarator);
        var symbol = original == null ? null : _model.GetDeclaredSymbol(original);
        return (symbol as ILocalSymbol)?.Type;
    }

    private void AddError(string message, SyntaxNode node)
    {
        var line = 1;
        var character = 1;
        var positionNode = _resolver.Resolve(node) ?? node;
        try
        {
            var span = positionNode.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 行号不可用时退回 (1,1)。
        }

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF055,
            message,
            null, line, character));
        _statistics.Rejections++;
        _statistics.Uf055Rejections++;
    }

    private static BlockSyntax EmptyBlock() => Block();
}
