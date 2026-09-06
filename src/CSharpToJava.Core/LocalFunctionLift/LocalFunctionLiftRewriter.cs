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
        // Before visiting, record which statement indices in the original
        // block are non-skipped local functions that should be removed.
        // We must use the original (pre-visit) statements because after
        // base.VisitBlock returns, child nodes may have been rewritten
        // (e.g. call sites renamed), causing SpanStart to become 0 for
        // newly constructed nodes. Using the original node reference
        // for identity comparison is the most reliable approach.
        var nonSkippedNodes = new HashSet<SyntaxNode>(
            _plans.Where(p => !p.Skipped)
                  .Select(p => (SyntaxNode)p.Node));

        // Also build a set of SpanStart values from the original block's
        // local function statements as a fallback for nodes that survive
        // without rewriting (same reference).
        var indicesToRemove = new HashSet<int>();
        for (var i = 0; i < node.Statements.Count; i++)
        {
            if (node.Statements[i] is LocalFunctionStatementSyntax localFn
                && nonSkippedNodes.Contains(localFn))
            {
                indicesToRemove.Add(i);
            }
        }

        // First, visit children (call sites get rewritten inside this block).
        var visited = (BlockSyntax?)base.VisitBlock(node);
        if (visited == null)
            return null;

        if (indicesToRemove.Count == 0)
            return visited;

        // If no statements were removed/added by visiting (count is same),
        // we can safely use the original indices.
        if (visited.Statements.Count == node.Statements.Count)
        {
            var newStatements = new List<StatementSyntax>();
            for (var i = 0; i < visited.Statements.Count; i++)
            {
                if (indicesToRemove.Contains(i))
                {
                    Changed = true;
                    continue;
                }
                newStatements.Add(visited.Statements[i]);
            }
            return visited.WithStatements(SyntaxFactory.List(newStatements));
        }

        // Fallback: match by SpanStart (works when nodes were not rewritten)
        var nonSkippedKeys = new HashSet<(int start, int length)>(
            _plans.Where(p => !p.Skipped)
                  .Select(p => (p.Node.SpanStart, p.Node.Span.Length)));

        var fallbackStatements = new List<StatementSyntax>();
        foreach (var statement in visited.Statements)
        {
            if (statement is LocalFunctionStatementSyntax localFn)
            {
                var key = (localFn.SpanStart, localFn.Span.Length);
                if (nonSkippedKeys.Contains(key))
                {
                    Changed = true;
                    continue;
                }
            }
            fallbackStatements.Add(statement);
        }

        if (fallbackStatements.Count != visited.Statements.Count)
        {
            visited = visited.WithStatements(SyntaxFactory.List(fallbackStatements));
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
            else if (symbol is IMethodSymbol method
                     && !method.Equals(method.OriginalDefinition, SymbolEqualityComparer.Default)
                     && _symbolToPlan.TryGetValue(method.OriginalDefinition, out var p2))
            {
                plan = p2;
            }
            else if (symbol == null && _nameToPlan.TryGetValue(id.Identifier.Text, out var p3))
            {
                plan = p3;
            }
        }
        else if (visited.Expression is GenericNameSyntax gn)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p))
            {
                plan = p;
            }
            else if (symbol is IMethodSymbol method
                     && !method.Equals(method.OriginalDefinition, SymbolEqualityComparer.Default)
                     && _symbolToPlan.TryGetValue(method.OriginalDefinition, out var p2))
            {
                plan = p2;
            }
            else if (symbol == null && _nameToPlan.TryGetValue(gn.Identifier.Text, out var p3))
            {
                plan = p3;
            }
        }

        if (plan == null)
            return visited;

        var newExpression = RewriteCallExpression(visited.Expression, plan);

        // If the hoisted method needs explicit type arguments (because it uses
        // enclosing method type parameters), and the call expression is a
        // simple IdentifierName (not already a GenericName), convert it to a
        // GenericName with the enclosing type parameters as type arguments.
        if (plan.NeedsExplicitTypeArguments && newExpression is IdentifierNameSyntax)
        {
            var typeArgs = SyntaxFactory.SeparatedList(
                plan.EnclosingTypeParams.Select(tp =>
                    SyntaxFactory.ParseTypeName(tp)));
            newExpression = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(plan.NewName),
                SyntaxFactory.TypeArgumentList(typeArgs));
        }

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
        // Try to resolve the symbol for this identifier
        var symbol = _model.GetSymbolInfo(node).Symbol;
        LiftPlan? plan = null;

        if (symbol != null && _symbolToPlan.TryGetValue(symbol, out var p1))
        {
            plan = p1;
        }
        else if (symbol is IMethodSymbol method
                 && !method.Equals(method.OriginalDefinition, SymbolEqualityComparer.Default)
                 && _symbolToPlan.TryGetValue(method.OriginalDefinition, out var p2))
        {
            plan = p2;
        }
        else if (symbol == null && _nameToPlan.TryGetValue(node.Identifier.Text, out var p3))
        {
            plan = p3;
        }

        if (plan == null)
            return base.VisitIdentifierName(node);

        // Skip if this is a member access expression on the left
        // (e.g. this.x or obj.x where x matches a hoisted name)
        if (node.Parent is MemberAccessExpressionSyntax mae && mae.Name == node)
            return base.VisitIdentifierName(node);

        // Skip if inside a NameColon (named argument)
        if (node.Parent is NameColonSyntax)
            return base.VisitIdentifierName(node);

        // Determine if this is an invocation call site or a method group reference.
        // If the parent is an InvocationExpression and this node is the expression
        // being invoked, it's a call site — handled by VisitInvocationExpression.
        if (node.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression == node)
            return base.VisitIdentifierName(node);

        // If the parent is a GenericName, it's handled by VisitInvocationExpression
        // through the GenericName path.
        if (node.Parent is GenericNameSyntax)
            return base.VisitIdentifierName(node);

        // Otherwise, this is a method group / delegate conversion reference.
        // Rename it to the hoisted method name.
        Changed = true;
        return SyntaxFactory.IdentifierName(plan.NewName).WithTriviaFrom(node);
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
        if (enclosingMethodParams.Count > 0)
        {
            // Only add enclosing method type parameters that are actually
            // used in the local function's body, parameter types, or return
            // type. Adding unused type parameters would break type inference
            // at call sites.
            var usedTypeParams = GetTypeParametersUsedInLocalFunction(localFn);

            foreach (var tpName in enclosingMethodParams)
            {
                if (typeParams.Any(tp => tp.Identifier.Text == tpName))
                    continue;
                if (!usedTypeParams.Contains(tpName))
                    continue;

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

    /// <summary>
    /// Collects the set of type parameter names used in the local function's
    /// body, parameter types, and return type. This is used to determine
    /// which enclosing method type parameters need to be added to the hoisted
    /// method's type parameter list.
    /// </summary>
    private static HashSet<string> GetTypeParametersUsedInLocalFunction(
        LocalFunctionStatementSyntax localFn)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        // Collect from parameter types (including the type node itself,
        // since DescendantNodes does not include the node itself)
        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
            {
                CollectIdentifierNamesFromType(param.Type, used);
            }
        }

        // Collect from return type
        CollectIdentifierNamesFromType(localFn.ReturnType, used);

        // Collect from body
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body != null)
        {
            foreach (var id in body.DescendantNodes()
                .OfType<IdentifierNameSyntax>())
            {
                used.Add(id.Identifier.Text);
            }
        }

        return used;
    }

    /// <summary>
    /// Collects all IdentifierName text from a type syntax node, including
    /// the node itself if it is an IdentifierNameSyntax. This is needed
    /// because DescendantNodes() does not include the node itself.
    /// </summary>
    private static void CollectIdentifierNamesFromType(
        TypeSyntax? type, HashSet<string> names)
    {
        if (type == null)
            return;
        if (type is IdentifierNameSyntax id)
            names.Add(id.Identifier.Text);
        foreach (var descendant in type.DescendantNodes().OfType<IdentifierNameSyntax>())
            names.Add(descendant.Identifier.Text);
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

        var renamer = new CaptureRenamer(plan.Captures);
        return (BlockSyntax)renamer.Visit(body)!;
    }

    private ExpressionSyntax RenameCapturesInExpression(ExpressionSyntax expr, LiftPlan plan)
    {
        if (plan.Captures.Count == 0)
            return expr;

        var renamer = new CaptureRenamer(plan.Captures);
        return (ExpressionSyntax)renamer.Visit(expr)!;
    }

    /// <summary>
    /// Renames captured variable references inside hoisted method bodies.
    /// Uses name-based matching instead of SemanticModel because the body
    /// may have already been rewritten by Visit(), producing nodes that are
    /// not part of the original syntax tree.
    /// </summary>
    private sealed class CaptureRenamer : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _renameMap;

        public CaptureRenamer(List<CapturePlan> captures)
        {
            _renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cap in captures)
                _renameMap[cap.OriginalName] = cap.ParameterName;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Parent is NameColonSyntax)
                return node;

            var name = node.Identifier.Text;
            if (_renameMap.TryGetValue(name, out var newName))
            {
                // Skip if this identifier is a member access on the left
                // (e.g. this.x or obj.x where x happens to match a capture name).
                if (node.Parent is MemberAccessExpressionSyntax mae
                    && mae.Name == node)
                    return base.VisitIdentifierName(node);

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
