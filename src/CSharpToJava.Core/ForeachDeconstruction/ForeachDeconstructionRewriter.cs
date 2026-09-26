using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachDeconstruction;

/// <summary>
/// C# syntax rewriter that lowers deconstruction foreach over KeyValuePair
/// collections into plain foreach + explicit local declarations.
/// </summary>
internal sealed class ForeachDeconstructionRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly ForeachDeconstructionOptions _options;
    private readonly ForeachDeconstructionStatistics _stats;
    private readonly List<ForeachDeconstructionDiagnostic> _diagnostics;

    public bool Changed { get; private set; }
    public IReadOnlyList<ForeachDeconstructionDiagnostic> Diagnostics => _diagnostics;

    private int _entryCounter;
    private int _innerCounter;

    public ForeachDeconstructionRewriter(
        SemanticModel? model,
        ForeachDeconstructionOptions options,
        ForeachDeconstructionStatistics stats,
        List<ForeachDeconstructionDiagnostic> diagnostics)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        // Visit children first (in case of nested deconstruction foreach in the body).
        var visited = (ForEachVariableStatementSyntax?)base.VisitForEachVariableStatement(node);
        if (visited == null)
            return visited;

        if (_model == null)
            return visited;

        // Determine the element type.
        var forEachInfo = _model.GetForEachStatementInfo(node);
        var elementType = forEachInfo.ElementType as INamedTypeSymbol;
        if (elementType == null)
            return visited;

        // Classify the element type.
        var family = ClassifyFamily(elementType);
        switch (family)
        {
            case FamilyKind.KeyValuePair:
                break; // proceed to lowering
            case FamilyKind.Deconstruct:
                AddInfoDiagnostic(node, "Deconstruct-based deconstruction skipped; handled by converter.");
                _stats.ForeachDeconstructionsSkipped++;
                return visited;
            case FamilyKind.Tuple:
                AddInfoDiagnostic(node, "Tuple-based deconstruction skipped; run TupleToStruct first.");
                _stats.ForeachDeconstructionsSkipped++;
                return visited;
            default:
                return visited;
        }

        // KV family: lower it.
        var entryName = FreshName("__entry", visited);
        var entryIdentifier = SyntaxFactory.IdentifierName(entryName);

        // Build the unpack statements.
        var unpackStatements = new List<StatementSyntax>();
        UnpackVariable(visited.Variable, entryIdentifier, elementType, unpackStatements, visited);

        // Build the new foreach statement.
        var newForEach = SyntaxFactory.ForEachStatement(
            visited.AwaitKeyword,
            visited.ForEachKeyword,
            visited.OpenParenToken,
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(entryName).WithLeadingTrivia(
                visited.Variable.GetLeadingTrivia()),
            visited.InKeyword,
            visited.Expression,
            visited.CloseParenToken,
            PrependStatements(visited.Statement, unpackStatements));

        Changed = true;
        _stats.ForeachDeconstructionsLowered++;

        return newForEach
            .WithLeadingTrivia(visited.GetLeadingTrivia())
            .WithTrailingTrivia(visited.GetTrailingTrivia());
    }

    // -----------------------------------------------------------------------
    // Family classification
    // -----------------------------------------------------------------------

    private enum FamilyKind { KeyValuePair, Deconstruct, Tuple, Other }

    private FamilyKind ClassifyFamily(INamedTypeSymbol elementType)
    {
        // KV family: named KeyValuePair<K,V> or implements IEnumerable<KeyValuePair<K,V>>.
        if (IsKeyValuePair(elementType))
            return FamilyKind.KeyValuePair;
        foreach (var iface in elementType.AllInterfaces)
        {
            if (IsKeyValuePair(iface))
                return FamilyKind.KeyValuePair;
        }

        // Tuple family: ValueTuple/Tuple or implements ITuple.
        if (IsTuple(elementType))
            return FamilyKind.Tuple;

        // Deconstruct family: has Deconstruct method with out parameters.
        if (HasDeconstruct(elementType))
            return FamilyKind.Deconstruct;

        return FamilyKind.Other;
    }

    private static bool IsKeyValuePair(INamedTypeSymbol type)
        => type.Name == "KeyValuePair"
           && type.TypeArguments.Length == 2
           && (type.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
               || type.OriginalDefinition.ToDisplayString().StartsWith(
                   "System.Collections.Generic.KeyValuePair<", StringComparison.Ordinal));

    private static bool IsTuple(INamedTypeSymbol type)
    {
        if (type.IsTupleType)
            return true;
        var defName = type.OriginalDefinition.ToDisplayString();
        if (defName.StartsWith("System.ValueTuple<", StringComparison.Ordinal)
            || defName.StartsWith("System.Tuple<", StringComparison.Ordinal))
            return true;
        return type.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString() == "System.Runtime.CompilerServices.ITuple");
    }

    private static bool HasDeconstruct(INamedTypeSymbol type)
    {
        // Look for instance Deconstruct methods with out parameters.
        var deconstructMethods = type.GetMembers("Deconstruct")
            .OfType<IMethodSymbol>()
            .Where(m => !m.IsStatic && m.Parameters.Length > 0
                        && m.Parameters.All(p => p.RefKind == RefKind.Out))
            .ToList();
        if (deconstructMethods.Count > 0)
            return true;

        // Also check extension methods via ReducedFrom — but that requires
        // compilation-level lookup which is complex. The downstream converter
        // handles extension Deconstruct, so we only check instance methods here.
        // If we miss an extension Deconstruct, the element type will be "Other"
        // and the foreach will pass through unchanged (safe).
        return false;
    }

    // -----------------------------------------------------------------------
    // Unpack recursion — generate local declarations from the designation
    // -----------------------------------------------------------------------

    private void UnpackVariable(
        ExpressionSyntax variable,
        IdentifierNameSyntax accessorRoot,
        INamedTypeSymbol currentType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode)
    {
        // DeclarationExpressionSyntax: var (x, y) form
        if (variable is DeclarationExpressionSyntax declaration)
        {
            UnpackDesignation(declaration.Designation, accessorRoot, currentType, statements, scopeNode,
                declaredTypeSyntax: declaration.Type);
            return;
        }

        // TupleExpressionSyntax: (int x, int y) or (var x, var y) form
        if (variable is TupleExpressionSyntax tuple)
        {
            var components = GetKvComponents(currentType);
            for (var i = 0; i < tuple.Arguments.Count && i < components.Count; i++)
            {
                var (suffix, subType) = components[i];
                var arg = tuple.Arguments[i].Expression;
                var memberAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    accessorRoot,
                    SyntaxFactory.IdentifierName(suffix));

                UnpackTupleArgument(arg, memberAccess, subType, statements, scopeNode);
            }
            return;
        }

        // IdentifierNameSyntax: whole-binding (rare)
        if (variable is IdentifierNameSyntax id && id.Identifier.Text != "_")
        {
            statements.Add(MakeLocalDeclaration("var", id.Identifier.Text, accessorRoot));
        }
    }

    private void UnpackDesignation(
        VariableDesignationSyntax designation,
        IdentifierNameSyntax accessorRoot,
        INamedTypeSymbol currentType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode,
        TypeSyntax? declaredTypeSyntax)
    {
        // SingleVariableDesignationSyntax: single variable (leaf)
        if (designation is SingleVariableDesignationSyntax single)
        {
            if (single.Identifier.Text == "_")
                return; // discard
            var declType = GetDeclarationType(declaredTypeSyntax);
            statements.Add(MakeLocalDeclaration(declType, single.Identifier.Text, accessorRoot));
            return;
        }

        // ParenthesizedVariableDesignationSyntax: composite
        if (designation is ParenthesizedVariableDesignationSyntax parenthesized)
        {
            var components = GetKvComponents(currentType);
            for (var i = 0; i < parenthesized.Variables.Count && i < components.Count; i++)
            {
                var (suffix, subType) = components[i];
                var memberAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    accessorRoot,
                    SyntaxFactory.IdentifierName(suffix));

                UnpackDesignationChild(parenthesized.Variables[i], memberAccess, subType, statements, scopeNode);
            }
            return;
        }
    }

    private void UnpackDesignationChild(
        VariableDesignationSyntax child,
        ExpressionSyntax accessor,
        INamedTypeSymbol subType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode)
    {
        if (child is SingleVariableDesignationSyntax single)
        {
            if (single.Identifier.Text == "_")
                return; // discard
            // For ParenthesizedVariableDesignationSyntax children, there's no
            // type annotation — use var.
            statements.Add(MakeLocalDeclaration("var", single.Identifier.Text, accessor));
            return;
        }

        if (child is ParenthesizedVariableDesignationSyntax nested)
        {
            // Nested composite: introduce intermediate variable and recurse.
            var subFamily = ClassifyFamily(subType);
            if (subFamily == FamilyKind.Tuple)
            {
                // Try to unpack as Tuple (.ItemN).
                var midName = FreshName("__entry_inner", scopeNode);
                var midId = SyntaxFactory.IdentifierName(midName);
                statements.Add(MakeLocalDeclaration("var", midName, accessor));

                var tupleComponents = GetTupleComponents(subType);
                for (var i = 0; i < nested.Variables.Count && i < tupleComponents.Count; i++)
                {
                    var (suffix, innerType) = tupleComponents[i];
                    var memberAccess = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        midId,
                        SyntaxFactory.IdentifierName(suffix));
                    UnpackDesignationChild(nested.Variables[i], memberAccess, innerType, statements, scopeNode);
                }
                return;
            }

            if (subFamily == FamilyKind.KeyValuePair)
            {
                // Nested KV: introduce intermediate and recurse.
                var midName2 = FreshName("__entry_inner", scopeNode);
                var midId2 = SyntaxFactory.IdentifierName(midName2);
                statements.Add(MakeLocalDeclaration("var", midName2, accessor));
                UnpackDesignation(child, midId2, subType, statements, scopeNode, null);
                return;
            }

            // Deconstruct or Other: bind whole thing to a var, emit info.
            var midName3 = FreshName("__entry_inner", scopeNode);
            statements.Add(MakeLocalDeclaration("var", midName3, accessor));
            AddInfoDiagnostic(child, "Nested Deconstruct-based deconstruction skipped; handled by converter.");
            return;
        }

        // Other designation types — skip
    }

    private void UnpackTupleArgument(
        ExpressionSyntax arg,
        ExpressionSyntax accessor,
        INamedTypeSymbol subType,
        List<StatementSyntax> statements,
        SyntaxNode scopeNode)
    {
        // DeclarationExpressionSyntax: var x or int x inside a TupleExpression
        if (arg is DeclarationExpressionSyntax decl)
        {
            if (decl.Designation is SingleVariableDesignationSyntax single)
            {
                if (single.Identifier.Text == "_")
                    return;
                // Determine the declaration type from the syntax.
                var declType = GetDeclarationType(decl.Type);
                statements.Add(MakeLocalDeclaration(declType, single.Identifier.Text, accessor));
            }
            else if (decl.Designation is ParenthesizedVariableDesignationSyntax nested)
            {
                // Nested composite in a TupleExpression argument.
                UnpackDesignationChild(nested, accessor, subType, statements, scopeNode);
            }
            return;
        }

        // IdentifierNameSyntax: inferred form (x) or discard (_)
        if (arg is IdentifierNameSyntax id)
        {
            if (id.Identifier.Text == "_")
                return;
            // Inferred type (no annotation) — use var.
            statements.Add(MakeLocalDeclaration("var", id.Identifier.Text, accessor));
            return;
        }

        // TupleExpressionSyntax: nested tuple (rare in KV context)
        if (arg is TupleExpressionSyntax nestedTuple)
        {
            UnpackVariable(nestedTuple, (IdentifierNameSyntax)accessor, subType, statements, scopeNode);
            return;
        }
    }

    // -----------------------------------------------------------------------
    // Component accessors
    // -----------------------------------------------------------------------

    private static List<(string Suffix, INamedTypeSymbol SubType)> GetKvComponents(INamedTypeSymbol kvType)
    {
        // Find the KeyValuePair<K,V> among the type or its interfaces.
        INamedTypeSymbol? kv = null;
        if (IsKeyValuePair(kvType))
            kv = kvType;
        else
            kv = kvType.AllInterfaces.FirstOrDefault(IsKeyValuePair);

        if (kv == null || kv.TypeArguments.Length != 2)
            return new List<(string, INamedTypeSymbol)>();

        return new List<(string, INamedTypeSymbol)>
        {
            ("Key", (kv.TypeArguments[0] as INamedTypeSymbol)!),
            ("Value", (kv.TypeArguments[1] as INamedTypeSymbol)!),
        };
    }

    private static List<(string Suffix, INamedTypeSymbol SubType)> GetTupleComponents(INamedTypeSymbol tupleType)
    {
        var result = new List<(string, INamedTypeSymbol)>();
        if (tupleType.IsTupleType)
        {
            for (var i = 0; i < tupleType.TupleElements.Length; i++)
            {
                result.Add(($"Item{i + 1}", (tupleType.TupleElements[i].Type as INamedTypeSymbol)!));
            }
        }
        return result;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string GetDeclarationType(TypeSyntax? declaredTypeSyntax)
    {
        if (declaredTypeSyntax == null)
            return "var";
        // If it's "var", return "var".
        if (declaredTypeSyntax is IdentifierNameSyntax id && id.Identifier.Text == "var")
            return "var";
        // For predefined types (int, string, etc.) and qualified names, return as-is.
        return declaredTypeSyntax.ToString();
    }

    private static StatementSyntax MakeLocalDeclaration(string declType, string varName, ExpressionSyntax initializer)
    {
        var typeSyntax = declType == "var"
            ? SyntaxFactory.IdentifierName("var")
            : SyntaxFactory.ParseTypeName(declType);

        var declarator = SyntaxFactory.VariableDeclarator(varName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(initializer));

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(typeSyntax)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    private static StatementSyntax PrependStatements(StatementSyntax body, List<StatementSyntax> prepend)
    {
        if (body is BlockSyntax block)
        {
            return block.WithStatements(
                SyntaxFactory.List(prepend.Concat(block.Statements)));
        }

        // Wrap non-block body in a block.
        var newBlock = SyntaxFactory.Block(
            SyntaxFactory.List(prepend.Concat(new[] { body })));
        return newBlock;
    }

    private string FreshName(string baseName, SyntaxNode scopeNode)
    {
        // Find the enclosing method declaration for scope checking.
        SyntaxNode? scope = scopeNode.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        scope ??= scopeNode.FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
        scope ??= scopeNode.FirstAncestorOrSelf<BlockSyntax>();

        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        if (scope != null)
        {
            foreach (var id in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                existingNames.Add(id.Identifier.Text);
            }
            foreach (var decl in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                existingNames.Add(decl.Identifier.Text);
            }
            foreach (var param in scope.DescendantNodes().OfType<ParameterSyntax>())
            {
                existingNames.Add(param.Identifier.Text);
            }
        }

        // First call with baseName returns baseName (counter=0).
        // Second call returns baseName1, etc.
        int counter;
        string name;
        do
        {
            counter = baseName == "__entry" ? _entryCounter++ : _innerCounter++;
            name = counter == 0 ? baseName : baseName + counter;
        }
        while (existingNames.Contains(name));

        return name;
    }

    private void AddInfoDiagnostic(SyntaxNode node, string message)
    {
        var severity = _options.Strict
            ? ForeachDeconstructionSeverity.Error
            : ForeachDeconstructionSeverity.Info;
        if (severity == ForeachDeconstructionSeverity.Info && !_options.ReportInfo)
            return;

        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _diagnostics.Add(new ForeachDeconstructionDiagnostic(severity, message, null, line));
    }
}
