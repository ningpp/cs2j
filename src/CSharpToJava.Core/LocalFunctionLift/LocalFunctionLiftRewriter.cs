using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// CSharpSyntaxRewriter that performs the actual syntax transformations:
/// 1. Deletes local function declarations (from their containing block)
/// 2. Rewrites call sites: renames + prepends capture arguments
/// 3. Renames captured variable references inside hoisted method bodies
/// 4. Inserts new MethodDeclarationSyntax into the host type
/// </summary>
internal sealed class LocalFunctionLiftRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly List<LiftPlan> _plans;
    private readonly LocalFunctionLiftOptions _options;
    private readonly LocalFunctionLiftStatistics _stats;

    private readonly Dictionary<ISymbol, LiftPlan> _symbolToPlan;
    private readonly Dictionary<string, LiftPlan> _nameToPlan;
    private readonly Dictionary<TypeDeclarationSyntax, List<MethodDeclarationSyntax>> _methodsToInsert;

    public bool Changed { get; private set; }

    public LocalFunctionLiftRewriter(
        SemanticModel model,
        List<LiftPlan> plans,
        LocalFunctionLiftOptions options,
        LocalFunctionLiftStatistics stats)
    {
        _model = model;
        _plans = plans;
        _options = options;
        _stats = stats;
        _symbolToPlan = new Dictionary<ISymbol, LiftPlan>(SymbolEqualityComparer.Default);
        _nameToPlan = new Dictionary<string, LiftPlan>(StringComparer.Ordinal);
        _methodsToInsert = new Dictionary<TypeDeclarationSyntax, List<MethodDeclarationSyntax>>();

        foreach (var plan in plans.Where(p => !p.Skipped))
        {
            if (plan.Symbol != null)
                _symbolToPlan[plan.Symbol] = plan;
            _nameToPlan[plan.Node.Identifier.Text] = plan;
        }
    }

    public void PrepareMethodDeclarations()
    {
        var byHost = _plans
            .Where(p => !p.Skipped && p.HostType != null)
            .GroupBy(p => p.HostType!);

        foreach (var group in byHost)
        {
            var hostType = group.Key;
            var methods = new List<MethodDeclarationSyntax>();
            foreach (var plan in group)
            {
                var method = BuildMethodDeclaration(plan);
                methods.Add(method);
                _stats.LocalFunctionsLifted++;
                foreach (var cap in plan.Captures)
                {
                    if (cap.IsWritten)
                        _stats.CapturesByRef++;
                    else
                        _stats.CapturesByValue++;
                }
            }
            _methodsToInsert[hostType] = methods;
        }
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitInterfaceDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => VisitTypeDeclarationCore(node, base.VisitRecordDeclaration(node));

    private TypeDeclarationSyntax? VisitTypeDeclarationCore(
        TypeDeclarationSyntax originalNode, SyntaxNode? visitedNode)
    {
        if (visitedNode is not TypeDeclarationSyntax visited)
            return null;

        if (_methodsToInsert.TryGetValue(originalNode, out var methods))
        {
            var members = visited.Members.ToList();
            var insertIndex = 0;
            for (var i = members.Count - 1; i >= 0; i--)
            {
                if (members[i] is FieldDeclarationSyntax)
                {
                    insertIndex = i + 1;
                    break;
                }
            }

            for (var i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                if (insertIndex > 0 || i > 0)
                {
                    var existingTrivia = method.GetLeadingTrivia();
                    var newTrivia = SyntaxFactory.TriviaList(
                        SyntaxFactory.CarriageReturnLineFeed,
                        SyntaxFactory.CarriageReturnLineFeed);
                    newTrivia = newTrivia.AddRange(existingTrivia);
                    method = method.WithLeadingTrivia(newTrivia);
                }
                members.Insert(insertIndex + i, method);
            }

            visited = visited.WithMembers(SyntaxFactory.List(members));
            Changed = true;
        }

        return visited;
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        // First, visit children (call sites get rewritten inside this block).
        // We need to track which local functions to remove by name, because
        // base.VisitBlock may rebuild LocalFunctionStatementSyntax nodes,
        // breaking reference equality with plan.Node.
        var visited = (BlockSyntax?)base.VisitBlock(node);
        if (visited == null)
            return null;

        var skipNames = new HashSet<string>(
            _plans.Where(p => !p.Skipped).Select(p => p.Node.Identifier.Text),
            StringComparer.Ordinal);

        var newStatements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
        {
            if (statement is LocalFunctionStatementSyntax localFn
                && skipNames.Contains(localFn.Identifier.Text))
            {
                Changed = true;
                continue;
            }
            newStatements.Add(statement);
        }

        if (newStatements.Count != visited.Statements.Count)
        {
            visited = visited.WithStatements(SyntaxFactory.List(newStatements));
        }

        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
        if (visited == null)
            return null;

        LiftPlan? plan = null;

        if (visited.Expression is IdentifierNameSyntax id)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p))
            {
                plan = p;
            }
            else if (_nameToPlan.TryGetValue(id.Identifier.Text, out var p2))
            {
                plan = p2;
            }
        }
        else if (visited.Expression is GenericNameSyntax gn)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p))
            {
                plan = p;
            }
            else if (_nameToPlan.TryGetValue(gn.Identifier.Text, out var p2))
            {
                plan = p2;
            }
        }

        if (plan == null)
            return visited;

        var newExpression = RewriteCallExpression(visited.Expression, plan);
        var newArgs = RewriteCallArguments(visited.ArgumentList, plan);

        Changed = true;
        return visited.WithExpression(newExpression).WithArgumentList(newArgs);
    }

    private static ExpressionSyntax RewriteCallExpression(ExpressionSyntax expression, LiftPlan plan)
    {
        if (expression is IdentifierNameSyntax id)
        {
            return SyntaxFactory.IdentifierName(plan.NewName).WithTriviaFrom(id);
        }
        if (expression is GenericNameSyntax gn)
        {
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(plan.NewName),
                gn.TypeArgumentList).WithTriviaFrom(gn);
        }
        return expression;
    }

    private static ArgumentListSyntax RewriteCallArguments(ArgumentListSyntax argumentList, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return argumentList;

        var newArgs = new List<ArgumentSyntax>();

        foreach (var cap in plan.Captures)
        {
            var argExpr = SyntaxFactory.IdentifierName(cap.OriginalName);
            if (cap.IsWritten)
            {
                newArgs.Add(SyntaxFactory.Argument(
                    default,
                    SyntaxFactory.Token(SyntaxKind.RefKeyword),
                    argExpr));
            }
            else
            {
                newArgs.Add(SyntaxFactory.Argument(argExpr));
            }
        }

        foreach (var arg in argumentList.Arguments)
        {
            newArgs.Add(arg);
        }

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs));
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (IsInsideNameOf(node))
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var plan))
            {
                Changed = true;
                return SyntaxFactory.IdentifierName(plan.NewName).WithTriviaFrom(node);
            }
            if (_nameToPlan.TryGetValue(node.Identifier.Text, out var plan2))
            {
                Changed = true;
                return SyntaxFactory.IdentifierName(plan2.NewName).WithTriviaFrom(node);
            }
        }

        return base.VisitIdentifierName(node);
    }

    private static bool IsInsideNameOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == "nameof")
                return true;
            current = current.Parent;
        }
        return false;
    }

    private MethodDeclarationSyntax BuildMethodDeclaration(LiftPlan plan)
    {
        var localFn = plan.Node;

        var modifierTokens = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
        };
        if (plan.IsStatic)
            modifierTokens.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            modifierTokens.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)))
            modifierTokens.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
        var modifiers = SyntaxFactory.TokenList(modifierTokens);

        var returnType = localFn.ReturnType ?? SyntaxFactory.PredefinedType(
            SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        var typeParams = BuildTypeParameterList(plan);
        var parameters = BuildParameterList(plan);
        var constraints = BuildConstraintClauses(plan);

        BlockSyntax? body = null;

        if (localFn.Body != null)
        {
            body = (BlockSyntax)Visit(localFn.Body)!;
            body = RenameCapturesInBody(body, plan);
        }
        else if (localFn.ExpressionBody != null)
        {
            var expr = localFn.ExpressionBody.Expression;
            var visitedExpr = (ExpressionSyntax)Visit(expr)!;
            visitedExpr = RenameCapturesInExpression(visitedExpr, plan);

            var isVoid = returnType.IsKind(SyntaxKind.VoidKeyword)
                || (returnType is PredefinedTypeSyntax pt && pt.Keyword.IsKind(SyntaxKind.VoidKeyword));

            StatementSyntax stmt;
            if (isVoid)
            {
                stmt = SyntaxFactory.ExpressionStatement(visitedExpr);
            }
            else
            {
                stmt = SyntaxFactory.ReturnStatement(visitedExpr);
            }
            body = SyntaxFactory.Block(stmt);
        }

        var leadingTrivia = localFn.GetLeadingTrivia();

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List<AttributeListSyntax>(),
            SyntaxFactory.TokenList(modifiers),
            returnType,
            null,
            SyntaxFactory.Identifier(plan.NewName),
            typeParams,
            parameters,
            constraints,
            body,
            null,
            default);

        if (leadingTrivia.Count > 0)
            method = method.WithLeadingTrivia(leadingTrivia);

        return method;
    }

    private static TypeParameterListSyntax? BuildTypeParameterList(LiftPlan plan)
    {
        var localFn = plan.Node;
        var typeParams = new List<TypeParameterSyntax>();

        if (localFn.TypeParameterList != null)
        {
            foreach (var tp in localFn.TypeParameterList.Parameters)
                typeParams.Add(tp);
        }

        var enclosingMethodParams = GetEnclosingMethodTypeParameters(localFn);
        foreach (var tpName in enclosingMethodParams)
        {
            if (!typeParams.Any(tp => tp.Identifier.Text == tpName))
            {
                typeParams.Add(SyntaxFactory.TypeParameter(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    default,
                    SyntaxFactory.Identifier(tpName)));
            }
        }

        if (typeParams.Count == 0)
            return null;

        return SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(typeParams));
    }

    private ParameterListSyntax BuildParameterList(LiftPlan plan)
    {
        var localFn = plan.Node;
        var parameters = new List<ParameterSyntax>();

        if (!_options.CapturesAtEnd)
        {
            foreach (var cap in plan.Captures)
            {
                parameters.Add(BuildCaptureParameter(cap));
            }
        }

        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
                parameters.Add(param);
        }

        if (_options.CapturesAtEnd)
        {
            foreach (var cap in plan.Captures)
            {
                parameters.Add(BuildCaptureParameter(cap));
            }
        }

        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters));
    }

    private static ParameterSyntax BuildCaptureParameter(CapturePlan cap)
    {
        return SyntaxFactory.Parameter(
            SyntaxFactory.List<AttributeListSyntax>(),
            cap.IsWritten
                ? SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword))
                : SyntaxFactory.TokenList(),
            SyntaxFactory.ParseTypeName(cap.TypeDisplay),
            SyntaxFactory.Identifier(cap.ParameterName),
            null);
    }

    private static SyntaxList<TypeParameterConstraintClauseSyntax> BuildConstraintClauses(LiftPlan plan)
    {
        var localFn = plan.Node;
        var clauses = new List<TypeParameterConstraintClauseSyntax>();

        if (localFn.ConstraintClauses != null)
        {
            foreach (var clause in localFn.ConstraintClauses)
                clauses.Add(clause);
        }

        var enclosingMethod = FindEnclosingMethod(plan.Node);
        if (enclosingMethod?.ConstraintClauses != null)
        {
            foreach (var clause in enclosingMethod.ConstraintClauses)
                clauses.Add(clause);
        }

        return SyntaxFactory.List(clauses);
    }

    private static MethodDeclarationSyntax? FindEnclosingMethod(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is MethodDeclarationSyntax method)
                return method;
            if (current is TypeDeclarationSyntax)
                return null;
            current = current.Parent;
        }
        return null;
    }

    private static List<string> GetEnclosingMethodTypeParameters(LocalFunctionStatementSyntax localFn)
    {
        var result = new List<string>();
        var method = FindEnclosingMethod(localFn);
        if (method?.TypeParameterList != null)
        {
            foreach (var tp in method.TypeParameterList.Parameters)
                result.Add(tp.Identifier.Text);
        }
        return result;
    }

    private BlockSyntax RenameCapturesInBody(BlockSyntax body, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return body;

        var renamer = new CaptureRenamer(_model, plan.Captures);
        return (BlockSyntax)renamer.Visit(body)!;
    }

    private ExpressionSyntax RenameCapturesInExpression(ExpressionSyntax expr, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return expr;

        var renamer = new CaptureRenamer(_model, plan.Captures);
        return (ExpressionSyntax)renamer.Visit(expr)!;
    }

    private sealed class CaptureRenamer : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly Dictionary<ISymbol, string> _renameMap;

        public CaptureRenamer(SemanticModel model, List<CapturePlan> captures)
        {
            _model = model;
            _renameMap = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            foreach (var cap in captures)
                _renameMap[cap.Symbol] = cap.ParameterName;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is NameColonSyntax)
                return node;

            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _renameMap.TryGetValue(symbol, out var newName))
            {
                return SyntaxFactory.IdentifierName(newName).WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            return node;
        }
    }
}
