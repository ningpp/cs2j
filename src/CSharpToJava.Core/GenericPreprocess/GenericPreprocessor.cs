using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>
/// C# 泛型预处理核心入口。把对转译器不友好的泛型用法改写为语义等价、
/// 但更适合 Java（type-erased）转译的 C# 代码。
///
/// 当前覆盖的 P0 模式（全部保证语义等价、可在 .NET 上编译并测试通过）：
///   - default(T)              → default 字面量（C# 7.2+）；在 `default(T) == null`
///                               等二义性上下文中改写为 `(object)default` 规避 CS0034。
///   - new T()                 → Activator.CreateInstance&lt;T&gt;()
///   - new T[n]                → (T[])Array.CreateInstance(typeof(T), n)
///
/// typeof(T) / is T / as T 暂不降级（仅统计），避免破坏签名/控制流。
///
/// 泛型 override 族锚点同步与仅前向类型参数的 typeof 锚点由下游
/// generic-deepen (C02) pass 的 GD-ANCHOR-2/3 负责（基于全编译符号表），
/// 本 pass 不再注入 __cs2j_typeof_ 哨兵。
/// </summary>
public sealed class GenericPreprocessor
{
    public GenericPreprocessResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        GenericPreprocessOptions? options = null)
    {
        options ??= new GenericPreprocessOptions();
        var stats = new GenericPreprocessStatistics();
        stats.FilesScanned = 1;

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var rewriter = new GenericSyntaxRewriter(semanticModel, options, stats);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new GenericPreprocessResult(
                sourceCode, Changed: false, Array.Empty<GenericPreprocessDiagnostic>(), stats);
        }

        // 格式化以规整 SyntaxFactory 构造节点缺省的 trivia
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(rewritten, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;
        return new GenericPreprocessResult(
            code, Changed: true, rewriter.Diagnostics, stats);
    }

    /// <summary>不依赖语义模型的重载（保守：只做不包含类型参数解析的安全改写）。</summary>
    public GenericPreprocessResult Preprocess(string sourceCode, GenericPreprocessOptions? options = null)
        => Preprocess(sourceCode, semanticModel: null, options);
}

internal sealed class GenericSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly GenericPreprocessOptions _options;
    private readonly GenericPreprocessStatistics _stats;
    private readonly List<GenericPreprocessDiagnostic> _diagnostics = new();

    public bool Changed { get; private set; }
    public IReadOnlyList<GenericPreprocessDiagnostic> Diagnostics => _diagnostics;

    public GenericSyntaxRewriter(
        SemanticModel? model,
        GenericPreprocessOptions options,
        GenericPreprocessStatistics stats)
    {
        _model = model;
        _options = options;
        _stats = stats;
    }

    // -----------------------------------------------------------------------
    // default(T): counted always; rewritten to `default` literal when
    // RewriteDefaultExpression is enabled (on by default).
    //
    // The bare `default` literal is ambiguous or breaks type inference in
    // several contexts, so we handle them specially:
    //
    //  1. Binary comparison (==, !=):
    //     default(T) == null  →  NOT rewritten (left as default(T)).
    //     The bare `default` literal is ambiguous (CS0034), and casting to
    //     `(object)default` is NOT semantically equivalent for value-type T
    //     (boxing differences). So we skip rewriting in this context.
    //
    //  2. Argument to a generic method call that relies on type inference
    //     (no explicit type arguments):
    //     JsonSerializer.Serialize(default(T))
    //     → NOT rewritten (bare `default` would cause CS0411 because the
    //       compiler cannot infer the type parameter from a `default` literal).
    //
    //  3. All other contexts (assignment, return, parameter to non-generic
    //     method, etc.):
    //     default(T)  →  default
    //     Safe because the target type is unambiguous.
    // -----------------------------------------------------------------------
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitDefaultExpression(DefaultExpressionSyntax node)
    {
        var visited = base.VisitDefaultExpression(node);

        // Only consider when the target type is a type parameter.
        if (!IsTypeParameterType(node.Type))
            return visited;

        // Count the occurrence regardless of whether we rewrite.
        _stats.DefaultExpressionsRewritten++;

        if (!_options.RewriteDefaultExpression)
            return visited;

        // Context 2: argument to a generic method call without explicit type
        // arguments — bare `default` would break type inference (CS0411).
        if (IsArgumentToGenericMethodWithoutTypeArgs(node))
            return visited;

        // Context 1: operand of a == or != comparison — do NOT rewrite.
        // The bare `default` literal is ambiguous (CS0034), and casting to
        // `(object)default` is semantically different for value-type T
        // (default(T) boxes to a non-null zero-value object, but
        // `(object)default` resolves to null). So we skip rewriting here.
        if (IsInEqualityComparison(node))
            return visited;

        Changed = true;

        // Context 3: safe context — bare `default` literal.
        return SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)
            .WithTriviaFrom(node);
    }

    /// <summary>
    /// Returns true if the node is a left or right operand of a == or !=
    /// binary expression, where a bare `default` literal would be ambiguous.
    /// </summary>
    private static bool IsInEqualityComparison(DefaultExpressionSyntax node)
    {
        var parent = node.Parent;
        if (parent is BinaryExpressionSyntax binary)
        {
            return binary.IsKind(SyntaxKind.EqualsExpression)
                || binary.IsKind(SyntaxKind.NotEqualsExpression);
        }
        return false;
    }

    /// <summary>
    /// Returns true if the node is an argument to a generic method invocation
    /// that does NOT have explicit type arguments. In such cases the compiler
    /// relies on the argument type to infer the type parameter, and a bare
    /// `default` literal cannot provide that type (CS0411).
    /// </summary>
    private static bool IsArgumentToGenericMethodWithoutTypeArgs(DefaultExpressionSyntax node)
    {
        // Walk up: default(T) → Argument → ArgumentList → InvocationExpression
        if (node.Parent is ArgumentSyntax argument
            && argument.Parent is ArgumentListSyntax argumentList
            && argumentList.Parent is InvocationExpressionSyntax invocation)
        {
            // Check if the method being invoked is a generic method without
            // explicit type arguments (e.g. Serialize(default) not Serialize<T>(default)).
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                // The name part could be a GenericName (has type args) or a
                // plain IdentifierName (no type args). Only skip when it's a
                // plain identifier — meaning no explicit type args.
                return memberAccess.Name is IdentifierNameSyntax;
            }
            if (invocation.Expression is IdentifierNameSyntax)
            {
                // Direct call like Method(default(T)) without type args.
                return true;
            }
            if (invocation.Expression is GenericNameSyntax)
            {
                // Has explicit type arguments — safe to rewrite.
                return false;
            }
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // new T() → Activator.CreateInstance<T>()
    // new T[n] → (T[])Array.CreateInstance(typeof(T), n)
    // Both surface through VisitObjectCreationExpression and VisitArrayCreationExpression.
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitObjectCreationExpression(node) ?? node;

        if (_options.RewriteNewTypeParam
            && node.ArgumentList?.Arguments.Count == 0
            && node.Initializer == null
            && IsTypeParameterType(node.Type))
        {
            // new T() with no arguments and no initializer.
            // Activator.CreateInstance<T>() invokes the parameterless constructor —
            // identical to `new T()` (both require a public parameterless ctor).
            // global:: is mandatory: inside projects whose own namespace starts with
            // `dotnet.System` (or any namespace shadowing a BCL prefix), a bare
            // `System.Activator` resolves to the shadowing namespace (CS0234).
            var typeSyntax = node.Type;
            _stats.NewTypeParamRewritten++;
            Changed = true;
            var invocation = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseName("global::System.Activator"),
                    SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier("CreateInstance"),
                        SyntaxFactory.TypeArgumentList(
                            SyntaxFactory.SingletonSeparatedList<TypeSyntax>(typeSyntax)))),
                SyntaxFactory.ArgumentList());
            return invocation.WithTriviaFrom(node);
        }

        return visited;
    }

    public override SyntaxNode? VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitArrayCreationExpression(node) ?? node;

        if (_options.RewriteNewTypeParamArray
            && node.Initializer == null
            && IsTypeParameterElementType(node.Type))
        {
            // new T[n] → (T[])Array.CreateInstance(typeof(T), n)
            var elementType = node.Type.ElementType;
            var rankSpecifiers = node.Type.RankSpecifiers;
            // We only handle single-dimensional T[n]; multi-dim (T[,]) stays as-is
            // (Array.CreateInstance has a different overload shape for multi-dim).
            if (rankSpecifiers.Count == 1
                && rankSpecifiers[0].Sizes.Count == 1)
            {
                var sizeExpression = rankSpecifiers[0].Sizes[0];
                _stats.NewTypeParamArrayRewritten++;
                Changed = true;

                var arrayCreate = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        // global:: keeps the lookup on the real BCL System.Array even
                        // when the enclosing namespace shadows `System` (e.g.
                        // dotnet.System.Collections.Generic) — see VisitObjectCreationExpression.
                        SyntaxFactory.ParseName("global::System.Array"),
                        SyntaxFactory.IdentifierName("CreateInstance")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[]
                        {
                            SyntaxFactory.Argument(
                                SyntaxFactory.TypeOfExpression(elementType)),
                            SyntaxFactory.Argument(sizeExpression)
                        })));

                // The cast target must be `T[]` (element type + empty rank specifier),
                // NOT `T[n]` (which carries the size expression and is invalid as a cast
                // target). Build a fresh ArrayTypeSyntax without the size.
                var castType = SyntaxFactory.ArrayType(elementType)
                    .WithRankSpecifiers(SyntaxFactory.SingletonList(
                        SyntaxFactory.ArrayRankSpecifier(
                            SyntaxFactory.Token(SyntaxKind.OpenBracketToken),
                            default,
                            SyntaxFactory.Token(SyntaxKind.CloseBracketToken))));
                var cast = SyntaxFactory.CastExpression(castType, arrayCreate);
                return cast.WithTriviaFrom(node);
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Tracking-only patterns (no rewrite): typeof(T), is T, as T
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitTypeOfExpression(TypeOfExpressionSyntax node)
    {
        if (_options.TrackTypeOfExpression && IsTypeParameterType(node.Type))
        {
            _stats.TypeOfExpressionsSeen++;
        }
        return base.VisitTypeOfExpression(node);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        // `x is T` / `x as T` surface as BinaryExpression in C# (pre-pattern)
        if (node.IsKind(SyntaxKind.IsExpression) || node.IsKind(SyntaxKind.AsExpression))
        {
            // For `is`/`as`, the right operand is a TypeSyntax even though the
            // BinaryExpressionSyntax.Right property is typed as ExpressionSyntax.
            if (node.Right is TypeSyntax rightType && IsTypeParameterType(rightType))
            {
                if (node.IsKind(SyntaxKind.IsExpression)) _stats.IsTypeParamSeen++;
                else _stats.AsTypeParamSeen++;
            }
        }
        return base.VisitBinaryExpression(node);
    }

    // -----------------------------------------------------------------------
    // Helpers: determine whether a TypeSyntax denotes a type parameter.
    // Delegated to the shared GenericDeepenSyntaxFacts (C02) so the generic
    // (E10) and generic-deepen (C02) passes cannot drift apart.
    // -----------------------------------------------------------------------
    private bool IsTypeParameterType(TypeSyntax? type)
        => GenericDeepenSyntaxFacts.IsTypeParameterType(_model, type);

    private bool IsTypeParameterElementType(ArrayTypeSyntax arrayType)
    {
        // The element type is arrayType.ElementType (may itself be T).
        return IsTypeParameterType(arrayType.ElementType);
    }
}
