using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Applies generic-deepen plans to one syntax tree of the bound compilation.</summary>
internal sealed class GenericDeepenRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<SyntaxNode, TokenAnchorPlan> _anchorsByNode;
    private readonly Dictionary<SyntaxNode, TypeParamPatternPlan> _patternsByNode;
    private readonly Dictionary<SyntaxNode, CatchTypeParamPlan> _catchesByNode;
    private readonly HashSet<SyntaxNode> _fieldDeclRemovals;
    private readonly Dictionary<SyntaxNode, StaticFieldUsagePlan> _fieldUsages;
    private readonly Dictionary<SyntaxNode, StaticMethodPlan> _staticMethodsByNode;
    private readonly Dictionary<SyntaxNode, StaticMethodCallSitePlan> _staticCallSites;
    private readonly GenericDeepenStatistics _stats;
    private readonly List<GenericDeepenDiagnostic> _diagnostics;
    private readonly string _filePath;

    public GenericDeepenRewriter(
        Dictionary<SyntaxNode, TokenAnchorPlan> anchorsByNode,
        Dictionary<SyntaxNode, TypeParamPatternPlan> patternsByNode,
        Dictionary<SyntaxNode, CatchTypeParamPlan> catchesByNode,
        HashSet<SyntaxNode> fieldDeclRemovals,
        Dictionary<SyntaxNode, StaticFieldUsagePlan> fieldUsages,
        Dictionary<SyntaxNode, StaticMethodPlan> staticMethodsByNode,
        Dictionary<SyntaxNode, StaticMethodCallSitePlan> staticCallSites,
        GenericDeepenStatistics stats,
        List<GenericDeepenDiagnostic> diagnostics,
        string filePath)
    {
        _anchorsByNode = anchorsByNode;
        _patternsByNode = patternsByNode;
        _catchesByNode = catchesByNode;
        _fieldDeclRemovals = fieldDeclRemovals;
        _fieldUsages = fieldUsages;
        _staticMethodsByNode = staticMethodsByNode;
        _staticCallSites = staticCallSites;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        // Single-declarator plans remove the whole declaration.
        if (_fieldDeclRemovals.Contains(node)) return null;
        return base.VisitFieldDeclaration(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Name is IdentifierNameSyntax name && _fieldUsages.TryGetValue(name, out var usage))
        {
            // The receiver (closed type construction) is dropped: the resolved type
            // arguments already went into the accessor call.
            return BuildFieldAccess(usage);
        }

        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // Name position of a member access is handled by VisitMemberAccessExpression;
        // receiver position (Latest.ToString()) still needs the accessor.
        if (_fieldUsages.ContainsKey(node)
            && (node.Parent is not MemberAccessExpressionSyntax parent || parent.Name != node))
        {
            return BuildFieldAccess(_fieldUsages[node]);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is AssignmentExpressionSyntax assignment
            && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && FieldUsageOf(assignment.Left) is { IsAssignmentTarget: true } usage)
        {
            // Visit only the right side: rewriting the left as a read access would
            // produce `__cs2j_get_.. = value`, which does not compile.
            var right = (ExpressionSyntax)(Visit(assignment.Right) ?? assignment.Right)!;
            var invocation = BuildFieldSetter(usage, right);
            return node.WithExpression(invocation);
        }

        return base.VisitExpressionStatement(node);
    }

    private StaticFieldUsagePlan? FieldUsageOf(ExpressionSyntax expression)
        => expression switch
        {
            IdentifierNameSyntax identifier when _fieldUsages.TryGetValue(identifier, out var usage) => usage,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } when _fieldUsages.TryGetValue(name, out var usage) => usage,
            _ => null,
        };

    private ExpressionSyntax BuildFieldAccess(StaticFieldUsagePlan usage)
    {
        _stats.StaticFieldAccessesRewritten++;
        return SyntaxFactory.ParseExpression($"{HolderReference(usage.Plan)}__cs2j_get_{usage.Plan.FieldName}<{TypeArgs(usage)}>({Tokens(usage)})");
    }

    private ExpressionSyntax BuildFieldSetter(StaticFieldUsagePlan usage, ExpressionSyntax right)
    {
        _stats.StaticFieldAccessesRewritten++;
        return SyntaxFactory.ParseExpression(
            $"{HolderReference(usage.Plan)}__cs2j_set_{usage.Plan.FieldName}<{TypeArgs(usage)}>({Tokens(usage)}, {right})");
    }

    private static string HolderReference(StaticFieldPlan plan)
        => plan.HolderNamespace is { } ns
            ? $"global::{ns}.{plan.HolderTypeName}."
            : $"global::{plan.HolderTypeName}.";

    private static string TypeArgs(StaticFieldUsagePlan usage)
        => string.Join(", ", usage.TypeArgumentTexts);

    private static string Tokens(StaticFieldUsagePlan usage)
        => string.Join(", ", usage.TypeArgumentTexts.Select(a => $"typeof({a})"));

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
        if (!_patternsByNode.TryGetValue(node, out var plan))
        {
            return visited;
        }

        var originalExpression = ((IsPatternExpressionSyntax)node.Condition).Expression;
        var tempDeclaration = SyntaxFactory.ParseStatement($"var {plan.TempName} = {originalExpression};");
        var condition = SyntaxFactory.ParseExpression(
            $"typeof({plan.Param.Name}).IsInstanceOfType({plan.TempName})");

        var thenStatements = new List<StatementSyntax>();
        if (!plan.IsDiscard)
        {
            thenStatements.Add(SyntaxFactory.ParseStatement(
                $"{plan.Param.Name} {plan.VariableName} = ({plan.Param.Name})(object){plan.TempName};"));
        }

        thenStatements.AddRange(((BlockSyntax)visited.Statement).Statements);
        var newIf = visited
            .WithCondition(condition)
            .WithStatement(SyntaxFactory.Block(thenStatements));

        _stats.TypeParamPatternsRewritten++;
        // Block wrapper: the temp and the if share one scope; else-chains keep their
        // semantics because the else stays attached to the inner if.
        return SyntaxFactory.Block(tempDeclaration, newIf);
    }

    public override SyntaxNode? VisitCatchClause(CatchClauseSyntax node)
    {
        var visited = (CatchClauseSyntax)base.VisitCatchClause(node)!;
        if (!_catchesByNode.TryGetValue(node, out var plan))
        {
            return visited;
        }

        var declaration = SyntaxFactory.CatchDeclaration(
            SyntaxFactory.ParseTypeName("global::System.Exception"),
            SyntaxFactory.Identifier(plan.ExceptionVariableName));
        var filter = SyntaxFactory.CatchFilterClause(
            SyntaxFactory.Token(SyntaxKind.WhenKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            SyntaxFactory.Token(SyntaxKind.OpenParenToken),
            SyntaxFactory.ParseExpression(
                $"typeof({plan.Param.Name}).IsAssignableFrom({plan.ExceptionVariableName}.GetType())"),
            SyntaxFactory.Token(SyntaxKind.CloseParenToken));

        var statements = new List<StatementSyntax>();
        if (plan.VariableName != null)
        {
            statements.Add(SyntaxFactory.ParseStatement(
                $"{plan.Param.Name} {plan.VariableName} = ({plan.Param.Name})(object){plan.ExceptionVariableName};"));
        }

        if (visited.Block != null)
        {
            statements.AddRange(visited.Block.Statements);
        }

        _stats.CatchTypeParamsRewritten++;
        return visited
            .WithDeclaration(declaration)
            .WithFilter(filter)
            .WithBlock(SyntaxFactory.Block(statements));
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (_staticMethodsByNode.TryGetValue(node, out var staticPlan))
        {
            visited = AttachTypeParameters(visited, staticPlan);
            _stats.StaticMethodsRewritten++;
        }

        if (!_anchorsByNode.TryGetValue(node, out var plan))
        {
            return visited;
        }

        if (plan.MaterializeBody)
        {
            return MaterializeStubBody(visited, plan);
        }

        visited = WrapExpressionBody(visited);
        if (visited.Body == null)
        {
            return visited;
        }

        var toInsert = AnchorsToInsert(visited, plan);
        if (toInsert.Count == 0)
        {
            return visited;
        }

        var newBody = visited.Body.WithStatements(visited.Body.Statements.InsertRange(0, toInsert));
        return visited.WithBody(newBody);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (!_staticCallSites.TryGetValue(node, out var callSite)) return visited;
        var nameText = node.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };
        if (nameText == null) return visited;
        var genericName = SyntaxFactory.GenericName(
            SyntaxFactory.Identifier(nameText),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList<TypeSyntax>(
                callSite.TypeArgumentTexts.Select(text => SyntaxFactory.ParseTypeName(text)))));
        var expression = visited.Expression switch
        {
            MemberAccessExpressionSyntax m => m.WithName(genericName.WithTriviaFrom(m.Name)),
            IdentifierNameSyntax id => genericName.WithTriviaFrom(id),
            _ => visited.Expression,
        };
        _stats.CallSitesUpdated++;
        return visited.WithExpression(expression);
    }

    /// <summary>
    /// Steals the type-parameter list and constraint clauses from a scratch declaration —
    /// avoids hand-building clause trivia.
    /// </summary>
    private static MethodDeclarationSyntax AttachTypeParameters(MethodDeclarationSyntax method, StaticMethodPlan plan)
    {
        var names = string.Join(", ", plan.Params.Select(p => p.Name));
        var constraints = plan.ConstraintTexts.Count == 0 ? "" : " " + string.Join(" ", plan.ConstraintTexts);
        var scratch = SyntaxFactory.ParseMemberDeclaration(
            $"class __Scratch {{ void __M<{names}>(){constraints} {{ }} }}")!;
        var typeParameterList = scratch.DescendantNodes().OfType<TypeParameterListSyntax>().Single();
        var constraintClauses = scratch.DescendantNodes().OfType<TypeParameterConstraintClauseSyntax>().ToList();
        var updated = method.WithTypeParameterList(
            typeParameterList.WithTrailingTrivia(method.ParameterList.CloseParenToken.TrailingTrivia));
        if (constraintClauses.Count > 0)
        {
            updated = updated.WithConstraintClauses(
                SyntaxFactory.List(constraintClauses.Select((clause, index) =>
                    index == 0
                        ? clause.WithLeadingTrivia(
                            SyntaxFactory.ElasticCarriageReturnLineFeed,
                            SyntaxFactory.Whitespace("    "))
                        : clause)));
        }

        return updated;
    }

    private List<StatementSyntax> AnchorsToInsert(MethodDeclarationSyntax method, TokenAnchorPlan plan)
    {
        var result = new List<StatementSyntax>();
        var body = method.Body!;
        var existing = ExistingAnchorParameterNames(body);
        var usedNames = CollectIdentifierNames(method);

        foreach (var param in plan.Params)
        {
            if (existing.Contains(param.Name))
            {
                _stats.AnchorsReconciledFromE10++;
                continue;
            }

            var variable = UniqueName(GenericDeepenSyntaxFacts.AnchorVariableName(param.Name), usedNames);
            usedNames.Add(variable);
            result.Add(CreateAnchorStatement(variable, param.Name));
            _stats.TokenAnchorsInserted++;
        }

        return result;
    }

    /// <summary>Parameters already anchored by E10 sentinels or earlier anchors in leading statements.</summary>
    private static HashSet<string> ExistingAnchorParameterNames(BlockSyntax body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in body.Statements)
        {
            if (statement is not LocalDeclarationStatementSyntax local) break;
            foreach (var variable in local.Declaration.Variables)
            {
                if ((variable.Identifier.ValueText.StartsWith(GenericDeepenSyntaxFacts.AnchorPrefix, StringComparison.Ordinal)
                     || variable.Identifier.ValueText.StartsWith(GenericDeepenSyntaxFacts.E10SentinelPrefix, StringComparison.Ordinal))
                    && variable.Initializer?.Value is TypeOfExpressionSyntax typeOf
                    && typeOf.Type is IdentifierNameSyntax identifier)
                {
                    names.Add(identifier.Identifier.ValueText);
                }
            }
        }

        return names;
    }

    private static HashSet<string> CollectIdentifierNames(MethodDeclarationSyntax method)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in method.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
        {
            names.Add(token.ValueText);
        }

        return names;
    }

    private static string UniqueName(string baseName, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = baseName + "_" + i;
            if (!usedNames.Contains(candidate)) return candidate;
        }
    }

    internal static StatementSyntax CreateAnchorStatement(string variableName, string paramText)
        => SyntaxFactory.ParseStatement($"global::System.Type {variableName} = typeof({paramText});")
            .WithLeadingTrivia(
                SyntaxFactory.ElasticMarker,
                SyntaxFactory.Comment($"// __cs2j: token anchor ({paramText})"),
                SyntaxFactory.ElasticMarker)
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

    /// <summary>
    /// Abstract/interface member whose family needs the token: materialize a stub body
    /// (E10 precedent — abstract becomes virtual; interface members gain a default body
    /// that throws, so no caller ever executes it).
    /// </summary>
    private MethodDeclarationSyntax MaterializeStubBody(MethodDeclarationSyntax method, TokenAnchorPlan plan)
    {
        var statements = new List<StatementSyntax>();
        foreach (var param in plan.Params)
        {
            // A bare `typeof(T);` is not a valid statement (CS0201); use the anchor
            // declaration form so the converter's typeof-scanner still sees the token.
            statements.Add(CreateAnchorStatement(
                GenericDeepenSyntaxFacts.AnchorVariableName(param.Name), param.Name));
        }

        statements.Add(SyntaxFactory.ParseStatement("throw new global::System.NotImplementedException();")
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed));

        var result = method
            .WithBody(SyntaxFactory.Block(statements))
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));

        var isAbstract = result.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
        if (isAbstract && !plan.IsInterfaceMember)
        {
            var modifiers = SyntaxFactory.TokenList(result.Modifiers.Select(m =>
                    m.IsKind(SyntaxKind.AbstractKeyword)
                        ? SyntaxFactory.Token(SyntaxKind.VirtualKeyword).WithTriviaFrom(m)
                        : m));
            result = result.WithModifiers(modifiers);
        }

        _stats.TokenAnchorsInserted += plan.Params.Count;
        return result;
    }

    private static MethodDeclarationSyntax WrapExpressionBody(MethodDeclarationSyntax method)
    {
        if (method.Body != null || method.ExpressionBody == null) return method;
        var @return = SyntaxFactory.ReturnStatement(
            SyntaxFactory.Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(SyntaxFactory.Space),
            method.ExpressionBody.Expression,
            SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var body = SyntaxFactory.Block(
            new SyntaxList<StatementSyntax>(
                @return.WithLeadingTrivia(SyntaxFactory.Whitespace("    ")).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed)));
        return method.WithBody(body)
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }
}
