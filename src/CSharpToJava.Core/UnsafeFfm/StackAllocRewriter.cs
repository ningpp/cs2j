using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R4：stackalloc 统一堆化改写（设计 D5，§4 组件表）。语法实测结论
/// （Roslyn 5.0.0，LanguageVersion.Preview 临时 probe）：
/// <list type="bullet">
/// <item><c>stackalloc T[n]</c> / <c>stackalloc T[]{...}</c> →
/// <see cref="StackAllocArrayCreationExpressionSyntax"/>；其 <c>Type</c>
/// 属性静态类型是 TypeSyntax、运行时为 <see cref="ArrayTypeSyntax"/>
/// （维度在 RankSpecifiers，初始化器形态维度=OmittedArraySizeExpression），
/// <c>Initializer</c> 可空；</item>
/// <item><c>stackalloc[] {...}</c> →
/// <see cref="ImplicitStackAllocArrayCreationExpressionSyntax"/>
/// （含 StackAllocKeyword、空方括号与 Initializer）；</item>
/// <item>站点转换类型 <c>GetTypeInfo(node).ConvertedType</c>：显式 Span 目标
/// （实参/条件/赋值/返回同形）为 Span&lt;T&gt;/ReadOnlySpan&lt;T&gt;；
/// 裸指针目标与 <c>var x = stackalloc …</c>（自然类型即指针）为 T*。</item>
/// </list>
/// 收编形态：stackalloc 表达式统一替换为 <c>new T[n]</c> / <c>new T[]{...}</c>
/// 堆数组（T[] 到 Span/ReadOnlySpan 为既有隐式转换）；Span/ReadOnlySpan
/// 目标的局部声明归一为 <c>T[] x = new T[n];</c>。该形态与 goto 消除器
/// <see cref="GotoEliminator.StateMachineBuilder"/> 既有 CS8353 降级
/// （<c>hoisted = new T[n]</c>）完全一致，两 pass 互不返工。
/// UF040 的站点/元素判定由 <see cref="StackAllocElementSafety"/> 统一提供；
/// 预处理门面在 R1 之前先做拒绝预检（拒绝优先），本改写只处理安全站点。
/// 替换节点带注释，天然幂等。
/// </summary>
internal sealed class StackAllocRewriter : CSharpSyntaxRewriter
{
    public static readonly SyntaxAnnotation StackAllocReplacedAnnotation =
        new("c06-stackalloc-replaced");

    private readonly SemanticModel _model;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    public StackAllocRewriter(
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

    // ---- 显式 stackalloc ----

    public override SyntaxNode? VisitStackAllocArrayCreationExpression(
        StackAllocArrayCreationExpressionSyntax node)
    {
        var visited = (StackAllocArrayCreationExpressionSyntax)
            base.VisitStackAllocArrayCreationExpression(node)!;

        var originalNode = _resolver.Resolve(node) as StackAllocArrayCreationExpressionSyntax;
        var siteType = originalNode != null
            ? StackAllocElementSafety.GetSiteType(_model, originalNode)
            : null;

        if (siteType is IPointerTypeSymbol)
        {
            AddError(
                $"stackalloc expression '{node.ToString().Replace("\n", " ")}' is assigned to a " +
                "bare pointer target (including 'var', whose natural type for stackalloc is T*); " +
                "it cannot be converted to a Span/array local without changing pointer-only usages. " +
                "Rewrite it as 'Span<T> x = stackalloc T[n];' (or an array form) manually.",
                node);
            return visited;
        }

        if (visited.Type is not ArrayTypeSyntax arrayType)
        {
            AddError(
                $"stackalloc expression '{node}' has an unrecognized type shape " +
                "(internal rule gap).",
                node);
            return visited;
        }

        var elementSymbol = originalNode != null
            ? StackAllocElementSafety.GetExplicitElement(_model, originalNode)
            : null;
        var elementReason = string.Empty;
        if (elementSymbol == null || !StackAllocElementSafety.IsSafeElement(elementSymbol, out elementReason))
        {
            AddError(
                $"stackalloc element type '{elementSymbol?.ToDisplayString() ?? "?"}' cannot be safely " +
                "heap-allocated: " + elementReason + " Rewrite the allocation manually or route it " +
                "through a supported form.",
                node);
            return visited;
        }

        var creation = ArrayCreationExpression(arrayType)
            .WithInitializer(visited.Initializer)
            .WithLeadingTrivia(visited.GetLeadingTrivia())
            .WithAdditionalAnnotations(StackAllocReplacedAnnotation);

        _statistics.ExplicitStackAllocCollected++;
        return creation;
    }

    // ---- 隐式 stackalloc（stackalloc[] {...}）----

    public override SyntaxNode? VisitImplicitStackAllocArrayCreationExpression(
        ImplicitStackAllocArrayCreationExpressionSyntax node)
    {
        var visited = (ImplicitStackAllocArrayCreationExpressionSyntax)
            base.VisitImplicitStackAllocArrayCreationExpression(node)!;

        var originalNode = _resolver.Resolve(node) as ImplicitStackAllocArrayCreationExpressionSyntax;
        var siteType = originalNode != null
            ? StackAllocElementSafety.GetSiteType(_model, originalNode)
            : null;

        if (siteType is IPointerTypeSymbol)
        {
            AddError(
                $"implicit stackalloc expression '{node}' is used at a bare pointer site (including " +
                "'var', whose natural type for stackalloc is T*); it cannot be converted to a " +
                "Span/array form without changing pointer-only usages. Rewrite it manually with an " +
                "explicit 'Span<T>' target.",
                node);
            return visited;
        }

        var elementSymbol = originalNode != null
            ? StackAllocElementSafety.GetImplicitElement(_model, originalNode)
            : null;
        var elementReason = string.Empty;
        if (elementSymbol == null || !StackAllocElementSafety.IsSafeElement(elementSymbol, out elementReason))
        {
            AddError(
                $"implicit stackalloc element type '{elementSymbol?.ToDisplayString() ?? "?"}' cannot be " +
                "safely heap-allocated: " + elementReason + " Rewrite the allocation manually.",
                node);
            return visited;
        }

        var elementSyntax = ParseTypeName(elementSymbol.ToDisplayString());
        var arrayType = ArrayType(elementSyntax)
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier()));

        var creation = ArrayCreationExpression(arrayType)
            .WithInitializer(visited.Initializer)
            .WithLeadingTrivia(visited.GetLeadingTrivia())
            .WithAdditionalAnnotations(StackAllocReplacedAnnotation);

        _statistics.ImplicitStackAllocCollected++;
        return creation;
    }

    // ---- Span 目标局部声明归一为 T[] 局部 ----

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var visited = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;

        // 仅当每个带初始化器的变量都是本规则替换产物时才归一（混合/无初始化器
        // 形态保持 Span 声明——Span<T> x = new T[n] 本身可编译）。
        foreach (var variable in visited.Declaration.Variables)
        {
            var value = variable.Initializer?.Value;
            if (value == null || !value.HasAnnotation(StackAllocReplacedAnnotation))
            {
                return visited;
            }
        }

        var originalDeclarator = _resolver.Resolve(node.Declaration.Variables.First());
        TypeSyntax? elementTypeSyntax = null;

        if (originalDeclarator != null
            && _model.GetDeclaredSymbol(originalDeclarator) is ILocalSymbol local
            && StackAllocElementSafety.IsSpanLike(local.Type, out var elementSymbol)
            && elementSymbol != null)
        {
            elementTypeSyntax = ParseTypeName(elementSymbol.ToDisplayString());
        }
        else if (visited.Declaration.Type is GenericNameSyntax generic
            && (generic.Identifier.ValueText is "Span" or "ReadOnlySpan")
            && generic.TypeArgumentList.Arguments.Count == 1)
        {
            // 退路：语义不可用时按语法 GenericName 判定（仅限 Span 形态名）。
            elementTypeSyntax = generic.TypeArgumentList.Arguments[0];
        }

        if (elementTypeSyntax == null) return visited;

        var arrayDeclarationType = ArrayType(elementTypeSyntax)
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier(
                SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression()))));

        return visited.WithDeclaration(visited.Declaration.WithType(arrayDeclarationType));
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
            // 脱离语法树的节点无法计算行号时退回 (1,1)。
        }

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF040,
            message,
            null, line, character));
        _statistics.Rejections++;
        _statistics.Uf040Rejections++;
    }
}
