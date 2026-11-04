using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.StackAllocLowering;

/// <summary>
/// Independent read-only post-pass <c>stackalloc-lowering</c> (pass stage Abi,
/// runs right after <c>unsafe-ffm</c>). It scans a whole file with its
/// <see cref="SemanticModel"/> and lowers only stackalloc shapes that Java can
/// never accept:
/// <list type="bullet">
/// <item>Span/ReadOnlySpan targets (direct initializer or conditional operand):
/// <c>stackalloc T[n]</c> → <c>new T[n]</c>, <c>stackalloc T[]{{...}}</c> →
/// <c>new T[]{{...}}</c>. The Span declaration itself is kept;</item>
/// <item>the provable bare-pointer shape
/// <c>T* ptr = stackalloc T[n]; ... new Span&lt;T&gt;(ptr, n)</c> where the
/// pointer symbol is used only as that constructor's pointer argument (or not
/// used at all): the declaration becomes <c>T[] ptr = new T[n]</c> and the
/// construction becomes <c>new Span&lt;T&gt;(ptr, 0, n)</c>.</item>
/// </list>
/// Every other bare-pointer stackalloc site (arithmetic, dereference, passing,
/// field store, return, unprovable) is preserved verbatim and reported as
/// SL001; <c>normalization-audit</c> keeps reporting NORM-STACKALLOC for it.
/// After all stackalloc rewrites the pass also unwraps <c>unsafe { ... }</c>
/// blocks whose rewritten body no longer contains any unsafe-only syntax
/// (pointer/function-pointer types, <c>fixed</c>, stackalloc, address-of /
/// pointer indirection, or a nested unsafe block that must stay).
/// Clean files are returned byte-for-byte and re-running the pass on its own
/// output is a no-op (idempotent).
/// </summary>
public sealed class StackAllocLoweringPreprocessor
{
    private readonly StackAllocLoweringOptions _options;

    public StackAllocLoweringPreprocessor(StackAllocLoweringOptions? options = null)
    {
        _options = options ?? new StackAllocLoweringOptions();
    }

    /// <summary>
    /// Rewrite one file using the caller's whole-tree semantic model.
    /// </summary>
    public StackAllocLoweringResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new StackAllocLoweringStatistics();
        var diagnostics = new List<StackAllocLoweringDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j-generated support/registry artifacts are not user stackalloc
        // surface (same convention as unsafe-ffm / make-readonly).
        if (IsCs2jGeneratedSupportFile(filePath))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var replacements = new List<(SyntaxNode Original, SyntaxNode Replacement)>();
        CollectReplacements(root, semanticModel, filePath, statistics, diagnostics, replacements);

        // 1) stackalloc 改写。
        var newRoot = root;
        if (replacements.Count > 0)
        {
            var map = new Dictionary<SyntaxNode, SyntaxNode>(
                replacements.Count, ReferenceEqualityComparer.Instance);
            foreach (var pair in replacements)
            {
                map[pair.Original] = pair.Replacement;
            }

            newRoot = (CompilationUnitSyntax)root.ReplaceNodes(
                map.Keys, (original, _) => map[original]);
        }

        // 2) 所有 stackalloc 改写完成后，再剥离已不含 unsafe 构造的 unsafe 块。
        newRoot = (CompilationUnitSyntax)new UnsafeBlockUnwrapper(statistics).Visit(newRoot)!;

        if (newRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new StackAllocLoweringDiagnostic(
                StackAllocLoweringSeverity.Warning,
                StackAllocLoweringDiagnosticCode.SL010,
                "stackalloc-lowering rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new StackAllocLoweringResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    // ---- site collection ----

    private void CollectReplacements(
        CompilationUnitSyntax root,
        SemanticModel model,
        string filePath,
        StackAllocLoweringStatistics statistics,
        List<StackAllocLoweringDiagnostic> diagnostics,
        List<(SyntaxNode Original, SyntaxNode Replacement)> replacements)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is StackAllocArrayCreationExpressionSyntax explicitNode)
            {
                AnalyzeSite(explicitNode, model, filePath, statistics, diagnostics, replacements);
            }
            else if (node is ImplicitStackAllocArrayCreationExpressionSyntax implicitNode)
            {
                AnalyzeSite(implicitNode, model, filePath, statistics, diagnostics, replacements);
            }
        }
    }

    private void AnalyzeSite(
        ExpressionSyntax node,
        SemanticModel model,
        string filePath,
        StackAllocLoweringStatistics statistics,
        List<StackAllocLoweringDiagnostic> diagnostics,
        List<(SyntaxNode Original, SyntaxNode Replacement)> replacements)
    {
        var siteType = ResolveSiteType(node, model);

        if (siteType is IPointerTypeSymbol)
        {
            AnalyzePointerSite(node, model, filePath, statistics, diagnostics, replacements);
            return;
        }

        if (IsSpanLike(siteType, out var spanElement) && spanElement != null)
        {
            if (!_options.LowerSpanTargets)
            {
                return;
            }

            var creation = BuildSpanTargetArray(node, spanElement);
            if (creation == null)
            {
                Reject(node, "the stackalloc type shape is not a supported array form.",
                    filePath, statistics, diagnostics);
                return;
            }

            creation = MaybeWrapForConditional(node, creation, spanElement, model);

            replacements.Add((node, creation));
            statistics.SpanTargetSitesLowered++;
            return;
        }

        Reject(node,
            "the stackalloc target is neither a Span/ReadOnlySpan nor a provable pointer local.",
            filePath, statistics, diagnostics);
    }

    /// <summary>
    /// Site type resolution: the Roslyn converted type, with defensive fallbacks
    /// for target-typed conditional operands and Span-typed initializer targets.
    /// </summary>
    private static ITypeSymbol? ResolveSiteType(ExpressionSyntax node, SemanticModel model)
    {
        var info = model.GetTypeInfo(node);
        var siteType = info.ConvertedType ?? info.Type;
        if (siteType is IPointerTypeSymbol || IsSpanLike(siteType, out _))
        {
            return siteType;
        }

        var conditional = FindConditional(node);
        if (conditional != null)
        {
            var conditionalInfo = model.GetTypeInfo(conditional);
            var conditionalType = conditionalInfo.ConvertedType ?? conditionalInfo.Type;
            if (IsSpanLike(conditionalType, out _))
            {
                return conditionalType;
            }
        }

        var declarator = FindInitializerDeclarator(node);
        if (declarator != null
            && model.GetDeclaredSymbol(declarator) is ILocalSymbol local
            && IsSpanLike(local.Type, out _))
        {
            return local.Type;
        }

        return siteType;
    }

    // ---- Span/ReadOnlySpan targets ----

    private static ExpressionSyntax? BuildSpanTargetArray(
        ExpressionSyntax node, ITypeSymbol element)
    {
        switch (node)
        {
            case StackAllocArrayCreationExpressionSyntax explicitNode:
                if (explicitNode.Type is not ArrayTypeSyntax arrayType)
                {
                    return null;
                }

                return ArrayCreationExpression(arrayType.WithoutTrivia())
                    .WithInitializer(explicitNode.Initializer)
                    .WithLeadingTrivia(explicitNode.GetLeadingTrivia());

            case ImplicitStackAllocArrayCreationExpressionSyntax implicitNode:
                var elementSyntax = ParseTypeName(element.ToDisplayString());
                var implicitArrayType = ArrayType(elementSyntax)
                    .WithRankSpecifiers(SingletonList(ArrayRankSpecifier()));
                return ArrayCreationExpression(implicitArrayType)
                    .WithInitializer(implicitNode.Initializer)
                    .WithLeadingTrivia(implicitNode.GetLeadingTrivia());

            default:
                return null;
        }
    }

    /// <summary>
    /// Target-typed conditionals convert an array branch back to Span implicitly.
    /// The wrapper is only emitted when the semantic model proves the array form
    /// is not implicitly convertible to the conditional's Span result type.
    /// </summary>
    private static ExpressionSyntax MaybeWrapForConditional(
        ExpressionSyntax node,
        ExpressionSyntax creation,
        ITypeSymbol element,
        SemanticModel model)
    {
        var conditional = FindConditional(node);
        if (conditional == null)
        {
            return creation;
        }

        var conditionalInfo = model.GetTypeInfo(conditional);
        var conditionalType = conditionalInfo.ConvertedType ?? conditionalInfo.Type;
        if (!IsSpanLike(conditionalType, out _) || conditionalType == null)
        {
            return creation;
        }

        var arrayType = model.Compilation.CreateArrayTypeSymbol(element);
        var conversion = model.Compilation.ClassifyCommonConversion(arrayType, conditionalType);
        if (conversion.IsImplicit)
        {
            return creation;
        }

        // Fallback safe form: MemoryExtensions.AsSpan(new T[n]) (Span<T>
        // implicitly converts to ReadOnlySpan<T> when that is the target).
        return InvocationExpression(
                ParseExpression("global::System.MemoryExtensions.AsSpan"),
                ArgumentList(SingletonSeparatedList(Argument(creation.WithoutTrivia()))))
            .WithLeadingTrivia(creation.GetLeadingTrivia());
    }

    // ---- bare pointer local + Span construction ----

    private void AnalyzePointerSite(
        ExpressionSyntax node,
        SemanticModel model,
        string filePath,
        StackAllocLoweringStatistics statistics,
        List<StackAllocLoweringDiagnostic> diagnostics,
        List<(SyntaxNode Original, SyntaxNode Replacement)> replacements)
    {
        if (!_options.LowerPointerLocals)
        {
            return;
        }

        if (node is not StackAllocArrayCreationExpressionSyntax explicitNode
            || explicitNode.Type is not ArrayTypeSyntax arrayType)
        {
            Reject(node,
                "an implicit or non-array stackalloc behind a bare pointer target cannot be " +
                "lowered safely.", filePath, statistics, diagnostics);
            return;
        }

        var declarator = FindInitializerDeclarator(node);
        if (declarator == null
            || declarator.Parent is not VariableDeclarationSyntax declaration
            || declaration.Parent is not LocalDeclarationStatementSyntax statement
            || declaration.Variables.Count != 1
            || declaration.Type is not PointerTypeSyntax)
        {
            Reject(node,
                "the bare-pointer stackalloc is not a single explicit pointer local declaration.",
                filePath, statistics, diagnostics);
            return;
        }

        if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol
            {
                Type: IPointerTypeSymbol pointerType
            } pointerLocal)
        {
            Reject(node,
                "the bare-pointer stackalloc declaration does not bind to a pointer local.",
                filePath, statistics, diagnostics);
            return;
        }

        var elementSyntaxType = model.GetTypeInfo(arrayType.ElementType).Type;
        if (elementSyntaxType == null
            || !SymbolEqualityComparer.Default.Equals(pointerType.PointedAtType, elementSyntaxType))
        {
            Reject(node,
                "the pointer pointee does not match the stackalloc element type.",
                filePath, statistics, diagnostics);
            return;
        }

        var scope = FindScope(declarator);
        if (scope == null)
        {
            Reject(node, "the pointer local has no enclosing method/function scope.",
                filePath, statistics, diagnostics);
            return;
        }

        var constructions = new List<ObjectCreationExpressionSyntax>();
        foreach (var reference in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(reference).Symbol, pointerLocal))
            {
                continue;
            }

            if (!TryMatchSpanConstruction(reference, model, pointerType.PointedAtType,
                    out var construction))
            {
                Reject(node,
                    "the pointer local is used outside 'new Span<T>(ptr, length)' / " +
                    "'new ReadOnlySpan<T>(ptr, length)'.", filePath, statistics, diagnostics);
                return;
            }

            constructions.Add(construction!);
        }

        // Declaration: T* ptr = stackalloc T[n];  ->  T[] ptr = new T[n];
        var arrayCreation = ArrayCreationExpression(arrayType.WithoutTrivia())
            .WithInitializer(explicitNode.Initializer)
            .WithLeadingTrivia(explicitNode.GetLeadingTrivia());

        var newDeclarationType = ArrayType(arrayType.ElementType.WithoutTrivia())
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier()));

        var newDeclarator = declarator.WithInitializer(
            declarator.Initializer!.WithValue(arrayCreation));
        var newDeclaration = declaration
            .WithType(newDeclarationType)
            .WithVariables(SingletonSeparatedList(newDeclarator));
        var newStatement = statement.WithDeclaration(newDeclaration);

        // new Span<T>(ptr, length) -> new Span<T>(ptr, 0, length)
        foreach (var construction in constructions)
        {
            var arguments = construction.ArgumentList.Arguments;
            var newArguments = SeparatedList(new[]
            {
                Argument(arguments[0].Expression),
                Argument(LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, Literal(0))),
                Argument(arguments[1].Expression),
            });
            var newConstruction = construction.WithArgumentList(
                construction.ArgumentList.WithArguments(newArguments));
            replacements.Add((construction, newConstruction));
            statistics.SpanConstructionsRewritten++;
        }

        replacements.Add((statement, newStatement));
        statistics.PointerLocalSitesLowered++;
    }

    /// <summary>
    /// True only for <c>new Span&lt;T&gt;(ptr, length)</c> /
    /// <c>new ReadOnlySpan&lt;T&gt;(ptr, length)</c> where this identifier is
    /// the first and the pointer pointee matches the span element.
    /// </summary>
    private static bool TryMatchSpanConstruction(
        IdentifierNameSyntax reference,
        SemanticModel model,
        ITypeSymbol pointee,
        out ObjectCreationExpressionSyntax? construction)
    {
        construction = null;

        if (reference.Parent is not ArgumentSyntax argument
            || argument.Expression != reference
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Arguments.Count != 2
            || argumentList.Arguments[0] != argument
            || argumentList.Parent is not ObjectCreationExpressionSyntax creation)
        {
            return false;
        }

        var createdType = model.GetTypeInfo(creation).Type;
        if (!IsSpanLike(createdType, out var element) || element == null)
        {
            return false;
        }

        if (!SymbolEqualityComparer.Default.Equals(element, pointee))
        {
            return false;
        }

        construction = creation;
        return true;
    }

    private static SyntaxNode? FindScope(SyntaxNode? node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is LocalFunctionStatementSyntax
                or BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax
                or AnonymousFunctionExpressionSyntax)
            {
                return current;
            }
        }

        return null;
    }

    // ---- shared helpers ----

    /// <summary>
    /// Find the local declarator whose initializer is exactly this expression
    /// (unwrapping parentheses/checked); null for non-declaration initializers.
    /// </summary>
    private static VariableDeclaratorSyntax? FindInitializerDeclarator(ExpressionSyntax node)
    {
        SyntaxNode? current = node;
        while (current.Parent is ParenthesizedExpressionSyntax or CheckedExpressionSyntax)
        {
            current = current.Parent;
        }

        return current.Parent is EqualsValueClauseSyntax
        {
            Parent: VariableDeclaratorSyntax declarator
        }
            ? declarator
            : null;
    }

    /// <summary>
    /// The conditional expression directly enclosing this stackalloc operand
    /// (through parentheses/checked wrappers), if any.
    /// </summary>
    private static ConditionalExpressionSyntax? FindConditional(SyntaxNode node)
    {
        SyntaxNode current = node;
        while (current.Parent is ParenthesizedExpressionSyntax or CheckedExpressionSyntax)
        {
            current = current.Parent;
        }

        return current.Parent as ConditionalExpressionSyntax;
    }

    /// <summary>System.Span&lt;T&gt;/System.ReadOnlySpan&lt;T&gt; 判定。</summary>
    private static bool IsSpanLike(ITypeSymbol? type, out ITypeSymbol? element)
    {
        element = null;
        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && named.TypeArguments.Length == 1)
        {
            var definition = named.OriginalDefinition.ToDisplayString();
            if (definition is "System.Span<T>" or "System.ReadOnlySpan<T>")
            {
                element = named.TypeArguments[0];
                return true;
            }
        }

        return false;
    }

    private static bool IsCs2jGeneratedSupportFile(string? filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
               && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static void Reject(
        ExpressionSyntax node,
        string reason,
        string filePath,
        StackAllocLoweringStatistics statistics,
        List<StackAllocLoweringDiagnostic> diagnostics)
    {
        var line = 1;
        var column = 1;
        try
        {
            var span = node.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // Location unavailable: fall back to (1,1).
        }

        statistics.RejectedSites++;
        diagnostics.Add(new StackAllocLoweringDiagnostic(
            StackAllocLoweringSeverity.Warning,
            StackAllocLoweringDiagnosticCode.SL001,
            "stackalloc site preserved: " + reason,
            filePath, line, column));
    }

    private static StackAllocLoweringResult Rollback(
        string sourceCode,
        StackAllocLoweringStatistics statistics,
        IReadOnlyList<StackAllocLoweringDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };

    /// <summary>整树格式化 → 候选；每个候选重新解析零 Error 才返回。</summary>
    private static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                candidates.Add(Formatter.Format(root, workspace).ToFullString());
            }
            catch
            {
                // Formatting failure is not fatal; the raw candidate still runs.
            }
        }

        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics()
                    .Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }

    /// <summary>
    /// 剥除 <see cref="UnsafeStatementSyntax"/>：仅当（后序处理后的）块内已不含
    /// 任何需要 unsafe 的语法时，才把 <c>unsafe { ... }</c> 退化为普通
    /// <c>{ ... }</c>。指针/函数指针类型、<c>fixed</c>、显式/隐式 stackalloc、
    /// 取址/指针解引用，以及仍需保留的内层 unsafe 块均视为需要 unsafe；
    /// 不依据 unsafe 关键字本身判断。后序确保内层先处理，外层随后可一并剥离。
    /// </summary>
    private sealed class UnsafeBlockUnwrapper : CSharpSyntaxRewriter
    {
        private readonly StackAllocLoweringStatistics _statistics;

        public UnsafeBlockUnwrapper(StackAllocLoweringStatistics statistics)
        {
            _statistics = statistics;
        }

        public override SyntaxNode? VisitUnsafeStatement(UnsafeStatementSyntax node)
        {
            var visited = (UnsafeStatementSyntax)base.VisitUnsafeStatement(node)!;
            if (ContainsUnsafeOnlySyntax(visited.Block))
            {
                return visited;
            }

            _statistics.UnsafeBlocksUnwrapped++;

            // unsafe keyword 的 trivia（缩进/换行/注释）并入 Block 前导，
            // 保持源码排版；格式化阶段会统一整理。
            var block = visited.Block;
            var mergedLeading = visited.UnsafeKeyword.LeadingTrivia
                .Concat(visited.UnsafeKeyword.TrailingTrivia)
                .Concat(block.GetLeadingTrivia());
            return block.WithLeadingTrivia(mergedLeading);
        }

        private static bool ContainsUnsafeOnlySyntax(SyntaxNode block)
        {
            foreach (var descendant in block.DescendantNodes())
            {
                switch (descendant)
                {
                    case PointerTypeSyntax:
                    case FunctionPointerTypeSyntax:
                    case FixedStatementSyntax:
                    case StackAllocArrayCreationExpressionSyntax:
                    case ImplicitStackAllocArrayCreationExpressionSyntax:
                    case UnsafeStatementSyntax:
                        return true;
                    case PrefixUnaryExpressionSyntax prefix
                        when prefix.IsKind(SyntaxKind.AddressOfExpression)
                             || prefix.IsKind(SyntaxKind.PointerIndirectionExpression):
                        return true;
                }
            }

            return false;
        }
    }
}
