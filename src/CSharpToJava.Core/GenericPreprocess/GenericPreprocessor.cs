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
/// 此外，泛型 override 方法间的 typeof(T) 使用会被同步：
/// 如果 override A 的方法体使用了 typeof(TProperty)，而兄弟 override B 没有，
/// 会在 B 的方法体开头注入 var __cs2j_typeof_TProperty = typeof(TProperty);
/// 以确保转换器统一为所有 override 和 base 方法注入 _cs2j_T 参数。
/// </summary>
public sealed class GenericPreprocessor
{
    /// <summary>
    /// 全局 override 类型参数映射：base 方法符号 → 所有兄弟 override 方法体中
    /// 被使用的 typeof(T) 类型参数名集合。在预扫描阶段构建，用于在处理单个
    /// override 方法时判断是否需要注入哨兵语句。
    /// </summary>
    private HashSet<string>? _overrideSyncTypeParams;

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

        var rewriter = new GenericSyntaxRewriter(semanticModel, options, stats, _overrideSyncTypeParams);
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

    /// <summary>
    /// 预扫描整个编译，构建全局 override 类型参数映射。
    /// 在逐文件处理之前调用，用于识别所有泛型 override 方法及其兄弟方法中的
    /// typeof(T) 使用，以便在后续处理时为缺少 typeof(T) 的 override 注入哨兵语句。
    /// </summary>
    public void PrescanOverrideSiblings(Compilation compilation)
    {
        var syncTypeParams = new HashSet<string>(StringComparer.Ordinal);

        // 遍历所有命名空间和类型，收集所有泛型 override 方法
        var overrideGroups = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(
            SymbolEqualityComparer.Default);

        CollectOverrideGroups(compilation.GlobalNamespace, overrideGroups);

        // 对于每组兄弟 override 方法，收集所有 typeof(T) 使用的类型参数名
        foreach (var kvp in overrideGroups)
        {
            var baseMethod = kvp.Key;
            var overrides = kvp.Value;
            if (overrides.Count < 1)
                continue;

            // 收集所有 override 方法体中使用的 typeof(T) 类型参数名
            var allTypeOfParams = new HashSet<string>(StringComparer.Ordinal);
            foreach (var overrideMethod in overrides)
            {
                foreach (var syntaxRef in overrideMethod.DeclaringSyntaxReferences)
                {
                    var node = syntaxRef.GetSyntax();
                    if (node is not MethodDeclarationSyntax methodDecl)
                        continue;
                    var bodyRoot = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
                    if (bodyRoot == null)
                        continue;
                    foreach (var typeOf in bodyRoot.DescendantNodes().OfType<TypeOfExpressionSyntax>())
                    {
                        if (typeOf.Type is IdentifierNameSyntax id)
                        {
                            // 检查是否是方法级类型参数
                            if (overrideMethod.TypeParameters.Any(tp => tp.Name == id.Identifier.Text))
                                allTypeOfParams.Add(id.Identifier.Text);
                        }
                    }
                }
            }

            // 将收集到的类型参数名添加到全局集合
            foreach (var name in allTypeOfParams)
                syncTypeParams.Add(name);
        }

        _overrideSyncTypeParams = syncTypeParams;
    }

    private static void CollectOverrideGroups(
        INamespaceSymbol ns,
        Dictionary<IMethodSymbol, List<IMethodSymbol>> overrideGroups)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamedTypeSymbol type)
                CollectOverrideGroups(type, overrideGroups);
            else if (member is INamespaceSymbol subNs)
                CollectOverrideGroups(subNs, overrideGroups);
        }
    }

    private static void CollectOverrideGroups(
        INamedTypeSymbol type,
        Dictionary<IMethodSymbol, List<IMethodSymbol>> overrideGroups)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is IMethodSymbol method
                && method.IsOverride
                && method.OriginalDefinition.IsGenericMethod)
            {
                var baseMethod = method.OverriddenMethod;
                while (baseMethod?.OverriddenMethod != null)
                    baseMethod = baseMethod.OverriddenMethod;

                if (baseMethod != null)
                {
                    var baseKey = baseMethod.OriginalDefinition;
                    if (!overrideGroups.TryGetValue(baseKey, out var list))
                    {
                        list = new List<IMethodSymbol>();
                        overrideGroups[baseKey] = list;
                    }
                    list.Add(method.OriginalDefinition);
                }
            }

            if (member is INamedTypeSymbol nestedType)
                CollectOverrideGroups(nestedType, overrideGroups);
        }
    }
}

internal sealed class GenericSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly GenericPreprocessOptions _options;
    private readonly GenericPreprocessStatistics _stats;
    private readonly List<GenericPreprocessDiagnostic> _diagnostics = new();
    private readonly HashSet<string>? _overrideSyncTypeParams;

    public bool Changed { get; private set; }
    public IReadOnlyList<GenericPreprocessDiagnostic> Diagnostics => _diagnostics;

    public GenericSyntaxRewriter(
        SemanticModel? model,
        GenericPreprocessOptions options,
        GenericPreprocessStatistics stats,
        HashSet<string>? overrideSyncTypeParams = null)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _overrideSyncTypeParams = overrideSyncTypeParams;
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
        var shouldProcessForwarded = _options.InsertTypeOfForForwardedTypeParams;
        var shouldProcessOverrideSync = _options.SyncTypeOfForOverrideSiblings
            && _overrideSyncTypeParams != null
            && _overrideSyncTypeParams.Count > 0;

        if (!shouldProcessForwarded && !shouldProcessOverrideSync)
            return base.VisitMethodDeclaration(node);

        // Handle abstract generic methods: if override methods use typeof(T),
        // convert abstract → virtual and add a default body with typeof(T) so
        // the converter can detect the Class<T> requirement from the base
        // method itself (not just from overrides). This fixes call sites that
        // resolve to the base method before overrides are processed.
        if (shouldProcessOverrideSync
            && node.Modifiers.Any(SyntaxKind.AbstractKeyword)
            && node.TypeParameterList != null
            && node.TypeParameterList.Parameters.Count > 0)
        {
            var abstractTypeParams = new List<string>();
            foreach (var tp in node.TypeParameterList.Parameters)
            {
                if (_overrideSyncTypeParams!.Contains(tp.Identifier.Text))
                    abstractTypeParams.Add(tp.Identifier.Text);
            }

            if (abstractTypeParams.Count > 0)
            {
                // Build a default body: typeof(T) statements + throw new NotImplementedException()
                var statements = new List<StatementSyntax>();
                foreach (var tpName in abstractTypeParams)
                {
                    statements.Add(CreateTypeOfStatement(tpName));
                    _stats.ForwardedTypeParamsInserted++;
                }
                // Add a throw to make the body valid (virtual methods need a body)
                statements.Add(SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("System.NotImplementedException"),
                        SyntaxFactory.ArgumentList(),
                        null)));

                var body = SyntaxFactory.Block(statements);
                // Replace abstract with virtual: find the actual abstract token
                // in the modifier list (not a factory-created token) and replace it.
                var newModifiers = node.Modifiers;
                var abstractToken = newModifiers.FirstOrDefault(t => t.IsKind(SyntaxKind.AbstractKeyword));
                if (!abstractToken.IsKind(SyntaxKind.None))
                {
                    newModifiers = newModifiers.Replace(abstractToken,
                        SyntaxFactory.Token(SyntaxKind.VirtualKeyword)
                            .WithTriviaFrom(abstractToken));
                }
                else
                {
                    // No abstract keyword found (shouldn't happen since we checked
                    // node.Modifiers.Any(AbstractKeyword) above, but be safe)
                    newModifiers = newModifiers.Add(SyntaxFactory.Token(SyntaxKind.VirtualKeyword));
                }
                var newNode = node
                    .WithModifiers(newModifiers)
                    .WithBody(body)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default);
                Changed = true;
                return base.VisitMethodDeclaration(newNode);
            }
        }

        // 只处理有 body 的方法（抽象方法无 body，不处理）
        if (node.Body == null && node.ExpressionBody == null)
            return base.VisitMethodDeclaration(node);

        if (node.TypeParameterList == null || node.TypeParameterList.Parameters.Count == 0)
            return base.VisitMethodDeclaration(node);

        // 递归访问子节点（这会触发其它 Visit* 改写）
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node);

        // 收集方法体中已被「跟踪模式」使用的类型参数
        var trackedParamNames = new HashSet<string>(StringComparer.Ordinal);
        CollectTrackedTypeParamNames(visited.Body ?? (SyntaxNode?)visited.ExpressionBody, trackedParamNames);

        // 收集需要注入 typeof 哨兵语句的类型参数名
        var paramsToInsert = new List<string>();

        if (shouldProcessForwarded)
        {
            // 收集方法体中作为「前向类型参数」出现的类型参数
            var forwardedParamNames = new HashSet<string>(StringComparer.Ordinal);
            CollectForwardedTypeParamNames(visited.Body ?? (SyntaxNode?)visited.ExpressionBody,
                node.TypeParameterList.Parameters, forwardedParamNames);

            // 找出「仅作为前向类型参数」出现的类型参数
            foreach (var tp in node.TypeParameterList.Parameters)
            {
                var name = tp.Identifier.Text;
                if (forwardedParamNames.Contains(name) && !trackedParamNames.Contains(name))
                    paramsToInsert.Add(name);
            }
        }

        if (shouldProcessOverrideSync)
        {
            // 对于 override 方法：检查全局 override 同步集合中是否有当前方法体
            // 中未被 typeof 使用的类型参数。这些参数在其他兄弟 override 中被 typeof 使用，
            // 需要在当前方法体中注入哨兵语句以确保转换器统一注入 _cs2j_T 参数。
            foreach (var tp in node.TypeParameterList.Parameters)
            {
                var name = tp.Identifier.Text;
                if (_overrideSyncTypeParams!.Contains(name)
                    && !trackedParamNames.Contains(name)
                    && !paramsToInsert.Contains(name))
                {
                    paramsToInsert.Add(name);
                }
            }
        }

        if (paramsToInsert.Count == 0)
            return visited;

        // 为每个需要注入的类型参数插入 ``var __cs2j_typeof_<T> = typeof(<T>);``
        //
        // 表达式体方法（`=> (obj) => ...`）原先在这里直接返回，导致这类 override 永远
        // 拿不到哨兵 ⇒ 永远没有 Class<T> 令牌，而它的基类声明（抽象方法，走同一集合被
        // 改写为 virtual）却拿到了令牌 ⇒ 两侧签名不一致、override 实际不构成覆写、
        // 虚派发落到基类方法体（SDK 实测 35 项失败，栈内均含 MemberAccessor.createField*）。
        // 这里把表达式体包装为块体后再注入，保持"只加不减"。
        var injectionBody = visited.Body;
        if (injectionBody == null)
        {
            if (visited.ExpressionBody == null)
                return visited;
            injectionBody = SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(visited.ExpressionBody.Expression));
        }

        var newStatements = injectionBody.Statements;
        foreach (var tpName in paramsToInsert)
        {
            var typeofStatement = CreateTypeOfStatement(tpName);
            newStatements = newStatements.Insert(0, typeofStatement);
            _stats.ForwardedTypeParamsInserted++;
            Changed = true;
        }

        var rewrittenMethod = visited.WithBody(injectionBody.WithStatements(newStatements));
        return visited.ExpressionBody == null
            ? rewrittenMethod
            : rewrittenMethod.WithExpressionBody(null).WithSemicolonToken(default);
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
