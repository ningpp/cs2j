using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeRefLowering;

/// <summary>
/// Independent ABI pass <c>unsafe-ref-lowering</c> (stage Abi, runs directly
/// after <c>ref-out-in</c>, before <c>unsigned</c>). It removes the
/// Java-unrepresentable ref-local shape
/// <c>ref T x = ref MemoryMarshal.GetReference(span)</c> together with the
/// <c>Unsafe</c> intrinsic reads that consume it:
/// <list type="bullet">
/// <item><c>Unsafe.ReadUnaligned&lt;U&gt;(ref x)</c> →
/// <c>MemoryMarshal.Read&lt;U&gt;(span)</c>;</item>
/// <item><c>Unsafe.ReadUnaligned&lt;U&gt;(ref Unsafe.Add(ref x, k))</c> →
/// <c>MemoryMarshal.Read&lt;U&gt;(span.Slice(k))</c>;</item>
/// <item><c>Unsafe.Add(ref x, k)</c> used as an ordinary value →
/// <c>span[k]</c>.</item>
/// </list>
/// The span must be a stable identifier / field access (or <c>this.field</c>)
/// and any other reference shape preserves the whole local verbatim with
/// URL001 so <c>normalization-audit</c> keeps reporting NORM-REF-LOCAL.
/// Clean files are returned byte-for-byte and re-running the pass on its own
/// output is a no-op.
/// </summary>
public sealed class UnsafeRefLoweringPreprocessor
{
    private const string MemoryMarshalTypeName = "System.Runtime.InteropServices.MemoryMarshal";
    private const string UnsafeTypeName = "System.Runtime.CompilerServices.Unsafe";

    private static readonly SyntaxAnnotation RemovalAnnotation =
        new("cs2j-unsafe-ref-lowering-remove");

    private readonly UnsafeRefLoweringOptions _options;

    public UnsafeRefLoweringPreprocessor(UnsafeRefLoweringOptions? options = null)
    {
        _options = options ?? new UnsafeRefLoweringOptions();
    }

    /// <summary>Rewrite one file using the caller's whole-tree semantic model.</summary>
    public UnsafeRefLoweringResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new UnsafeRefLoweringStatistics();
        var diagnostics = new List<UnsafeRefLoweringDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j-generated support/registry artifacts are not user ref-local surface.
        if (IsCs2jGeneratedSupportFile(filePath))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var replacements = new List<(SyntaxNode Original, SyntaxNode Replacement)>();

        foreach (var declaration in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            AnalyzeRefLocal(declaration, semanticModel, filePath,
                statistics, diagnostics, replacements);
        }

        if (statistics.RefLocalsLowered == 0)
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var map = new Dictionary<SyntaxNode, SyntaxNode>(
            replacements.Count, ReferenceEqualityComparer.Instance);
        foreach (var pair in replacements)
        {
            map[pair.Original] = pair.Replacement;
        }

        var newRoot = (CompilationUnitSyntax)root.ReplaceNodes(
            map.Keys, (original, _) => map[original]);

        // Drop the annotated ref-local declarations. Statements keep their
        // surrounding trivia; comments on the removed declaration are moved to
        // the next statement (or the closing brace).
        newRoot = (CompilationUnitSyntax)new RemovalRewriter().Visit(newRoot)!;

        var outputCode = Validate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new UnsafeRefLoweringDiagnostic(
                UnsafeRefLoweringSeverity.Warning,
                UnsafeRefLoweringDiagnosticCode.URL010,
                "unsafe-ref-lowering rewrite failed local parse validation; the original " +
                "file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        statistics.FilesTransformed++;
        return new UnsafeRefLoweringResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    // ---- per-declaration analysis ----

    private void AnalyzeRefLocal(
        LocalDeclarationStatementSyntax declaration,
        SemanticModel model,
        string filePath,
        UnsafeRefLoweringStatistics statistics,
        List<UnsafeRefLoweringDiagnostic> diagnostics,
        List<(SyntaxNode Original, SyntaxNode Replacement)> replacements)
    {
        if (declaration.Declaration.Type is not RefTypeSyntax)
            return;

        if (declaration.Declaration.Variables.Count != 1)
            return;

        var declarator = declaration.Declaration.Variables[0];
        var initializer = declarator.Initializer?.Value;
        if (initializer is null)
            return;

        var getReference = UnwrapRefInitializer(initializer);
        if (getReference is null || !IsMemoryMarshalGetReference(getReference, model))
            return; // Not the owner shape; leave it to ref-out-in / other passes.

        if (model.GetDeclaredSymbol(declarator) is not ILocalSymbol local
            || local.RefKind != RefKind.Ref)
        {
            Reject(declaration, "the declaration does not bind to a ref local.",
                filePath, statistics, diagnostics);
            return;
        }

        if (getReference.ArgumentList.Arguments.Count != 1)
        {
            Reject(declaration, "MemoryMarshal.GetReference does not have exactly one argument.",
                filePath, statistics, diagnostics);
            return;
        }

        var span = getReference.ArgumentList.Arguments[0].Expression;
        var spanType = model.GetTypeInfo(span).Type;
        if (!IsSpanLike(spanType, out var element) || element is null)
        {
            Reject(declaration,
                "the GetReference argument is not a Span/ReadOnlySpan expression.",
                filePath, statistics, diagnostics);
            return;
        }

        if (!SymbolEqualityComparer.Default.Equals(element, local.Type))
        {
            Reject(declaration,
                "the span element type does not match the ref local element type.",
                filePath, statistics, diagnostics);
            return;
        }

        if (!IsSupportedScalar(element))
        {
            Reject(declaration,
                "the span element type is not a supported MemoryMarshal.Read value type.",
                filePath, statistics, diagnostics);
            return;
        }

        if (!IsStableSpanExpression(span, model))
        {
            Reject(declaration,
                "the span expression is not a stable identifier/parameter/field reference; " +
                "re-evaluating it would not be equivalent to the captured ref.",
                filePath, statistics, diagnostics);
            return;
        }

        var scope = FindScope(declaration);
        if (scope is null)
        {
            Reject(declaration, "the ref local has no enclosing method/function scope.",
                filePath, statistics, diagnostics);
            return;
        }

        var memoryMarshalReceiver = GetMemoryMarshalReceiver(getReference);

        var pendingReplacements = new List<(SyntaxNode Original, SyntaxNode Replacement)>();
        var readSites = 0;
        var addSites = 0;

        foreach (var identifier in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol symbol
                || !SymbolEqualityComparer.Default.Equals(symbol, local))
            {
                continue;
            }

            if (!TryLowerReference(identifier, model, span, element,
                    memoryMarshalReceiver, out var replacement, out var kind, out var reason))
            {
                Reject(declaration, reason, filePath, statistics, diagnostics);
                return;
            }

            pendingReplacements.Add(replacement!.Value);
            if (kind == ReferenceKind.ReadUnaligned)
                readSites++;
            else
                addSites++;
        }

        replacements.AddRange(pendingReplacements);
        replacements.Add((declaration, declaration.WithAdditionalAnnotations(RemovalAnnotation)));
        statistics.RefLocalsLowered++;
        statistics.ReadUnalignedSitesLowered += readSites;
        statistics.AddValueSitesLowered += addSites;
    }

    // ---- reference shapes ----

    private enum ReferenceKind
    {
        ReadUnaligned,
        AddValue,
    }

    private bool TryLowerReference(
        IdentifierNameSyntax identifier,
        SemanticModel model,
        ExpressionSyntax span,
        ITypeSymbol element,
        ExpressionSyntax memoryMarshalReceiver,
        out (SyntaxNode Original, SyntaxNode Replacement)? replacement,
        out ReferenceKind kind,
        out string reason)
    {
        replacement = null;
        kind = ReferenceKind.ReadUnaligned;
        reason = string.Empty;

        if (identifier.Parent is not ArgumentSyntax argument
            || argument.Expression != identifier
            || argument.RefKindKeyword.IsKind(SyntaxKind.None))
        {
            reason = "the ref local is used outside an Unsafe.ReadUnaligned/Unsafe.Add ref argument.";
            return false;
        }

        if (argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            reason = "the ref local argument has no enclosing Unsafe invocation.";
            return false;
        }

        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method is null
            || method.ContainingType?.ToDisplayString() != UnsafeTypeName)
        {
            reason = "the ref local is passed to an unsupported intrinsic or method.";
            return false;
        }

        if (method.Name == "ReadUnaligned")
        {
            if (!_options.LowerReadUnaligned)
            {
                reason = "Unsafe.ReadUnaligned lowering is disabled by options.";
                return false;
            }

            if (argumentList.Arguments.Count != 1 || argumentList.Arguments[0] != argument)
            {
                reason = "Unsafe.ReadUnaligned(ref local) is not the only argument shape.";
                return false;
            }

            if (!TryGetSupportedTypeArgument(invocation, model, out var typeArgument))
            {
                reason = "Unsafe.ReadUnaligned requires one explicit, supported value-type argument.";
                return false;
            }

            if (element.SpecialType != SpecialType.System_Byte)
            {
                reason = "Unsafe.ReadUnaligned(ref local) requires a byte-element span.";
                return false;
            }

            var readSource = BuildReadSource(span, index: null);
            var read = BuildReadInvocation(memoryMarshalReceiver, readSource, typeArgument!);
            replacement = (invocation, read.WithTriviaFrom(invocation));
            kind = ReferenceKind.ReadUnaligned;
            return true;
        }

        if (method.Name != "Add")
        {
            reason = "the ref local is passed to an unsupported Unsafe intrinsic.";
            return false;
        }

        if (!TryMatchUnsafeAdd(argument, argumentList, model, out var index))
        {
            reason = "Unsafe.Add(ref local, k) is not a two-argument int-offset call.";
            return false;
        }

        if (TryGetReadUnalignedParent(invocation, model, out var readParent, out var readArgument))
        {
            if (!_options.LowerReadUnaligned)
            {
                reason = "Unsafe.ReadUnaligned lowering is disabled by options.";
                return false;
            }

            if (readParent.ArgumentList.Arguments.Count != 1
                || readParent.ArgumentList.Arguments[0] != readArgument)
            {
                reason = "Unsafe.ReadUnaligned(ref Unsafe.Add(...)) is not the only argument shape.";
                return false;
            }

            if (!TryGetSupportedTypeArgument(readParent, model, out var typeArgument))
            {
                reason = "Unsafe.ReadUnaligned requires one explicit, supported value-type argument.";
                return false;
            }

            if (element.SpecialType != SpecialType.System_Byte)
            {
                reason = "Unsafe.ReadUnaligned(ref local) requires a byte-element span.";
                return false;
            }

            var readSource = BuildReadSource(span, index);
            var read = BuildReadInvocation(memoryMarshalReceiver, readSource, typeArgument!);
            replacement = (readParent, read.WithTriviaFrom(readParent));
            kind = ReferenceKind.ReadUnaligned;
            return true;
        }

        if (!_options.LowerAddValueReads)
        {
            reason = "Unsafe.Add value lowering is disabled by options.";
            return false;
        }

        if (AddInvocationRequiresRef(invocation))
        {
            reason = "Unsafe.Add(ref local, k) is used in a ref context, not as an ordinary value.";
            return false;
        }

        if (!SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(invocation).Type, element))
        {
            reason = "Unsafe.Add(ref local, k) does not have the span element type.";
            return false;
        }

        var elementAccess = ElementAccessExpression(
            span.WithoutTrivia(),
            BracketedArgumentList(SingletonSeparatedList(Argument(index!))));
        replacement = (invocation, elementAccess.WithTriviaFrom(invocation));
        kind = ReferenceKind.AddValue;
        return true;
    }

    private static bool TryMatchUnsafeAdd(
        ArgumentSyntax argument,
        ArgumentListSyntax argumentList,
        SemanticModel model,
        out ExpressionSyntax? index)
    {
        index = null;
        if (argumentList.Arguments.Count != 2 || argumentList.Arguments[0] != argument)
            return false;

        var argumentIndex = argumentList.Arguments[1];
        if (!argumentIndex.RefKindKeyword.IsKind(SyntaxKind.None))
            return false;

        var info = model.GetTypeInfo(argumentIndex.Expression);
        var type = info.ConvertedType ?? info.Type;
        if (type?.SpecialType != SpecialType.System_Int32)
            return false;

        index = argumentIndex.Expression.WithoutTrivia();
        return true;
    }

    private static bool TryGetReadUnalignedParent(
        InvocationExpressionSyntax addInvocation,
        SemanticModel model,
        out InvocationExpressionSyntax readParent,
        out ArgumentSyntax readArgument)
    {
        readParent = null!;
        readArgument = null!;

        if (addInvocation.Parent is not ArgumentSyntax argument
            || argument.Expression != addInvocation
            || argument.RefKindKeyword.IsKind(SyntaxKind.None)
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax candidate)
        {
            return false;
        }

        var method = model.GetSymbolInfo(candidate).Symbol as IMethodSymbol;
        if (method is null
            || method.Name != "ReadUnaligned"
            || method.ContainingType?.ToDisplayString() != UnsafeTypeName)
        {
            return false;
        }

        readParent = candidate;
        readArgument = argument;
        return true;
    }

    private static bool AddInvocationRequiresRef(InvocationExpressionSyntax addInvocation)
    {
        var parent = addInvocation.Parent;
        while (parent is ParenthesizedExpressionSyntax parenthesized)
            parent = parenthesized.Parent;

        switch (parent)
        {
            case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
            case RefExpressionSyntax:
                return true;
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }
                when declaration.Type is RefTypeSyntax:
                return true;
            default:
                return false;
        }
    }

    // ---- expression construction ----

    private static ExpressionSyntax BuildReadSource(
        ExpressionSyntax span,
        ExpressionSyntax? index)
    {
        var source = span.WithoutTrivia();
        return index is null
            ? source
            : SliceInvocation(source, index.WithoutTrivia());
    }

    private static InvocationExpressionSyntax SliceInvocation(
        ExpressionSyntax receiver, ExpressionSyntax index)
        => InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                IdentifierName("Slice")),
            ArgumentList(SingletonSeparatedList(Argument(index))));

    private static InvocationExpressionSyntax BuildReadInvocation(
        ExpressionSyntax memoryMarshalReceiver,
        ExpressionSyntax source,
        TypeSyntax typeArgument)
    {
        var readName = GenericName(
            Identifier("Read"),
            TypeArgumentList(SingletonSeparatedList(typeArgument.WithoutTrivia())));

        return InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                memoryMarshalReceiver.WithoutTrivia(),
                readName),
            ArgumentList(SingletonSeparatedList(Argument(source))));
    }

    private static bool TryGetSupportedTypeArgument(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        out TypeSyntax? typeArgument)
    {
        typeArgument = null;

        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax genericName
            })
        {
            return false;
        }

        if (genericName.TypeArgumentList.Arguments.Count != 1)
            return false;

        typeArgument = genericName.TypeArgumentList.Arguments[0];
        var type = model.GetTypeInfo(typeArgument).Type;
        return IsSupportedScalar(type);
    }

    // ---- syntax/symbol helpers ----

    private static InvocationExpressionSyntax? UnwrapRefInitializer(ExpressionSyntax value)
    {
        while (value is ParenthesizedExpressionSyntax parenthesized)
            value = parenthesized.Expression;

        if (value is not RefExpressionSyntax refExpression)
            return null;

        var expression = refExpression.Expression;
        while (expression is ParenthesizedExpressionSyntax nested)
            expression = nested.Expression;

        return expression as InvocationExpressionSyntax;
    }

    private static bool IsMemoryMarshalGetReference(
        InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        return method is not null
               && method.Name == "GetReference"
               && method.ContainingType?.ToDisplayString() == MemoryMarshalTypeName;
    }

    private static ExpressionSyntax GetMemoryMarshalReceiver(InvocationExpressionSyntax getReference)
        => getReference.Expression is MemberAccessExpressionSyntax member
            ? member.Expression
            : ParseExpression("global::" + MemoryMarshalTypeName);

    private static bool IsStableSpanExpression(ExpressionSyntax expression, SemanticModel model)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                return model.GetSymbolInfo(identifier).Symbol
                    is ILocalSymbol or IParameterSymbol or IFieldSymbol;

            case MemberAccessExpressionSyntax member
                when member.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                     && member.Expression is ThisExpressionSyntax:
                return model.GetSymbolInfo(member).Symbol is IFieldSymbol;

            default:
                return false;
        }
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

    private static bool IsSupportedScalar(ITypeSymbol? type)
        => type?.SpecialType is SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Char
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Boolean;

    private static bool IsCommentTrivia(SyntaxTrivia trivia)
        => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
           || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
           || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
           || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);

    private static bool IsCs2jGeneratedSupportFile(string? filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
               && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static void Reject(
        LocalDeclarationStatementSyntax declaration,
        string reason,
        string filePath,
        UnsafeRefLoweringStatistics statistics,
        List<UnsafeRefLoweringDiagnostic> diagnostics)
    {
        var line = 1;
        var column = 1;
        try
        {
            var span = declaration.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // Location unavailable: fall back to (1,1).
        }

        statistics.RejectedRefLocals++;
        diagnostics.Add(new UnsafeRefLoweringDiagnostic(
            UnsafeRefLoweringSeverity.Warning,
            UnsafeRefLoweringDiagnosticCode.URL001,
            "ref local preserved: " + reason,
            filePath, line, column));
    }

    private static UnsafeRefLoweringResult Rollback(
        string sourceCode,
        UnsafeRefLoweringStatistics statistics,
        IReadOnlyList<UnsafeRefLoweringDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };

    /// <summary>Format-free candidate; it must reparse with zero errors.</summary>
    private static string? Validate(CompilationUnitSyntax root)
    {
        var candidate = root.ToFullString();
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                candidate, new CSharpParseOptions(LanguageVersion.Latest));
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error)
                ? candidate
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes statements carrying <see cref="RemovalAnnotation"/> from block and
    /// switch-section statement lists. Comments on a removed declaration are
    /// re-attached to the next statement (or the closing brace) so trivia is not
    /// silently lost.
    /// </summary>
    private sealed class RemovalRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            var (statements, pending) = FilterStatements(visited.Statements);
            if (pending.Count == 0)
                return visited.WithStatements(statements);

            var closeBrace = visited.CloseBraceToken.WithLeadingTrivia(
                pending.AddRange(visited.CloseBraceToken.LeadingTrivia));
            return visited.WithStatements(statements).WithCloseBraceToken(closeBrace);
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
            var (statements, _) = FilterStatements(visited.Statements);
            return visited.WithStatements(statements);
        }

        private static (SyntaxList<StatementSyntax> Statements, SyntaxTriviaList Pending)
            FilterStatements(SyntaxList<StatementSyntax> statements)
        {
            var kept = new List<StatementSyntax>(statements.Count);
            var pending = new List<SyntaxTrivia>();

            foreach (var statement in statements)
            {
                if (statement.HasAnnotation(RemovalAnnotation))
                {
                    pending.AddRange(CollectComments(statement.GetLeadingTrivia()));
                    continue;
                }

                var current = statement;
                if (pending.Count > 0)
                {
                    current = current.WithLeadingTrivia(
                        SyntaxFactory.TriviaList(pending)
                            .AddRange(current.GetLeadingTrivia()));
                    pending.Clear();
                }

                kept.Add(current);
            }

            return (SyntaxFactory.List(kept), SyntaxFactory.TriviaList(pending));
        }

        private static IEnumerable<SyntaxTrivia> CollectComments(SyntaxTriviaList trivia)
        {
            foreach (var item in trivia)
            {
                if (!IsCommentTrivia(item))
                    continue;

                yield return item;
                yield return SyntaxFactory.EndOfLine("\n");
            }
        }
    }
}
