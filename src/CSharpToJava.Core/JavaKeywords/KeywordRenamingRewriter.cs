using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Phase 3a：声明点与引用点改名、nameof 字面量化。
/// 按 RewriteContext.Analysis.RenameTable 查表改写，禁止逐处重新派生。
/// </summary>
internal sealed partial class KeywordRenamingRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly Dictionary<ISymbol, string> _table;
    /// <summary>改名表中出现过的原始名（剥 @），用于调用点回退改名的快速预筛。</summary>
    private readonly HashSet<string> _renamedOriginalNames;

    public KeywordRenamingRewriter(RewriteContext context) : base(visitIntoStructuredTrivia: true)
    {
        _context = context;
        _table = context.Analysis.RenameTable;
        _renamedOriginalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in _table.Keys)
            _renamedOriginalNames.Add(JavaKeywordSyntaxFacts.StripAt(symbol.Name));
    }

    /// <summary>
    /// 文本预筛：重命名永远要求被绑定符号在改名表内，而符号名就是引用处标识符原文
    /// （复合符号的构造形式/partial 各部分同名），因此标识符文本不在被改名原始名集合
    /// 里的节点不可能产生改写。在调用 GetSymbolInfo/GetDeclaredSymbol 前先做这个纯字符
    /// 串判定，避免为每个标识符触发绑定（整树绑定是本 pass 的主要开销）。
    /// </summary>
    private bool ShouldVisit(string? identifierText)
        => identifierText != null
           && _renamedOriginalNames.Contains(JavaKeywordSyntaxFacts.StripAt(identifierText));

    // ---- 声明点改名 ----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax?)base.VisitMethodDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax?)base.VisitPropertyDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node)
    {
        var visited = (EventDeclarationSyntax?)base.VisitEventDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
        var visited = (VariableDeclaratorSyntax?)base.VisitVariableDeclarator(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.Text)) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
            _context.Statistics.SymbolsRenamed++;
            IncrementKindCounter(symbol);
        }
        return visited;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax?)base.VisitClassDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax?)base.VisitStructDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        var visited = (InterfaceDeclarationSyntax?)base.VisitInterfaceDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var visited = (EnumDeclarationSyntax?)base.VisitEnumDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var visited = (DelegateDeclarationSyntax?)base.VisitDelegateDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax?)base.VisitRecordDeclaration(node);
        if (visited == null) return null;
        if (ShouldVisit(node.Identifier.Text) && TryGetNewNameForDeclaration(node, out var newName))
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        return visited;
    }

    public override SyntaxNode? VisitTypeParameter(TypeParameterSyntax node)
    {
        var visited = (TypeParameterSyntax?)base.VisitTypeParameter(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.Text)) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitParameter(ParameterSyntax node)
    {
        var visited = (ParameterSyntax?)base.VisitParameter(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.Text)) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = (LocalFunctionStatementSyntax?)base.VisitLocalFunctionStatement(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.Text)) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax?)base.VisitForEachStatement(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.Text)) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitCatchDeclaration(CatchDeclarationSyntax node)
    {
        var visited = (CatchDeclarationSyntax?)base.VisitCatchDeclaration(node);
        if (visited == null) return null;
        if (visited.Identifier.IsKind(SyntaxKind.IdentifierToken)
            && ShouldVisit(node.Identifier.Text))
        {
            var symbol = _context.Model.GetDeclaredSymbol(node);
            if (symbol != null && _table.TryGetValue(symbol, out var newName))
            {
                visited = visited.WithIdentifier(
                    SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                    .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
            }
        }
        return visited;
    }

    public override SyntaxNode? VisitSingleVariableDesignation(SingleVariableDesignationSyntax node)
    {
        var visited = (SingleVariableDesignationSyntax?)base.VisitSingleVariableDesignation(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.Text)) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        var visited = (NamespaceDeclarationSyntax?)base.VisitNamespaceDeclaration(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Name.ToString())) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithName(SyntaxFactory.ParseName(newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax?)base.VisitFileScopedNamespaceDeclaration(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Name.ToString())) return visited;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol != null && _table.TryGetValue(symbol, out var newName))
        {
            visited = visited.WithName(SyntaxFactory.ParseName(newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var visited = (UsingDirectiveSyntax?)base.VisitUsingDirective(node);
        if (visited == null) return null;
        if (visited.Alias != null && ShouldVisit(visited.Alias.Name.Identifier.Text))
        {
            var symbol = _context.Model.GetDeclaredSymbol(visited.Alias);
            if (symbol != null && _table.TryGetValue(symbol, out var newName))
            {
                visited = visited.WithAlias(
                    visited.Alias.WithName(SyntaxFactory.IdentifierName(newName)))
                    .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
            }
        }
        return visited;
    }

    // ---- 引用点改名 ----

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (IdentifierNameSyntax?)base.VisitIdentifierName(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.ValueText)) return visited;
        return RenameIfBound(visited, node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax?)base.VisitGenericName(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Identifier.ValueText)) return visited;
        // Explicit type arguments (e.g. `Throws<T>(...)`) parse as
        // GenericNameSyntax, which the IdentifierNameSyntax visit never sees;
        // without this the declaration is renamed but the reference is not,
        // producing CS0103.
        var symbolInfo = _context.Model.GetSymbolInfo(node);

        if (symbolInfo.Symbol != null
            && TryLookupRename(symbolInfo.Symbol, out var newName))
        {
            return visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }

        if (symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure
            || symbolInfo.CandidateReason == CandidateReason.MemberGroup)
        {
            foreach (var cand in symbolInfo.CandidateSymbols)
            {
                if (TryLookupRename(cand, out newName))
                {
                    return visited.WithIdentifier(
                        SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                        .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
                }
            }
        }

        // JK-4d：合并整树绑定失败（跨项目同名类型）→ 作用域化重绑定
        if (TryScopedRebind(node, node.Identifier.ValueText, out var scopedName))
        {
            return (GenericNameSyntax)RenameSimpleNameNode(visited, scopedName);
        }

        return visited;
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        var visited = (QualifiedNameSyntax?)base.VisitQualifiedName(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Right.Identifier.ValueText)) return visited;
        // 只改右段（Left 由成员访问/命名空间路径处理）
        var rightSymbol = _context.Model.GetSymbolInfo(node).Symbol;
        if (rightSymbol != null && TryLookupRename(rightSymbol, out var newName))
        {
            visited = visited.WithRight(
                RenameSimpleName(visited.Right, newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        else if (visited.Right is SimpleNameSyntax right
                 && TryScopedRebind(node, right.Identifier.ValueText, out var scopedRight))
        {
            visited = visited.WithRight((SimpleNameSyntax)RenameSimpleNameNode(visited.Right, scopedRight))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax?)base.VisitMemberAccessExpression(node);
        if (visited == null) return null;
        if (!ShouldVisit(node.Name.Identifier.ValueText)) return visited;
        // 改 Name 段
        var nameSymbol = _context.Model.GetSymbolInfo(node).Symbol;
        if (nameSymbol != null && TryLookupRename(nameSymbol, out var newName))
        {
            visited = visited.WithName(
                RenameSimpleName(visited.Name, newName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        else if (visited.Name is SimpleNameSyntax name
                 && TryScopedRebind(node, name.Identifier.ValueText, out var scopedName))
        {
            visited = visited.WithName((SimpleNameSyntax)RenameSimpleNameNode(visited.Name, scopedName))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }
        return visited;
    }

    // ---- nameof 字面量化（JK-4a） ----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 先递归处理参数
        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
        if (visited == null) return null;

        // nameof 检测：表达式是 IdentifierName "nameof"
        if (visited.Expression is IdentifierNameSyntax nameSyntax
            && nameSyntax.Identifier.ValueText == "nameof")
        {
            // 取原始（未改名）参数
            var origArg = node.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (origArg != null)
            {
                // 检查参数中是否有任一段标识符的文本匹配改名表中的旧名
                var hasRenamed = false;
                foreach (var id in origArg.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    if (ShouldVisit(id.Identifier.ValueText))
                    {
                        hasRenamed = true;
                        break;
                    }
                }
                if (hasRenamed)
                {
                    var replaced = TryReplaceNameof(node);
                    if (replaced != null)
                        return replaced;
                }
            }
        }

        return visited;
    }

    /// <summary>
    /// JK-4a：nameof 参数中任一段绑定到被改名符号时，
    /// 替换整个 nameof(...) 为原名常量字符串字面量。
    /// </summary>
    private ExpressionSyntax? TryReplaceNameof(InvocationExpressionSyntax original)
    {
        // 取参数表达式中最右一段标识符（剥 @ 后的原文）
        var arg = original.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (arg == null) return null;

        // 原名字符串 = 参数最右一段标识符剥 @ 后的原文
        var lastName = arg switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };

        if (lastName == null) return null;
        lastName = JavaKeywordSyntaxFacts.StripAt(lastName);

        var literal = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
            SyntaxFactory.Literal(lastName))
            .WithTriviaFrom(original)
            .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);

        _context.Statistics.NameofLiteralized++;
        return literal;
    }

    // ---- 辅助 ----

    private bool TryGetNewNameForDeclaration(SyntaxNode declarationNode, out string newName)
    {
        newName = "";
        var symbol = _context.Model.GetDeclaredSymbol(declarationNode);
        if (symbol == null) return false;
        if (_table.TryGetValue(symbol, out newName))
        {
            _context.Statistics.SymbolsRenamed++;
            IncrementKindCounter(symbol);
            return true;
        }
        // Fall back: the declared symbol may be one part of a partial
        // method whose other part is in the rename table. This handles
        // multi-file partial methods where GetDeclaredSymbol returns
        // different IMethodSymbol instances for each .cs file and only
        // the definition part (or one implementation part) was collected.
        if (symbol is IMethodSymbol method)
        {
            if (method.PartialDefinitionPart is { } def
                && _table.TryGetValue(def, out newName))
            {
                _context.Statistics.SymbolsRenamed++;
                IncrementKindCounter(symbol);
                return true;
            }
            if (method.PartialImplementationPart is { } impl
                && _table.TryGetValue(impl, out newName))
            {
                _context.Statistics.SymbolsRenamed++;
                IncrementKindCounter(symbol);
                return true;
            }

            // Final fallback: when Roslyn does not link this implementation
            // part to the definition part (e.g. a second implementation file
            // in a multi-target project where only the first implementation
            // is linked), look up the rename table by the method's original
            // name. If another symbol with the same name was renamed, use
            // the same new name. This ensures all partial parts — even
            // unlinked ones — get the same rename.
            if (JavaKeywordSyntaxFacts.NeedsEscape(method.Name))
            {
                foreach (var (existingSymbol, existingNewName) in _table)
                {
                    if (existingSymbol is IMethodSymbol existingMethod
                        && existingMethod.Name == method.Name
                        && existingMethod.IsPartialDefinition)
                    {
                        newName = existingNewName;
                        _context.Statistics.SymbolsRenamed++;
                        IncrementKindCounter(symbol);
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private void IncrementKindCounter(ISymbol symbol)
    {
        switch (symbol)
        {
            case IMethodSymbol: _context.Statistics.MethodsRenamed++; break;
            case IPropertySymbol: _context.Statistics.PropertiesRenamed++; break;
            case IFieldSymbol: _context.Statistics.FieldsRenamed++; break;
            case IEventSymbol: _context.Statistics.EventsRenamed++; break;
            case ILocalSymbol: _context.Statistics.LocalsRenamed++; break;
            case IParameterSymbol: _context.Statistics.ParametersRenamed++; break;
            case INamedTypeSymbol: _context.Statistics.TypesRenamed++; break;
            case INamespaceSymbol: _context.Statistics.NamespacesRenamed++; break;
            case IAliasSymbol: _context.Statistics.AliasesRenamed++; break;
        }
    }

    /// <summary>
    /// Replace the identifier of a (possibly generic) simple name while
    /// preserving its type-argument list (e.g. <c>Throws&lt;T&gt;</c>).
    /// </summary>
    private static SimpleNameSyntax RenameSimpleName(SimpleNameSyntax name, string newName)
        => name is GenericNameSyntax generic
            ? generic.WithIdentifier(
                SyntaxFactory.Identifier(
                    generic.Identifier.LeadingTrivia, newName, generic.Identifier.TrailingTrivia))
            : SyntaxFactory.IdentifierName(newName);

    private IdentifierNameSyntax RenameIfBound(IdentifierNameSyntax visited, IdentifierNameSyntax original)
    {
        var symbolInfo = _context.Model.GetSymbolInfo(original);

        // JK-4c：精确绑定
        if (symbolInfo.Symbol != null
            && TryLookupRename(symbolInfo.Symbol, out var newName))
        {
            return visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }

        // JK-4c：方法组多候选——候选集合与表中符号相交即改写
        if (symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure
            || symbolInfo.CandidateReason == CandidateReason.MemberGroup)
        {
            foreach (var cand in symbolInfo.CandidateSymbols)
            {
                if (TryLookupRename(cand, out newName))
                {
                    return visited.WithIdentifier(
                        SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
                        .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
                }
            }
        }

        // JK-4d：合并整树绑定失败（跨项目同名类型）→ 作用域化重绑定
        if (TryScopedRebind(original, original.Identifier.ValueText, out var scopedName))
        {
            return visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, scopedName, visited.Identifier.TrailingTrivia))
                .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
        }

        // 动态绑定不改（JK-4c / JK-8）
        return visited;
    }

    /// <summary>
    /// Look up the rename table directly, falling back to the other
    /// part of a partial method (definition ↔ implementation) when the
    /// bound symbol itself is not in the table.
    /// </summary>
    private bool TryLookupRename(ISymbol symbol, out string newName)
    {
        if (_table.TryGetValue(symbol, out newName))
            return true;
        if (symbol is IMethodSymbol method)
        {
            // `Throws<T>(...)` binds to the constructed method; the rename
            // table keys the generic definition.
            if (!SymbolEqualityComparer.Default.Equals(method, method.ConstructedFrom)
                && _table.TryGetValue(method.ConstructedFrom, out newName))
                return true;
            if (method.PartialDefinitionPart is { } def
                && _table.TryGetValue(def, out newName))
                return true;
            if (method.PartialImplementationPart is { } impl
                && _table.TryGetValue(impl, out newName))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 作用域化重绑定回退（JK-4d）。整树合并编译里跨项目同名类型会让调用点绑定
    /// 完全失败（Symbol=null、无候选，如 <c>System.AssertExtensions</c> 与
    /// <c>System.Tests.AssertExtensions</c>），声明侧改名因此残留调用点（真实 SDK
    /// 上 147 处 CS0117）。此处按宿主提供的作用域编译（文件所属项目分区 + 该项目
    /// global usings）重新绑定同一节点，命中改名表即采纳。仅当标识符原文与表中
    /// 某个原始名相同时才触发，常规标识符零开销；查不到则保持现状不改写。
    /// </summary>
    private bool TryScopedRebind(SyntaxNode original, string identifierText, out string newName)
    {
        newName = string.Empty;
        var fallback = _context.ScopedBindingFallback;
        if (fallback == null) return false;
        if (!_renamedOriginalNames.Contains(JavaKeywordSyntaxFacts.StripAt(identifierText)))
            return false;

        var scopedModel = fallback.GetScopedModel(original.SyntaxTree);
        if (scopedModel == null) return false;

        var info = scopedModel.GetSymbolInfo(original);
        // 作用域编译与合并编译是不同编译实例：即使共享同一语法树，声明符号也是
        // 不同实例，直接查改名表（合并符号为键）必落空。这里沿作用域符号的声明
        // 语法引用回到共享语法树，用合并模型重新取声明符号后再查表。
        var scopedSymbol = (ISymbol?)info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (scopedSymbol == null) return false;
        var declRef = scopedSymbol.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef == null) return false;

        var declNode = declRef.GetSyntax();
        var mergedModel = _context.Model.Compilation.GetSemanticModel(declRef.SyntaxTree);
        var mergedSymbol = declNode switch
        {
            MethodDeclarationSyntax m => mergedModel.GetDeclaredSymbol(m),
            PropertyDeclarationSyntax p => mergedModel.GetDeclaredSymbol(p),
            EventDeclarationSyntax e => mergedModel.GetDeclaredSymbol(e),
            FieldDeclarationSyntax f => mergedModel.GetDeclaredSymbol(
                f.Declaration.Variables.FirstOrDefault()) is { } v ? v : null,
            VariableDeclaratorSyntax vd => mergedModel.GetDeclaredSymbol(vd),
            ClassDeclarationSyntax c => mergedModel.GetDeclaredSymbol(c),
            StructDeclarationSyntax s => mergedModel.GetDeclaredSymbol(s),
            InterfaceDeclarationSyntax i => mergedModel.GetDeclaredSymbol(i),
            EnumDeclarationSyntax en => mergedModel.GetDeclaredSymbol(en),
            DelegateDeclarationSyntax d => mergedModel.GetDeclaredSymbol(d),
            RecordDeclarationSyntax r => mergedModel.GetDeclaredSymbol(r),
            TypeParameterSyntax tp => mergedModel.GetDeclaredSymbol(tp),
            ParameterSyntax par => mergedModel.GetDeclaredSymbol(par),
            _ => null,
        };
        if (mergedSymbol != null && TryLookupRename(mergedSymbol, out newName))
            return true;

        foreach (var candidate in info.CandidateSymbols)
        {
            var candRef = candidate.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault();
            if (candRef == null) continue;
            var candNode = candRef.GetSyntax();
            var candMerged = _context.Model.Compilation.GetSemanticModel(candRef.SyntaxTree)
                .GetDeclaredSymbol(candNode as MethodDeclarationSyntax ?? (SyntaxNode?)null);
            if (candMerged != null && TryLookupRename(candMerged, out newName))
                return true;
        }
        return false;
    }

    /// <summary>带注记地把简单名（可含类型实参）改名，保留前后 trivia。</summary>
    private SyntaxNode RenameSimpleNameNode(SimpleNameSyntax visited, string newName)
        => visited.WithIdentifier(
                SyntaxFactory.Identifier(visited.Identifier.LeadingTrivia, newName, visited.Identifier.TrailingTrivia))
            .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
}
