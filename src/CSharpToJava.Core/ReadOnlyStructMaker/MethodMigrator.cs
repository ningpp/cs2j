using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class MethodMigrator
{
    private readonly string _structName;
    private readonly HashSet<string> _fieldNames;
    private readonly HashSet<string> _propertyNames;
    private readonly HashSet<string> _migratedMethodNames;
    private readonly HashSet<string> _instanceMethodNames;

    public MethodMigrator(string structName, HashSet<string>? fieldNames = null,
        HashSet<string>? propertyNames = null, HashSet<string>? migratedMethodNames = null,
        HashSet<string>? instanceMethodNames = null)
    {
        _structName = structName;
        _fieldNames = fieldNames ?? new HashSet<string>();
        _propertyNames = propertyNames ?? new HashSet<string>();
        _migratedMethodNames = migratedMethodNames ?? new HashSet<string>();
        _instanceMethodNames = instanceMethodNames ?? new HashSet<string>();
    }

    public MethodDeclarationSyntax Migrate(MethodDeclarationSyntax method)
    {
        var body = method.Body;
        if (body == null) return method;

        var isVoid = method.ReturnType is PredefinedTypeSyntax pts &&
                     pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
        var returnsThis = body.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression is ThisExpressionSyntax);
        var isOtherReturn = !isVoid && !returnsThis;

        // 1. Insert `var result = this;` at top
        var resultDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator("result")
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.ThisExpression())))))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        // 2. Replace `this` with `result` in body
        var newBody = (BlockSyntax)new ThisToResultRewriter().Visit(body)!;

        // 3. Replace implicit field/property accesses with result.field/result.property
        if (_fieldNames.Count > 0 || _propertyNames.Count > 0)
        {
            newBody = (BlockSyntax)new ImplicitFieldToResultRewriter(_fieldNames, _propertyNames).Visit(newBody)!;
        }

        // 3b. Replace bare calls to migrated methods with result = result.Method(args)
        if (_migratedMethodNames.Count > 0)
        {
            newBody = (BlockSyntax)new ImplicitCallRewriter(_migratedMethodNames, method.Identifier.Text).Visit(newBody)!;
        }

        // 3c. Replace bare calls to OTHER instance methods with result.Method(args).
        // The local `result` carries the mutations performed so far; implicit `this`
        // would read stale state (e.g. a depth decremented through result above).
        if (_instanceMethodNames.Count > 0)
        {
            newBody = (BlockSyntax)new ImplicitInstanceCallRewriter(_instanceMethodNames, method.Identifier.Text,
                _migratedMethodNames).Visit(newBody)!;
        }

        // 4. Replace `return this;` with `return result;`
        newBody = (BlockSyntax)new ReturnThisRewriter().Visit(newBody)!;

        // 5. Prepend result declaration
        newBody = newBody.WithStatements(
            newBody.Statements.Insert(0, resultDecl));

        // 6. Handle based on migration type
        if (isVoid)
        {
            // void → struct return: change return type, replace bare returns with return result, add final return
            newBody = (BlockSyntax)new BareReturnRewriter().Visit(newBody)!;
            var returnType = (TypeSyntax)SyntaxFactory.IdentifierName(_structName);
            var returnStatement = SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result"))
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            newBody = newBody.AddStatements(returnStatement);
            return method.WithReturnType(returnType).WithBody(newBody);
        }
        else if (isOtherReturn)
        {
            // Other return → add out parameter, insert `newStatus = result;` before returns
            var outParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("newStatus"))
                .WithType(SyntaxFactory.IdentifierName(_structName))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.OutKeyword).WithTrailingTrivia(SyntaxFactory.Space));

            // Insert `newStatus = result;` before each return statement
            newBody = (BlockSyntax)new InsertOutAssignmentRewriter().Visit(newBody)!;

            // The out parameter is required and must not appear after any optional
            // parameters (CS1737). Insert it before the first optional parameter.
            var parameters = method.ParameterList.Parameters;
            var firstOptional = parameters
                .Select((p, i) => (p, i))
                .FirstOrDefault(x => x.p.Default != null);
            if (firstOptional.p == null)
            {
                return method
                    .AddParameterListParameters(outParam)
                    .WithBody(newBody);
            }
            var newParameters = new List<ParameterSyntax>();
            newParameters.AddRange(parameters.Take(firstOptional.i));
            newParameters.Add(outParam);
            newParameters.AddRange(parameters.Skip(firstOptional.i));
            return method
                .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParameters)))
                .WithBody(newBody);
        }
        else
        {
            // return-this → just return result (already handled by ReturnThisRewriter)
            return method.WithBody(newBody);
        }
    }

    private sealed class ThisToResultRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
        {
            // Don't replace `this` in `var result = this;` context — handled separately
            return SyntaxFactory.IdentifierName("result").WithTriviaFrom(node);
        }
    }

    /// <summary>
    /// Rewrites implicit field/property accesses (e.g. `_field = value;` or `Prop = value;`)
    /// to `result._field = value;` / `result.Prop = value;` and implicit reads
    /// (e.g. `if (_field == null)` or `var x = Prop`) to `result._field` / `result.Prop`.
    /// Only rewrites identifiers that match known struct field/property names and are not local variables.
    /// </summary>
    internal sealed class ImplicitFieldToResultRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _fieldNames;
        private readonly HashSet<string> _propertyNames;
        private readonly HashSet<string> _localNames = new();

        public ImplicitFieldToResultRewriter(HashSet<string> fieldNames, HashSet<string> propertyNames)
        {
            _fieldNames = fieldNames;
            _propertyNames = propertyNames;
        }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            // Track local variable names to avoid rewriting them
            foreach (var variable in node.Declaration.Variables)
                _localNames.Add(variable.Identifier.Text);
            return base.VisitLocalDeclarationStatement(node);
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            _localNames.Add(node.Identifier.Text);
            return base.VisitForEachStatement(node);
        }

        public override SyntaxNode? VisitParameter(ParameterSyntax node)
        {
            _localNames.Add(node.Identifier.Text);
            return base.VisitParameter(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.Text;
            if ((_fieldNames.Contains(name) || _propertyNames.Contains(name)) && !_localNames.Contains(name))
            {
                // Never rewrite identifiers in type positions (local declaration types,
                // casts, typeof, generic type arguments, etc.) — those refer to types,
                // not fields, and rewriting them would break the (TypeSyntax) casts in
                // Roslyn's base rewriter.
                if (!IsTypePosition(node))
                {
                    // Check it's not already qualified (e.g. result._field)
                    if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node)
                        return base.VisitIdentifierName(node); // already qualified

                    // Replace with result.fieldName or result.propertyName
                    var resultAccess = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("result"),
                        node.WithoutTrivia())
                        .WithTriviaFrom(node);
                    return resultAccess;
                }
            }
            return base.VisitIdentifierName(node);
        }

        /// <summary>
        /// Determines whether the identifier is used where a type is expected:
        /// climb the surrounding type syntax (qualified/generic/array/nullable names,
        /// type argument lists) and check the outermost node occupies a type slot.
        /// Identifiers under a type argument list are always type usages (C# method
        /// and type arguments can only name types).
        /// </summary>
        private static bool IsTypePosition(IdentifierNameSyntax node)
        {
            SyntaxNode current = node;
            var inTypeArgumentList = false;
            while (current.Parent is TypeSyntax || current.Parent is TypeArgumentListSyntax)
            {
                if (current.Parent is TypeArgumentListSyntax)
                {
                    inTypeArgumentList = true;
                }
                current = current.Parent!;
            }

            // Type arguments (e.g. List<JsonClassInfo>, obj.Method<JsonClassInfo>())
            // can only name types in C#, regardless of the outer context.
            if (inTypeArgumentList) return true;

            return current.Parent switch
            {
                VariableDeclarationSyntax vd => vd.Type == current,
                ParameterSyntax p => p.Type == current,
                CastExpressionSyntax ce => ce.Type == current,
                ObjectCreationExpressionSyntax oc => oc.Type == current,
                TypeOfExpressionSyntax to => to.Type == current,
                DefaultExpressionSyntax de => de.Type == current,
                DeclarationExpressionSyntax dx => dx.Type == current,
                TupleElementSyntax te => te.Type == current,
                MethodDeclarationSyntax md => md.ReturnType == current,
                PropertyDeclarationSyntax pd => pd.Type == current,
                FieldDeclarationSyntax fd => fd.Declaration.Type == current,
                EventDeclarationSyntax ed => ed.Type == current,
                IndexerDeclarationSyntax id => id.Type == current,
                LocalFunctionStatementSyntax lf => lf.ReturnType == current,
                DelegateDeclarationSyntax dd => dd.ReturnType == current,
                _ => false,
            };
        }
    }

    /// <summary>Replaces bare `return;` with `return result;` in void→struct migrated methods.</summary>
    private sealed class BareReturnRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
                return node.WithExpression(SyntaxFactory.IdentifierName("result"));
            return base.VisitReturnStatement(node);
        }
    }

    /// <summary>
    /// Rewrites bare calls to migrated methods (implicit this) to `result = result.Method(args)`.
    /// E.g., `Add(value);` → `result = result.Add(value);`
    /// Also handles `result.Method(args);` statements (originally `this.Method(args);`,
    /// whose receiver was already rewritten to `result` by ThisToResultRewriter).
    /// </summary>
    private sealed class ImplicitCallRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _migratedMethodNames;
        private readonly string _currentMethodName;

        public ImplicitCallRewriter(HashSet<string> migratedMethodNames, string currentMethodName)
        {
            _migratedMethodNames = migratedMethodNames;
            _currentMethodName = currentMethodName;
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            // Match bare invocation: Method(args); (no explicit receiver)
            // or result-receiver invocation: result.Method(args);
            if (node.Expression is InvocationExpressionSyntax invocation)
            {
                string? name = invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.Text: "result" }
                    } ma => ma.Name.Identifier.Text,
                    _ => null
                };

                if (name != null && name != _currentMethodName && _migratedMethodNames.Contains(name))
                {
                    // Transform: Method(args) → result = result.Method(args)
                    var resultCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("result"),
                            SyntaxFactory.IdentifierName(name)),
                        invocation.ArgumentList);

                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName("result"),
                            resultCall))
                        .WithTriviaFrom(node);

                    return assignment;
                }
            }

            return base.VisitExpressionStatement(node);
        }
    }

    /// <summary>
    /// Rewrites bare calls to instance methods (implicit this) into result-qualified
    /// calls: `PopFromArray();` → `result.PopFromArray();`. The `result` local is the
    /// "current instance" once a method starts mutating through `var result = this;` —
    /// calling back through implicit `this` would read stale field state.
    /// Migrated methods are excluded: their statement calls are handled by
    /// <see cref="ImplicitCallRewriter"/> (result = result.Method(...)), and migrated
    /// methods cannot legally appear in expression position in original code.
    /// </summary>
    internal sealed class ImplicitInstanceCallRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _instanceMethodNames;
        private readonly HashSet<string> _migratedMethodNames;
        private readonly string _currentMethodName;
        private readonly HashSet<string> _localNames = new();

        public ImplicitInstanceCallRewriter(HashSet<string> instanceMethodNames, string currentMethodName,
            HashSet<string>? migratedMethodNames = null)
        {
            _instanceMethodNames = instanceMethodNames;
            _currentMethodName = currentMethodName;
            _migratedMethodNames = migratedMethodNames ?? new HashSet<string>();
        }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
                _localNames.Add(variable.Identifier.Text);
            return base.VisitLocalDeclarationStatement(node);
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            _localNames.Add(node.Identifier.Text);
            return base.VisitForEachStatement(node);
        }

        public override SyntaxNode? VisitParameter(ParameterSyntax node)
        {
            _localNames.Add(node.Identifier.Text);
            return base.VisitParameter(node);
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            node = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

            if (node.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text != _currentMethodName &&
                !_localNames.Contains(id.Identifier.Text) &&
                _instanceMethodNames.Contains(id.Identifier.Text) &&
                !_migratedMethodNames.Contains(id.Identifier.Text))
            {
                return node.WithExpression(SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("result"),
                        id.WithoutTrivia()))
                    .WithTriviaFrom(node);
            }

            return node;
        }
    }

    private sealed class ReturnThisRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression is ThisExpressionSyntax)
                return node.WithExpression(SyntaxFactory.IdentifierName("result"));
            return base.VisitReturnStatement(node);
        }
    }

    /// <summary>Inserts `newStatus = result;` before each return statement for OtherReturnToOut.</summary>
    private sealed class InsertOutAssignmentRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            // Skip returns inside lambdas / local functions / query expressions: the
            // method's out parameter is not capturable there (CS1628).
            if (node.Ancestors().Any(a =>
                    a is AnonymousFunctionExpressionSyntax || a is LocalFunctionStatementSyntax))
            {
                return base.VisitReturnStatement(node);
            }

            // Wrap EVERY return in a block that assigns the out parameter first:
            //   { newStatus = result; return X; }
            // This covers top-level returns, returns inside nested blocks
            // (if/for/while bodies), and returns in switch sections, so the out
            // parameter is definitely assigned on every exit path (CS0177).
            var assignment = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("newStatus"),
                    SyntaxFactory.IdentifierName("result")))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

            return SyntaxFactory.Block(
                    assignment,
                    node.WithLeadingTrivia(SyntaxFactory.Whitespace("            ")))
                .WithLeadingTrivia(node.GetLeadingTrivia());
        }
    }
}
