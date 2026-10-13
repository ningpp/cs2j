using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Family-wide single-evaluation temp-lifting machinery, generalized from the
/// C47 equality-concat TempLifter. Pass 1 (each rule's rewriter) collects
/// <see cref="PendingTemp"/> entries carrying a <b>statement sequence</b>
/// (declaration + Add/AddRange/foreach statements, not just one declaration);
/// <see cref="Apply"/> (Pass 2) inserts the sequences at their anchors.
/// All rules share the <c>__ed_</c> prefix and one per-file numbering space
/// (design hard invariant #4).
/// </summary>
internal static class ExpressionDesugarTempLifter
{
    /// <summary>Annotation kind used to keep anchor nodes locatable after rewrites.</summary>
    public const string AnchorAnnotationKind = "ed-anchor";

    internal sealed record PendingTemp(
        IReadOnlyList<StatementSyntax> Statements,
        SyntaxNode AnchorNode,
        int Order);

    /// <summary>
    /// Finds the nearest enclosing statement, which serves as the insertion
    /// anchor. When the site is in an expression-bodied member, the
    /// <see cref="ArrowExpressionClauseSyntax"/> is returned (the clause is
    /// converted to a block); when the site is in an EXPRESSION-BODIED lambda
    /// (or anonymous method), the <see cref="AnonymousFunctionExpressionSyntax"/>
    /// is returned (the lambda is converted to a block body, preserving its
    /// deferred evaluation instead of hoisting past the lambda boundary).
    /// Block-bodied lambdas have ordinary inner statements, so they are
    /// unaffected. Returns null in truly anchor-free contexts (field/property
    /// initializers, attribute arguments, default parameter values, query
    /// clauses, ...).
    /// </summary>
    internal static SyntaxNode? FindAnchor(SyntaxNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is StatementSyntax stmt)
                return stmt;
            if (current is ArrowExpressionClauseSyntax arrow)
                return arrow;
            if (current is AnonymousFunctionExpressionSyntax
                {
                    Body: ExpressionSyntax,
                } lambda)
            {
                return lambda;
            }

            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Inserts all collected statement sequences. When the anchor is a direct
    /// statement of a <see cref="BlockSyntax"/>, statements are inserted before
    /// it; when the anchor is inside a top-level (global) statement, they are
    /// inserted as preceding global statements; when the anchor is an embedded
    /// statement (if/while/...) or a switch-section statement, the anchor is
    /// wrapped in a block carrying the inserted statements, keeping scope and
    /// evaluation order intact.
    /// </summary>
    internal static CompilationUnitSyntax Apply(
        CompilationUnitSyntax root,
        List<PendingTemp> pending,
        IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> anchorAnnotations,
        SemanticModel? semanticModel = null)
    {
        if (pending.Count == 0)
            return root;

        var byAnchor = pending
            .GroupBy(p => p.AnchorNode)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<StatementSyntax>)g
                    .OrderBy(p => p.Order)
                    .SelectMany(p => p.Statements)
                    .ToList());

        SyntaxNode current = root;
        foreach (var (anchor, statements) in byAnchor)
        {
            if (!anchorAnnotations.TryGetValue(anchor, out var annotation))
                continue;

            var currentAnchor = current.GetAnnotatedNodes(annotation)
                .FirstOrDefault();
            if (currentAnchor == null)
                continue;

            if (currentAnchor is VariableDeclaratorSyntax
                or PropertyDeclarationSyntax)
            {
                current = ApplyBuilderMethod(current, currentAnchor, statements);
                continue;
            }

            if (currentAnchor is ArrowExpressionClauseSyntax arrow)
            {
                current = ExpandExpressionBody(current, arrow, statements);
                continue;
            }

            if (currentAnchor is AnonymousFunctionExpressionSyntax
                {
                    Body: ExpressionSyntax,
                } lambda)
            {
                // The semantic model is bound to the ORIGINAL tree; resolve the
                // delegate return type from the original anchor instance while
                // replacing the refreshed current-tree instance.
                var originalLambda = anchor as AnonymousFunctionExpressionSyntax;
                current = ExpandLambdaBody(
                    current, lambda, originalLambda, statements, semanticModel);
                continue;
            }

            if (currentAnchor.Parent is BlockSyntax block)
            {
                var anchorStatement = (StatementSyntax)currentAnchor;
                var index = block.Statements.IndexOf(anchorStatement);
                var newStatements = block.Statements
                    .Take(index)
                    .Concat(statements)
                    .Concat(block.Statements.Skip(index));
                var newBlock = block.WithStatements(
                    SyntaxFactory.List(newStatements));
                current = current.ReplaceNode(block, newBlock);
            }
            else if (currentAnchor.Parent is GlobalStatementSyntax global
                     && global.Parent is CompilationUnitSyntax unit)
            {
                // Top-level statements: the anchor lives inside a global
                // statement. Insert the lifted sequences as preceding global
                // statements (same synthesized Main scope); wrapping them in a
                // nested block would hide locals from later global statements.
                var index = unit.Members.IndexOf(global);
                var liftedGlobals = statements
                    .Select(s => (MemberDeclarationSyntax)
                        SyntaxFactory.GlobalStatement(s));
                var newMembers = unit.Members
                    .Take(index)
                    .Concat(liftedGlobals)
                    .Concat(unit.Members.Skip(index));
                var newUnit = unit.WithMembers(
                    SyntaxFactory.List(newMembers));
                current = current.ReplaceNode(unit, newUnit);
            }
            else
            {
                var anchorStatement = (StatementSyntax)currentAnchor;
                var wrapped = SyntaxFactory.Block(
                    SyntaxFactory.List(
                        statements.Concat(new[] { anchorStatement })));
                current = current.ReplaceNode(currentAnchor, wrapped);
            }
        }

        return (CompilationUnitSyntax)current;
    }

    /// <summary>
    /// Field/event-field/property initializer lowering (anchor-free for
    /// statement lifting): the lifted sequence plus <c>return &lt;rhs&gt;;</c>
    /// becomes a private static builder method on the enclosing type, and the
    /// initializer value (already rewritten to reference the lifted temps) is
    /// replaced by the builder invocation. A static method is semantically
    /// always sufficient because field/property initializers cannot reference
    /// instance members (CS0236).
    /// </summary>
    private static SyntaxNode ApplyBuilderMethod(
        SyntaxNode current, SyntaxNode anchor, IReadOnlyList<StatementSyntax> statements)
    {
        TypeSyntax returnType;
        string identifier;
        SyntaxNode replaceable;
        TypeDeclarationSyntax enclosingType;

        if (anchor is VariableDeclaratorSyntax declarator
            && declarator.Parent is VariableDeclarationSyntax declaration
            && declaration.Parent is MemberDeclarationSyntax)
        {
            returnType = declaration.Type;
            identifier = declarator.Identifier.Text;
            replaceable = declarator;
            enclosingType = (TypeDeclarationSyntax)declaration.Parent.Parent!;
        }
        else if (anchor is PropertyDeclarationSyntax property)
        {
            returnType = property.Type;
            identifier = property.Identifier.Text;
            replaceable = property;
            enclosingType = (TypeDeclarationSyntax)property.Parent!;
        }
        else
        {
            return current;
        }

        var initializer = replaceable switch
        {
            VariableDeclaratorSyntax d => d.Initializer,
            PropertyDeclarationSyntax p => p.Initializer,
            _ => null,
        };
        if (initializer?.Value is not { } returnExpression)
            return current;

        var tracked = current.TrackNodes(enclosingType, replaceable);
        var currentReplaceable = tracked.GetCurrentNode(replaceable);
        if (currentReplaceable == null)
            return current;

        var currentType = tracked.GetCurrentNode(enclosingType);
        if (currentType == null)
            return current;

        var methodName = UniqueBuilderMethodName(currentType, identifier);
        var method = BuildBuilderMethod(
            methodName, returnType, statements, returnExpression);
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(methodName));

        var updatedReplaceable = currentReplaceable switch
        {
            VariableDeclaratorSyntax d =>
                d.WithInitializer(SyntaxFactory.EqualsValueClause(invocation)),
            PropertyDeclarationSyntax p =>
                p.WithInitializer(SyntaxFactory.EqualsValueClause(invocation)),
            _ => currentReplaceable,
        };

        tracked = tracked.ReplaceNode(currentReplaceable, updatedReplaceable);
        currentType = tracked.GetCurrentNode(enclosingType)!;
        tracked = tracked.ReplaceNode(
            currentType, currentType.AddMembers(method));
        return tracked;
    }

    private static MethodDeclarationSyntax BuildBuilderMethod(
        string name, TypeSyntax returnType,
        IReadOnlyList<StatementSyntax> statements, ExpressionSyntax returnExpression)
    {
        var bodyStatements = statements
            .Concat(new[] { SyntaxFactory.ReturnStatement(returnExpression) });
        return SyntaxFactory.MethodDeclaration(returnType, name)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.List(bodyStatements)));
    }

    /// <summary>
    /// Deterministic builder method name (<c>__cs2j_init_&lt;field&gt;</c>)
    /// made unique against every member name of the enclosing type.
    /// </summary>
    private static string UniqueBuilderMethodName(
        TypeDeclarationSyntax type, string identifier)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in type.Members)
        {
            switch (member)
            {
                case BaseFieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                        used.Add(variable.Identifier.Text);
                    break;
                case PropertyDeclarationSyntax property:
                    used.Add(property.Identifier.Text);
                    break;
                case MethodDeclarationSyntax methodDeclaration:
                    used.Add(methodDeclaration.Identifier.Text);
                    break;
                case EventDeclarationSyntax eventDeclaration:
                    used.Add(eventDeclaration.Identifier.Text);
                    break;
            }
        }

        var baseName = "__cs2j_init_" + SanitizeBuilderIdentifier(identifier);
        var candidate = baseName;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = baseName + "_" + suffix++;
        return candidate;
    }

    private static string SanitizeBuilderIdentifier(string identifier)
    {
        var trimmed = identifier.StartsWith("@", StringComparison.Ordinal)
            ? identifier.Substring(1)
            : identifier;
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(
                char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.Length == 0 ? "field" : builder.ToString();
    }

    /// <summary>
    /// Converts an expression-bodied member that needs lifted statements into
    /// a block body (or, for properties/indexers, a block-bodied get accessor).
    /// </summary>
    private static SyntaxNode ExpandExpressionBody(
        SyntaxNode current,
        ArrowExpressionClauseSyntax arrow,
        IReadOnlyList<StatementSyntax> statements)
    {
        var member = arrow.Parent!;
        var returnsValue = MemberReturnsValue(member);

        var allStatements = new List<StatementSyntax>(statements)
        {
            returnsValue
                ? SyntaxFactory.ReturnStatement(arrow.Expression)
                : SyntaxFactory.ExpressionStatement(arrow.Expression),
        };
        var block = SyntaxFactory.Block(SyntaxFactory.List(allStatements));

        SyntaxNode updated = member switch
        {
            MethodDeclarationSyntax m => m.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            LocalFunctionStatementSyntax l => l.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            OperatorDeclarationSyntax o => o.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            ConversionOperatorDeclarationSyntax c => c.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            ConstructorDeclarationSyntax ctor => ctor.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            DestructorDeclarationSyntax dtor => dtor.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            AccessorDeclarationSyntax a => a.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            PropertyDeclarationSyntax p => p.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(MakeGetAccessorList(block)),
            IndexerDeclarationSyntax i => i.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(MakeGetAccessorList(block)),
            _ => member,
        };

        return current.ReplaceNode(member, updated);
    }

    /// <summary>
    /// Converts an expression-bodied lambda carrying lifted statements into a
    /// block-bodied lambda. The tail statement is <c>return</c> when the
    /// lambda's inferred delegate returns a value (Func-style) and an
    /// expression statement when it is void (Action-style), keeping the body
    /// expression's semantics and the lifted statements at the lambda's own
    /// deferred evaluation position.
    /// </summary>
    private static SyntaxNode ExpandLambdaBody(
        SyntaxNode current,
        AnonymousFunctionExpressionSyntax lambda,
        AnonymousFunctionExpressionSyntax? originalLambda,
        IReadOnlyList<StatementSyntax> statements,
        SemanticModel? model)
    {
        if (lambda.Body is not ExpressionSyntax expression)
            return current;

        var allStatements = new List<StatementSyntax>(statements)
        {
            LambdaReturnsValue(originalLambda ?? lambda, model)
                ? SyntaxFactory.ReturnStatement(expression)
                : SyntaxFactory.ExpressionStatement(expression),
        };
        var block = SyntaxFactory.Block(SyntaxFactory.List(allStatements));

        // WithBlock keeps the old expression body node in place (it surfaces as
        // text after the block), so clear it explicitly — the body now lives in
        // the synthesized block tail.
        var updated = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax parenthesized
                => parenthesized.WithBlock(block).WithExpressionBody(null!),
            SimpleLambdaExpressionSyntax simple
                => simple.WithBlock(block).WithExpressionBody(null!),
            _ => lambda,
        };

        return current.ReplaceNode(lambda, updated);
    }

    /// <summary>
    /// True when the expression-bodied lambda's converted delegate type
    /// (Action vs Func/...) returns a value — the tail statement must then be
    /// <c>return …</c>; a void delegate gets a plain expression statement.
    /// When the delegate conversion cannot be resolved (e.g. the pass
    /// compilation lacks the target method's metadata, as with test-framework
    /// asserts), the body expression's own bound type decides: a void body can
    /// NEVER legally follow <c>return</c> (CS8030), so emitting a return there
    /// is always wrong even when the delegate is unknown. Only an unknown
    /// delegate combined with a value-producing body keeps the historical
    /// value-returning assumption.
    /// </summary>
    private static bool LambdaReturnsValue(
        AnonymousFunctionExpressionSyntax lambda, SemanticModel? model)
    {
        if (model == null)
            return BodyReturnsValue(null, lambda);

        var converted = model.GetTypeInfo(lambda).ConvertedType;
        if (converted is INamedTypeSymbol
            {
                DelegateInvokeMethod: { } invoke,
            })
        {
            return invoke.ReturnType.SpecialType != SpecialType.System_Void;
        }

        return BodyReturnsValue(model, lambda);
    }

    /// <summary>
    /// Fallback classification straight from the lambda body: <c>return E;</c>
    /// is legal only when <c>E</c> produces a value, so a void-returning body
    /// (e.g. an invocation of a void method) forces an expression statement.
    /// With no model at all the historical value-returning default is kept.
    /// </summary>
    private static bool BodyReturnsValue(
        SemanticModel? model, AnonymousFunctionExpressionSyntax lambda)
    {
        if (model == null || lambda.Body is not ExpressionSyntax body)
            return true;

        var bodyType = model.GetTypeInfo(body).Type;
        return bodyType is null || bodyType.SpecialType != SpecialType.System_Void;
    }

    private static AccessorListSyntax MakeGetAccessorList(BlockSyntax block)
        => SyntaxFactory.AccessorList(
            SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(
                        SyntaxKind.GetAccessorDeclaration)
                    .WithBody(block)));

    private static bool MemberReturnsValue(SyntaxNode member)
        => member switch
        {
            MethodDeclarationSyntax m => !IsVoid(m.ReturnType),
            LocalFunctionStatementSyntax l => !IsVoid(l.ReturnType),
            OperatorDeclarationSyntax => true,
            ConversionOperatorDeclarationSyntax => true,
            PropertyDeclarationSyntax => true,
            IndexerDeclarationSyntax => true,
            AccessorDeclarationSyntax a =>
                !a.IsKind(SyntaxKind.SetAccessorDeclaration)
                && !a.IsKind(SyntaxKind.AddAccessorDeclaration)
                && !a.IsKind(SyntaxKind.RemoveAccessorDeclaration)
                && !a.IsKind(SyntaxKind.InitAccessorDeclaration),
            _ => false,
        };

    private static bool IsVoid(TypeSyntax returnType)
        => returnType is PredefinedTypeSyntax predefined
           && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);

    /// <summary>Builds <c>var name = initializer;</c>.</summary>
    internal static LocalDeclarationStatementSyntax DeclareVar(
        string name, ExpressionSyntax initializer)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(name),
                        null,
                        SyntaxFactory.EqualsValueClause(initializer)))));

    /// <summary>Wraps one temp declaration as a single-statement pending entry.</summary>
    internal static PendingTemp ForDeclaration(
        LocalDeclarationStatementSyntax declaration,
        SyntaxNode anchor,
        int order)
        => new(new[] { declaration }, anchor, order);
}

/// <summary>
/// File-scoped temp-name allocator with collision avoidance. Names use the
/// family <c>__ed_</c> prefix; existing local declarations in the file seed
/// the used-name set.
/// </summary>
internal sealed class ExpressionDesugarTempNameAllocator
{
    private readonly HashSet<string> _used;
    private int _counter;

    public ExpressionDesugarTempNameAllocator(CompilationUnitSyntax root)
    {
        _used = new HashSet<string>(StringComparer.Ordinal);
        // Seeds BOTH local declarators (temps live in their statement scope)
        // and field/event declarators (temps may live in a synthesized builder
        // method's member scope), so a __ed_n name can never shadow either.
        foreach (var declarator in root.DescendantNodes()
                     .OfType<VariableDeclaratorSyntax>())
        {
            _used.Add(declarator.Identifier.Text);
        }
    }

    /// <summary>Allocates a fresh <c>__ed_n</c> name not present in the file.</summary>
    public string Allocate()
    {
        while (true)
        {
            var name = $"__ed_{_counter}";
            _counter++;
            if (_used.Add(name))
                return name;
        }
    }
}
