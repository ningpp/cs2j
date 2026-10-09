using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// M3 (C44 G3): 在迭代器状态机分析之前，把迭代器方法体内的
/// using 语句 / using 声明（含 pattern-based Dispose、多资源、堆叠）
/// 归一为 try/finally，把 lock 语句归一为 Monitor.Enter/Exit 的
/// Roslyn 规范 taken 形态。归一后的树交给既有区域表/局部提升通道，
/// 资源与 lockTaken 跨 yield 自动提升为状态机字段。
///
/// 边界（与 C44 设计 §2.4 G3 一致）：
/// - await using 保留（归 C38 async streams），发 CS2J0402；
/// - ref struct 资源（含 Span 族）保留，发 CS2J0403（跨 yield 不可提升；
///   非迭代器上下文的 ref struct using 归 C06 R9/UF055，本归一器不触碰）；
/// - 非迭代器方法体内的 using/lock 一律不动（同步 using 由主转换器内联处理）。
/// </summary>
internal sealed class IteratorResourceNormalizer : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Compilation _compilation;
    private readonly INamedTypeSymbol _idisposable;
    private readonly List<IteratorLoweringDiagnostic> _diagnostics = new();

    private int _resourceCounter;
    private int _lockCounter;
    private readonly HashSet<string> _syntheticNames = new(StringComparer.Ordinal);

    /// <summary>true 仅当当前访问节点位于含 yield 的方法体内。</summary>
    private bool _inIteratorBody;

    public int UsingRegionsNormalized { get; private set; }
    public int UsingResourcesHoisted { get; private set; }
    public int LocksNormalized { get; private set; }
    public IReadOnlyList<IteratorLoweringDiagnostic> Diagnostics => _diagnostics;

    public IteratorResourceNormalizer(SemanticModel model)
    {
        _model = model;
        _compilation = model.Compilation;
        _idisposable = _compilation.GetSpecialType(SpecialType.System_IDisposable);
    }

    public CompilationUnitSyntax Normalize(CompilationUnitSyntax root)
        => (CompilationUnitSyntax)Visit(root);

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var containsYield = node.Body != null
            && node.Body.DescendantNodes().Any(n =>
                n.IsKind(YieldReturnStatement) || n.IsKind(YieldBreakStatement));
        var previous = _inIteratorBody;
        _inIteratorBody = containsYield;
        try
        {
            return base.VisitMethodDeclaration(node);
        }
        finally
        {
            _inIteratorBody = previous;
        }
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        // 自底向上：先归一内层块/语句。
        var rewritten = (BlockSyntax)base.VisitBlock(node)!;
        if (!_inIteratorBody) return rewritten;

        // using 声明：从块尾向前，把每个声明点之后的语句包进 try/finally，
        // 多个声明自然形成声明序相反的 LIFO 嵌套。
        var statements = rewritten.Statements.ToList();
        var changed = false;
        for (var i = statements.Count - 1; i >= 0; i--)
        {
            if (statements[i] is not LocalDeclarationStatementSyntax declaration
                || declaration.UsingKeyword.RawKind == 0)
            {
                continue;
            }

            if (declaration.AwaitKeyword.RawKind != 0)
            {
                // await using 声明：保留，交 C38。
                AddDiagnostic(declaration, IteratorLoweringDiagnosticCode.CS2J0402,
                    "await using inside an iterator body is owned by async streams normalization (C38) and cannot be lowered synchronously.");
                continue;
            }

            var resourceTypes = GetDeclaratorTypes(declaration.Declaration);
            if (resourceTypes.Any(t => t.IsRefLikeType))
            {
                AddDiagnostic(declaration, IteratorLoweringDiagnosticCode.CS2J0403,
                    "a using declaration whose resource is a ref struct cannot be hoisted across a yield boundary.");
                continue;
            }

            var trailing = statements.Skip(i + 1).ToList();
            StatementSyntax expansion = Block(trailing);
            for (var d = declaration.Declaration.Variables.Count - 1; d >= 0; d--)
            {
                var variable = declaration.Declaration.Variables[d];
                var type = resourceTypes[d];
                var single = LocalDeclarationStatement(
                    VariableDeclaration(declaration.Declaration.Type,
                        SingletonSeparatedList(variable.WithoutTrivia())));
                var release = BuildDisposeStatement(variable.Identifier.ValueText, type, declaration.SpanStart);
                var guarded = TryStatement()
                    .WithBlock(Block(expansion))
                    .WithFinally(FinallyClause(Block(release)));
                expansion = Block(single, guarded);
                UsingResourcesHoisted++;
            }
            UsingRegionsNormalized++;
            statements.RemoveRange(i, statements.Count - i);
            statements.Add(expansion);
            changed = true;
        }

        return changed ? rewritten.WithStatements(List(statements)) : rewritten;
    }

    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        // 所有语义查询必须在子树重写之前对原始树完成：重写会产生
        // 尚未挂回语法树的新节点，对其调用 SemanticModel 会抛
        // ArgumentException（语法节点不在语法树中）。
        var skipCode = (IteratorLoweringDiagnosticCode?)null;
        var skipMessage = "";
        var resources = new List<(string Name, ITypeSymbol Type, bool IsTemp)>();
        ITypeSymbol? expressionResourceType = null;

        if (node.AwaitKeyword.RawKind != 0)
        {
            skipCode = IteratorLoweringDiagnosticCode.CS2J0402;
            skipMessage = "await using inside an iterator body is owned by async streams normalization (C38) and cannot be lowered synchronously.";
        }
        else if (node.Declaration != null)
        {
            var types = GetDeclaratorTypes(node.Declaration);
            for (var i = 0; i < node.Declaration.Variables.Count; i++)
            {
                resources.Add((node.Declaration.Variables[i].Identifier.ValueText, types[i], false));
            }
            if (resources.Any(r => r.Type.IsRefLikeType))
            {
                skipCode = IteratorLoweringDiagnosticCode.CS2J0403;
                skipMessage = "a using statement whose resource is a ref struct cannot be hoisted across a yield boundary.";
            }
        }
        else if (node.Expression != null)
        {
            expressionResourceType = _model.GetTypeInfo(node.Expression).ConvertedType
                ?? _model.GetTypeInfo(node.Expression).Type;
            if (expressionResourceType == null)
            {
                skipCode = IteratorLoweringDiagnosticCode.CS2J0402;
                skipMessage = "the using resource type cannot be statically resolved.";
            }
            else if (expressionResourceType.IsRefLikeType)
            {
                skipCode = IteratorLoweringDiagnosticCode.CS2J0403;
                skipMessage = "a using expression whose resource is a ref struct cannot be hoisted across a yield boundary.";
            }
            else if (node.Expression is not IdentifierNameSyntax)
            {
                // 非标识符表达式只求值一次：合成临时变量（重写后用新表达式节点构造声明）。
                resources.Add((NewSyntheticName("_cs2jUsingResource"), expressionResourceType, true));
            }
            else
            {
                resources.Add((((IdentifierNameSyntax)node.Expression).Identifier.ValueText,
                    expressionResourceType, false));
            }
        }

        // 先归一内层（body 中的 using 声明/语句/lock）。
        var rewritten = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
        if (!_inIteratorBody) return rewritten;

        if (skipCode != null)
        {
            AddDiagnostic(rewritten, skipCode.Value, skipMessage);
            return rewritten;
        }
        if (rewritten.Declaration == null && rewritten.Expression == null)
        {
            return rewritten;
        }

        var body = AsBlock(rewritten.Statement);

        StatementSyntax? tempDeclaration = null;
        if (rewritten.Expression != null && expressionResourceType != null
            && rewritten.Expression is not IdentifierNameSyntax)
        {
            var tempName = resources[0].Name;
            tempDeclaration = LocalDeclarationStatement(
                VariableDeclaration(IdentifierName("var"),
                    SingletonSeparatedList(VariableDeclarator(tempName)
                        .WithInitializer(EqualsValueClause(rewritten.Expression.WithoutTrivia())))));
        }

        // 资源按声明序嵌套：外层先获取后释放（D4/D5）。
        StatementSyntax current = body;
        for (var i = resources.Count - 1; i >= 0; i--)
        {
            var (name, type, isTemp) = resources[i];
            var release = BuildDisposeStatement(name, type, node.SpanStart);
            var guarded = TryStatement()
                .WithBlock(Block(current))
                .WithFinally(FinallyClause(Block(release)));
            if (!isTemp && rewritten.Declaration != null)
            {
                var single = LocalDeclarationStatement(
                    VariableDeclaration(rewritten.Declaration.Type,
                        SingletonSeparatedList(rewritten.Declaration.Variables[i].WithoutTrivia())));
                current = Block(single, guarded);
            }
            else
            {
                current = guarded;
            }
            UsingResourcesHoisted++;
        }
        if (tempDeclaration != null)
        {
            current = Block(tempDeclaration, current);
        }
        UsingRegionsNormalized++;
        return current;
    }

    public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
    {
        // 表达式文本在重写前捕获（重写只归一内层，表达式本身不变形）。
        var lockExpressionText = node.Expression.ToFullString().Trim();
        var rewritten = (LockStatementSyntax)base.VisitLockStatement(node)!;
        if (!_inIteratorBody) return rewritten;

        var takenName = NewSyntheticName("_cs2jLockTaken");
        var body = AsBlock(rewritten.Statement);

        var takenDeclaration = ParseStatement($"bool {takenName} = false;");
        var enter = ParseStatement(
            $"System.Threading.Monitor.Enter({lockExpressionText}, ref {takenName});");
        var exit = ParseStatement(
            $"if ({takenName}) System.Threading.Monitor.Exit({lockExpressionText});");
        var tryFinally = TryStatement()
            .WithBlock(body)
            .WithFinally(FinallyClause(Block(exit)));
        LocksNormalized++;
        return Block(takenDeclaration, enter, tryFinally);
    }

    // ── 资源释放语句合成（C44 D1-D3/D13） ─────────────────────────────

    private StatementSyntax BuildDisposeStatement(string name, ITypeSymbol type, int siteSpanStart)
    {
        ITypeSymbol? nullableUnderlying = null;
        if (type.IsValueType && type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } namedNullable)
        {
            nullableUnderlying = namedNullable.TypeArguments[0];
        }

        if (nullableUnderlying != null)
        {
            var dispose = BuildDirectOrInterfaceDispose($"{name}.Value", nullableUnderlying, siteSpanStart);
            return ParseStatement($"if ({name}.HasValue) {dispose};");
        }

        if (type.IsValueType)
        {
            // 值类型：无 null 守卫；有可访问 Dispose 走直接调用（constrained，不装箱），
            // 仅显式接口实现时经接口槽（此时装箱不可避免）。
            return ParseStatement(BuildDirectOrInterfaceDispose(name, type, siteSpanStart) + ";");
        }

        // 引用类型：null 守卫是 C# using 语义的硬性部分（using(null) 合法）。
        var disposeExpr = HasImplicitDisposeConversion(type)
            ? $"((System.IDisposable){name}).Dispose()"
            : $"{name}.Dispose()";
        return ParseStatement($"if ({name} != null) {disposeExpr};");
    }

    private string BuildDirectOrInterfaceDispose(string accessor, ITypeSymbol type, int siteSpanStart)
    {
        if (HasAccessiblePatternDispose(type, siteSpanStart))
        {
            return $"{accessor}.Dispose()";
        }
        return $"((System.IDisposable){accessor}).Dispose()";
    }

    private bool HasImplicitDisposeConversion(ITypeSymbol type)
        => type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _idisposable))
            || _compilation.ClassifyConversion(type, _idisposable).IsImplicit;

    private bool HasAccessiblePatternDispose(ITypeSymbol type, int siteSpanStart)
    {
        foreach (var member in type.GetMembers("Dispose"))
        {
            if (member is not IMethodSymbol method) continue;
            if (method.Parameters.Length != 0 || !method.ReturnsVoid) continue;
            if (_model.IsAccessible(siteSpanStart, method)) return true;
        }
        // Nullable<T> 的 Value 上的模式方法经 T 自身查找。
        return false;
    }

    private IReadOnlyList<ITypeSymbol> GetDeclaratorTypes(VariableDeclarationSyntax declaration)
    {
        var types = new List<ITypeSymbol>();
        foreach (var variable in declaration.Variables)
        {
            var symbol = _model.GetDeclaredSymbol(variable) as ILocalSymbol;
            var type = symbol?.Type
                ?? (variable.Initializer != null
                    ? _model.GetTypeInfo(variable.Initializer.Value).ConvertedType
                    : null)
                ?? _model.GetTypeInfo(declaration.Type).Type!;
            types.Add(type);
        }
        return types;
    }

    private static BlockSyntax AsBlock(StatementSyntax statement)
        => statement as BlockSyntax ?? Block(statement);

    private string NewSyntheticName(string prefix)
    {
        if (_syntheticNames.Count == 0)
        {
            foreach (var text in ((CompilationUnitSyntax)_model.SyntaxTree.GetRoot())
                .DescendantTokens()
                .Where(t => t.IsKind(IdentifierToken))
                .Select(t => t.ValueText))
            {
                _syntheticNames.Add(text);
            }
        }
        var n = 1;
        string candidate;
        do
        {
            candidate = prefix + "_" + n++;
        }
        while (!_syntheticNames.Add(candidate));
        return candidate;
    }

    private void AddDiagnostic(SyntaxNode site, IteratorLoweringDiagnosticCode code, string message)
    {
        var lineSpan = site.GetLocation().GetLineSpan();
        _diagnostics.Add(new IteratorLoweringDiagnostic(
            IteratorLoweringSeverity.Error,
            code,
            message,
            FilePath: null,
            Line: lineSpan.StartLinePosition.Line + 1));
    }
}
