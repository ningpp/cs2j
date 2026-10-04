using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// 单个 await using 资源的冻结计划（设计 §4.1/§4.3）。
/// </summary>
/// <param name="Declarator">语句/声明形态中显式声明的变量；纯表达式形态为 null。</param>
/// <param name="DeclaredTypeSyntax">显式声明形态的源类型语法（可能为 var）；纯表达式为 null。</param>
/// <param name="ResourceExpression">资源表达式（只求值一次，try 外原位保留）。</param>
/// <param name="Name">finally 释放引用的标识符（用户变量名或纯表达式临时量名）。</param>
/// <param name="IsReferenceType">引用类型资源 → <c>if (r != null)</c> 守卫（不做 nullability 流分析）。</param>
/// <param name="IsNullableValueType"><c>S?</c> 资源 → <c>if (r.HasValue)</c> 守卫、<c>r.Value</c> 接收者。</param>
/// <param name="UseInterfaceCast">显式接口实现等无公开模式方法 → <c>((IAsyncDisposable)r)</c> 转换调用。</param>
internal sealed record AwaitUsingResource(
    VariableDeclaratorSyntax? Declarator,
    TypeSyntax? DeclaredTypeSyntax,
    ExpressionSyntax ResourceExpression,
    string Name,
    bool IsReferenceType,
    bool IsNullableValueType,
    bool UseInterfaceCast);

/// <summary>
/// 一个 await using 站点（语句形态或 using 声明形态；多变量声明的每个声明符为一个资源，
/// 按声明序嵌套展开，禁止合并）。
/// </summary>
internal sealed record AwaitUsingSite(
    StatementSyntax Node,
    bool IsStatementForm,
    IReadOnlyList<AwaitUsingResource> Resources);

/// <summary>
/// C38 3a 单文件分析结果。诊断一律 Error 发射，非严格降级由入口统一处理。
/// <see cref="RejectedNodes"/>（1841/1842/1845）与 <see cref="BaselineExemptNodes"/>
/// （CS4033/CS8647 等基线已报站点）在重写时原样保留并挂内存 skip 标注供 residual 白名单识别。
/// </summary>
internal sealed record AsyncStreamAnalysisResult(
    IReadOnlyList<AsyncNormalizeDiagnostic> Diagnostics,
    IReadOnlyList<AwaitUsingSite> Sites,
    IReadOnlyList<StatementSyntax> RejectedNodes,
    IReadOnlyList<StatementSyntax> BaselineExemptNodes,
    int StatementSitesAccepted,
    int DeclarationSitesAccepted,
    int GuardsPlanned,
    int TypeSitesMapped,
    int TypeSitesRejected,
    int DuckTypedDeferred);

/// <summary>
/// C38 3a 发现面分析（设计 §4.1、§4.3、§6、D4/D5/D8/D10/D11）：
/// 逐方法/块发现带 AwaitKeyword 的 UsingStatementSyntax 与 using LocalDeclarationStatementSyntax；
/// 基线优先（Roslyn 已报的 CS4033/CS8647 等不二次诊断）；计划在任何改写前冻结。
/// </summary>
internal static class AsyncStreamAnalyzer
{
    public static AsyncStreamAnalysisResult Analyze(
        SemanticModel model,
        AsyncNormalizeOptions options)
    {
        var diagnostics = new List<AsyncNormalizeDiagnostic>();
        var sites = new List<AwaitUsingSite>();
        var rejected = new List<StatementSyntax>();
        var baselineExempt = new List<StatementSyntax>();

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var asyncDisposableInterface =
            model.Compilation.GetTypeByMetadataName(AsyncTypeResolver.AsyncDisposableMetadataName);
        var disposableInterface =
            model.Compilation.GetSpecialType(SpecialType.System_IDisposable);

        var baselineErrors = CollectBaselineErrors(model);
        var dualImplInfoEmitted = new HashSet<string>(StringComparer.Ordinal);

        // 宿主成员作用域：已用名集合 + 纯表达式临时量序号（源序确定性）。
        var scopeNamesByMember = new Dictionary<SyntaxNode, HashSet<string>>();
        var tempCountersByMember = new Dictionary<SyntaxNode, int>();

        var statementsAccepted = 0;
        var declarationsAccepted = 0;
        var guardsPlanned = 0;
        var typeSitesRejected = 0;
        var duckTypedDeferred = 0;

        foreach (var candidate in EnumerateAwaitUsingNodes(root))
        {
            // 基线优先：站点跨度内已有编译错误（非 async 内 await＝CS4033、
            // switch section using 声明＝CS8647 等）不抢诊断、不改写，只挂白名单。
            if (BaselineContains(baselineErrors, model.SyntaxTree, candidate.Span))
            {
                baselineExempt.Add(candidate);
                continue;
            }

            // 声明形态只支持直接落在 Block（v1 可安全包尾到块末）；其余位置
            // （top-level statements 等）CS2J1845 防御报错、原样保留。
            if (candidate is LocalDeclarationStatementSyntax { Parent: not BlockSyntax })
            {
                rejected.Add(candidate);
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                    "'await using' declaration is only supported directly inside a block '{ }' " +
                    "(scope folding to the block end); declaration-form 'await using' in this " +
                    "host scope cannot be wrapped safely. Rewrite it as statement form or move it " +
                    "into an enclosing block."));
                continue;
            }

            var resourcesResult = BuildResources(
                model, candidate, options, asyncDisposableInterface, disposableInterface,
                scopeNamesByMember, tempCountersByMember,
                dualImplInfoEmitted, diagnostics,
                ref typeSitesRejected, ref duckTypedDeferred);

            if (resourcesResult == null)
            {
                // 资源被判 1841/1842/1845：站点原样保留，不做部分改写。
                rejected.Add(candidate);
                continue;
            }

            var isStatementForm = candidate is UsingStatementSyntax;
            sites.Add(new AwaitUsingSite(candidate, isStatementForm, resourcesResult));
            if (isStatementForm)
                statementsAccepted++;
            else
                declarationsAccepted++;
            guardsPlanned += resourcesResult.Count(r => r.IsReferenceType || r.IsNullableValueType);
        }

        var typeSitesMapped = CountAsyncDisposableTypeSites(model, root, asyncDisposableInterface);

        return new AsyncStreamAnalysisResult(
            diagnostics,
            sites,
            rejected,
            baselineExempt,
            statementsAccepted,
            declarationsAccepted,
            guardsPlanned,
            typeSitesMapped,
            typeSitesRejected,
            duckTypedDeferred);
    }

    // ── 站点与资源构建 ──

    private static IEnumerable<StatementSyntax> EnumerateAwaitUsingNodes(CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case UsingStatementSyntax usingStatement
                    when usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    yield return usingStatement;
                    break;
                case LocalDeclarationStatementSyntax local
                    when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
                         && local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                    yield return local;
                    break;
            }
        }
    }

    private static IReadOnlyList<AwaitUsingResource>? BuildResources(
        SemanticModel model,
        StatementSyntax candidate,
        AsyncNormalizeOptions options,
        INamedTypeSymbol? asyncDisposableInterface,
        INamedTypeSymbol disposableInterface,
        Dictionary<SyntaxNode, HashSet<string>> scopeNamesByMember,
        Dictionary<SyntaxNode, int> tempCountersByMember,
        HashSet<string> dualImplInfoEmitted,
        List<AsyncNormalizeDiagnostic> diagnostics,
        ref int typeSitesRejected,
        ref int duckTypedDeferred)
    {
        var declarators = new List<(VariableDeclaratorSyntax? Declarator, TypeSyntax? TypeSyntax,
            ExpressionSyntax? Expression, ITypeSymbol? Type)>();

        switch (candidate)
        {
            case UsingStatementSyntax { Declaration: { } declaration }:
                foreach (var declarator in declaration.Variables)
                {
                    var type = (model.GetDeclaredSymbol(declarator) as ILocalSymbol)?.Type;
                    declarators.Add((declarator, declaration.Type,
                        declarator.Initializer?.Value, type));
                }
                break;

            case UsingStatementSyntax { Expression: { } expression }:
                var expressionType = model.GetTypeInfo(expression).Type
                    ?? model.GetTypeInfo(expression).ConvertedType;
                declarators.Add((null, null, expression, expressionType));
                break;

            case LocalDeclarationStatementSyntax localDeclaration:
                foreach (var declarator in localDeclaration.Declaration.Variables)
                {
                    var type = (model.GetDeclaredSymbol(declarator) as ILocalSymbol)?.Type;
                    declarators.Add((declarator, localDeclaration.Declaration.Type,
                        declarator.Initializer?.Value, type));
                }
                break;

            default:
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                    "unrecognized 'await using' site shape; the node is left unchanged."));
                return null;
        }

        if (declarators.Count == 0
            || declarators.Any(d => d.Expression == null || d.Type is not { } type))
        {
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                "'await using' resource could not be bound to a type or has no initializer; " +
                "the site is left unchanged."));
            return null;
        }

        var resources = new List<AwaitUsingResource>(declarators.Count);
        var siteRejected = false;

        foreach (var (declarator, declaredTypeSyntax, expression, type) in declarators)
        {
            var resourceType = type!;
            var resourceExpression = expression!;

            // ① ref struct（含其鸭子型 DisposeAsync）：v1 不吞，CS2J1842。
            if (AsyncTypeResolver.IsRefLike(resourceType))
            {
                duckTypedDeferred++;
                siteRejected = true;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1842, candidate,
                    $"'await using' resource type '{resourceType.ToDisplayString()}' is a ref struct " +
                    "with pattern-based async disposal; ref struct / pattern-based (duck-typed) " +
                    "'DisposeAsync' is not supported in v1. Only resources implementing " +
                    "'System.IAsyncDisposable' are normalized."));
                continue;
            }

            // ② 接口资源：选项关 → CS2J1841；双实现 → Info 一次，仍绑 DisposeAsync（D8）。
            if (AsyncTypeResolver.IsAsyncDisposable(resourceType, asyncDisposableInterface))
            {
                if (!options.EnableAwaitUsing)
                {
                    typeSitesRejected++;
                    siteRejected = true;
                    diagnostics.Add(DiagnosticAt(
                        AsyncNormalizeDiagnosticCode.CS2J1841, candidate,
                        $"'await using' resource of type '{resourceType.ToDisplayString()}' cannot " +
                        "be normalized because the EnableAwaitUsing option is disabled. Enable the " +
                        "option, or wait for the Java-side IAsyncDisposable bridge."));
                    continue;
                }

                RecordDualImplementationInfoOnce(
                    resourceType, disposableInterface, candidate, dualImplInfoEmitted, diagnostics);

                resources.Add(CreateResource(
                    declarator, declaredTypeSyntax, resourceExpression, resourceType,
                    candidate, scopeNamesByMember, tempCountersByMember));
                continue;
            }

            // ③ 鸭子型（无接口、有可绑定 DisposeAsync）：CS2J1842。
            if (AsyncTypeResolver.IsPatternBasedAsyncDisposable(resourceType))
            {
                duckTypedDeferred++;
                siteRejected = true;
                diagnostics.Add(DiagnosticAt(
                    AsyncNormalizeDiagnosticCode.CS2J1842, candidate,
                    $"'await using' resource type '{resourceType.ToDisplayString()}' only provides a " +
                    "pattern-based (duck-typed) 'DisposeAsync' and does not implement " +
                    "'System.IAsyncDisposable'; pattern-based async disposal is not supported in " +
                    "v1 (implement the interface explicitly)."));
                continue;
            }

            // ④ 既非接口也非模式：基线通常已报（CS1848 族）；走到这里属漏网，防御镜像。
            siteRejected = true;
            diagnostics.Add(DiagnosticAt(
                AsyncNormalizeDiagnosticCode.CS2J1845, candidate,
                $"'await using' resource type '{resourceType.ToDisplayString()}' neither implements " +
                "'System.IAsyncDisposable' nor provides a bindable pattern-based 'DisposeAsync'; " +
                "the site is left unchanged."));
        }

        return siteRejected ? null : resources;
    }

    private static AwaitUsingResource CreateResource(
        VariableDeclaratorSyntax? declarator,
        TypeSyntax? declaredTypeSyntax,
        ExpressionSyntax resourceExpression,
        ITypeSymbol resourceType,
        StatementSyntax site,
        Dictionary<SyntaxNode, HashSet<string>> scopeNamesByMember,
        Dictionary<SyntaxNode, int> tempCountersByMember)
    {
        string name;
        if (declarator != null)
        {
            name = declarator.Identifier.ValueText;
        }
        else
        {
            var scopeMember = GetScopeMember(site);
            var usedNames = GetOrCreateScopeNames(scopeNamesByMember, scopeMember, site);
            var index = tempCountersByMember.TryGetValue(scopeMember, out var current) ? current : 0;
            tempCountersByMember[scopeMember] = index + 1;
            name = AsyncStreamNaming.AllocateResourceTempName(index, usedNames);
        }

        return new AwaitUsingResource(
            declarator,
            declaredTypeSyntax,
            resourceExpression,
            name,
            IsReferenceType: resourceType.IsReferenceType,
            IsNullableValueType: AsyncTypeResolver.IsNullableValueType(resourceType),
            UseInterfaceCast: !AsyncTypeResolver.HasAccessiblePatternDispose(resourceType));
    }

    private static void RecordDualImplementationInfoOnce(
        ITypeSymbol resourceType,
        INamedTypeSymbol disposableInterface,
        StatementSyntax site,
        HashSet<string> infoEmitted,
        List<AsyncNormalizeDiagnostic> diagnostics)
    {
        if (!AsyncTypeResolver.ImplementsSyncDisposable(resourceType, disposableInterface))
            return;

        var display = resourceType.OriginalDefinition.ToDisplayString();
        if (!infoEmitted.Add(display))
            return;

        var line = site.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Info,
            AsyncNormalizeDiagnosticCode.CS2J1830,
            $"Type '{display}' implements both System.IDisposable and System.IAsyncDisposable; " +
            "'await using' binds 'DisposeAsync' (sync 'using' still binds 'Dispose').",
            Line: line));
    }

    // ── IAsyncDisposable 类型使用点（非语句位放行，零改写，设计 §2.2 归宿矩阵） ──

    private static int CountAsyncDisposableTypeSites(
        SemanticModel model,
        CompilationUnitSyntax root,
        INamedTypeSymbol? asyncDisposableInterface)
    {
        if (asyncDisposableInterface is null)
            return 0;

        var count = 0;
        var comparer = SymbolEqualityComparer.Default;

        bool IsExactAsyncDisposable(ITypeSymbol? type)
            => type != null && comparer.Equals(type, asyncDisposableInterface);

        // 形参（方法/构造/属性访问器/索引器/lambda…，去重在符号层天然一次）。
        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (model.GetDeclaredSymbol(parameter) is IParameterSymbol symbol
                && IsExactAsyncDisposable(symbol.Type))
            {
                count++;
            }
        }

        // 字段。
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var declarator in field.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(declarator) is IFieldSymbol symbol
                    && IsExactAsyncDisposable(symbol.Type))
                {
                    count++;
                }
            }
        }

        // 属性。
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(property) is IPropertySymbol symbol
                && IsExactAsyncDisposable(symbol.Type))
            {
                count++;
            }
        }

        // 方法 / 局部函数返回类型。
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(method) is IMethodSymbol symbol
                && IsExactAsyncDisposable(symbol.ReturnType))
            {
                count++;
            }
        }

        foreach (var localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (model.GetDeclaredSymbol(localFunction) is IMethodSymbol symbol
                && IsExactAsyncDisposable(symbol.ReturnType))
            {
                count++;
            }
        }

        // 局部（排除 using 声明资源与 using 语句内声明资源——那些是语句位）。
        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                continue;
            foreach (var declarator in local.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(declarator) is ILocalSymbol symbol
                    && IsExactAsyncDisposable(symbol.Type))
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ── 作用域 / 基线快照 ──

    private static SyntaxNode GetScopeMember(StatementSyntax site)
    {
        for (var node = site.Parent; node != null; node = node.Parent)
        {
            switch (node)
            {
                case MethodDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                case AccessorDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case ConversionOperatorDeclarationSyntax:
                case DestructorDeclarationSyntax:
                    return node;
                case CompilationUnitSyntax:
                    return node;
            }
        }

        return site.SyntaxTree.GetRoot();
    }

    private static HashSet<string> GetOrCreateScopeNames(
        Dictionary<SyntaxNode, HashSet<string>> cache,
        SyntaxNode scopeMember,
        StatementSyntax site)
    {
        if (cache.TryGetValue(scopeMember, out var existing))
            return existing;

        // CollectScopeNames 自身按站点祖先解析成员作用域，scopeMember 仅作缓存键。
        var used = AsyncStreamNaming.CollectScopeNames(site);
        cache[scopeMember] = used;
        return used;
    }

    private static List<(SyntaxTree Tree, TextSpan Span)> CollectBaselineErrors(SemanticModel model)
    {
        var result = new List<(SyntaxTree, TextSpan)>();
        foreach (var diagnostic in model.Compilation.GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error) continue;
            if (diagnostic.Location.SourceTree is not { } sourceTree) continue;
            result.Add((sourceTree, diagnostic.Location.SourceSpan));
        }
        return result;
    }

    private static bool BaselineContains(
        List<(SyntaxTree Tree, TextSpan Span)> baselineErrors,
        SyntaxTree tree,
        TextSpan outerSpan)
        => baselineErrors.Any(e =>
            e.Tree == tree
            && e.Span.Start >= outerSpan.Start
            && e.Span.End <= outerSpan.End);

    private static AsyncNormalizeDiagnostic DiagnosticAt(
        AsyncNormalizeDiagnosticCode code,
        SyntaxNode node,
        string message)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Error,
            code,
            message,
            Line: line);
    }
}
