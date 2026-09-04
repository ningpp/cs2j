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
            var typeSyntax = node.Type;
            _stats.NewTypeParamRewritten++;
            Changed = true;
            var invocation = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("System.Activator"),
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
                        SyntaxFactory.IdentifierName("System.Array"),
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
    // 前向类型参数（forwarded-only type param）识别与插入：
    // 如果方法体中某个类型参数仅作为显式类型参数出现在泛型转发调用中
    // （SomeMethod<T>(arg)），而没有被 default(T)/typeof(T)/new T()/is T 等
    // 已跟踪模式使用，则转换器不会在 Java 方法签名中添加 _cs2j_T 前导参数，
    // 导致 override 体中对该类型参数的引用成为未声明符号。
    //
    // 为此在方法体前部插入 ``var __cs2j_typeof_<T> = typeof(<T>);`` 以触发
    // 转换器的 AddTypeOfDefaultRequirements 识别。
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (!_options.InsertTypeOfForForwardedTypeParams)
            return base.VisitMethodDeclaration(node);

        // 只处理方法（抽头方法无 body，不处理）
        if (node.Body == null && node.ExpressionBody == null)
            return base.VisitMethodDeclaration(node);

        if (node.TypeParameterList == null || node.TypeParameterList.Parameters.Count == 0)
            return base.VisitMethodDeclaration(node);

        // 递归访问子节点（这会触发其它 Visit* 改写）
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node);

        // 收集方法体中已被「跟踪模式」使用的类型参数
        var trackedParamNames = new HashSet<string>(StringComparer.Ordinal);
        CollectTrackedTypeParamNames(visited.Body ?? (SyntaxNode?)visited.ExpressionBody, trackedParamNames);

        // 收集方法体中作为「前向类型参数」出现的类型参数
        var forwardedParamNames = new HashSet<string>(StringComparer.Ordinal);
        CollectForwardedTypeParamNames(visited.Body ?? (SyntaxNode?)visited.ExpressionBody,
            node.TypeParameterList.Parameters, forwardedParamNames);

        // 找出「仅作为前向类型参数」出现的类型参数
        var onlyForwarded = new List<string>();
        foreach (var tp in node.TypeParameterList.Parameters)
        {
            var name = tp.Identifier.Text;
            if (forwardedParamNames.Contains(name) && !trackedParamNames.Contains(name))
                onlyForwarded.Add(name);
        }

        if (onlyForwarded.Count == 0)
            return visited;

        // 为每个仅前向的类型参数插入 ``var __cs2j_typeof_<T> = typeof(<T>);``
        if (visited.Body == null)
            return visited;

        var newStatements = visited.Body.Statements;
        foreach (var tpName in onlyForwarded)
        {
            var typeofStatement = CreateTypeOfStatement(tpName);
            newStatements = newStatements.Insert(0, typeofStatement);
            _stats.ForwardedTypeParamsInserted++;
            Changed = true;
        }

        return visited.WithBody(visited.Body.WithStatements(newStatements));
    }

    private static LocalDeclarationStatementSyntax CreateTypeOfStatement(string typeParamName)
    {
        var typeofExpr = SyntaxFactory.TypeOfExpression(
            SyntaxFactory.IdentifierName(typeParamName));
        var variable = SyntaxFactory.VariableDeclarator($"__cs2j_typeof_{typeParamName}")
            .WithInitializer(SyntaxFactory.EqualsValueClause(typeofExpr));
        var declaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(variable)));
        // 注意：注释必须另起一行，不能和声明语句放在同一行，
        // 否则 Java 转换器会把整行当作注释。
        return declaration
            .WithLeadingTrivia(
                SyntaxFactory.Comment($"// __cs2j: forward-type-param 识别声明 ({typeParamName})"),
                SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
    }

    /// <summary>
    /// 收集方法体中被跟踪模式（default(T)/typeof(T)/new T()/new T[n]/is T/as T）
    /// 使用的类型参数名。
    /// </summary>
    private void CollectTrackedTypeParamNames(SyntaxNode? root, HashSet<string> trackedNames)
    {
        if (root == null) return;
        foreach (var node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case DefaultExpressionSyntax d when IsTypeParameterType(d.Type):
                    trackedNames.Add(d.Type.GetText().ToString().Trim());
                    break;
                case TypeOfExpressionSyntax t when IsTypeParameterType(t.Type):
                    trackedNames.Add(t.Type.GetText().ToString().Trim());
                    break;
                case ObjectCreationExpressionSyntax o when IsTypeParameterType(o.Type):
                    trackedNames.Add(o.Type.GetText().ToString().Trim());
                    break;
            }
        }
    }

    /// <summary>
    /// 收集方法体中作为「前向类型参数」出现的类型参数：
    /// 即仅出现在显式类型参数列表中、调用了其他泛型方法的类型参数。
    /// </summary>
    private static void CollectForwardedTypeParamNames(
        SyntaxNode? root,
        Microsoft.CodeAnalysis.SeparatedSyntaxList<TypeParameterSyntax> methodTypeParameters,
        HashSet<string> forwardedNames)
    {
        if (root == null) return;
        var tpNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tp in methodTypeParameters)
            tpNames.Add(tp.Identifier.Text);

        foreach (var node in root.DescendantNodesAndSelf())
        {
            if (node is GenericNameSyntax genericName)
            {
                foreach (var typeArg in genericName.TypeArgumentList.Arguments)
                {
                    var name = typeArg.GetText().ToString().Trim();
                    // 只把方法自身定义的类型参数算作「前向」候选
                    if (tpNames.Contains(name) && IsForwardedTypeArg(typeArg))
                    {
                        forwardedNames.Add(name);
                    }
                }
            }
        }
    }

    private static bool IsForwardedTypeArg(TypeSyntax typeArg)
    {
        // 裸标识符类型参数（如 T、TValue）视为前向候选
        return typeArg is IdentifierNameSyntax;
    }

    // -----------------------------------------------------------------------
    // Helpers: determine whether a TypeSyntax denotes a type parameter.
    // Uses the SemanticModel when available; falls back to a conservative
    // syntactic heuristic (single-identifier, not a known BCL type).
    // -----------------------------------------------------------------------
    private bool IsTypeParameterType(TypeSyntax? type)
    {
        if (type == null) return false;

        // Quickly accept IdentifierNameSyntax that the semantic model confirms
        // resolves to a type parameter.
        if (_model != null)
        {
            try
            {
                var symbol = _model.GetSymbolInfo(type).Symbol;
                if (symbol is ITypeParameterSymbol)
                    return true;
                // If symbol resolves to a real named type, it's NOT a type param.
                if (symbol is INamedTypeSymbol)
                    return false;
                // Unresolved identifier in a generic context: fall through to
                // the syntactic heuristic only when we have a generic enclosing
                // type/method — but be conservative.
            }
            catch (ArgumentException)
            {
                // Node may not be part of this semantic model's tree (e.g.
                // already-rewritten node). Fall back to syntactic heuristic.
            }
        }

        // Syntactic heuristic (fallback when no semantic model or symbol
        // unresolved): a bare single identifier (no namespace qualifier, no
        // type arguments) is a candidate type parameter.
        //
        // We accept:
        //  - Single uppercase letters (T, K, V) — classic type parameter names.
        //  - Identifiers starting with 'T' followed by another uppercase
        //    letter (TKey, TValue, TItem) — the conventional .NET pattern
        //    for multi-char type parameter names (CA1715 naming guideline).
        //
        // We reject predefined type names (int, string, etc.) to avoid
        // false positives on BCL value types.
        if (type is IdentifierNameSyntax id)
        {
            var name = id.Identifier.Text;
            if (IsPredefinedTypeName(name))
                return false;
            // Single uppercase letter (T, K, V, U).
            if (name.Length == 1 && char.IsLetter(name[0]))
                return true;
            // Multi-char names following the TXxx convention (TKey, TValue, TItem).
            // This is the standard .NET naming guideline for type parameters
            // when a descriptive name is needed (CA1715).
            if (name.Length >= 2 && name[0] == 'T' && char.IsUpper(name[1]))
                return true;
        }

        return false;
    }

    private bool IsTypeParameterElementType(ArrayTypeSyntax arrayType)
    {
        // The element type is arrayType.ElementType (may itself be T).
        return IsTypeParameterType(arrayType.ElementType);
    }

    private static bool IsPredefinedTypeName(string name) => name switch
    {
        "bool" or "byte" or "sbyte" or "char" or "decimal" or "double" or "float"
        or "int" or "uint" or "long" or "ulong" or "short" or "ushort"
        or "object" or "string" or "void" or "dynamic" or "var"
        or "Boolean" or "Byte" or "SByte" or "Char" or "Decimal" or "Double"
        or "Single" or "Int32" or "UInt32" or "Int64" or "UInt64"
        or "Int16" or "UInt16" or "Object" or "String" or "Void"
        => true,
        _ => false,
    };
}
