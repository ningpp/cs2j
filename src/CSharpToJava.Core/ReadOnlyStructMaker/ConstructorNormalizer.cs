using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Normalizes constructors of readonly structs so that every instance field is
/// assigned exactly once. C# allows readonly fields to be assigned multiple times
/// inside a constructor, but Java final fields allow only a single assignment per
/// constructor path. For each field assigned more than once, a shadow local variable
/// is introduced: all <c>this.field</c> accesses in the constructor are redirected to
/// the local (bare references bind to the local via shadowing when names do not
/// collide with parameters), and a single <c>this.field = local;</c> is appended at
/// the end of the constructor.
/// </summary>
public static class ConstructorNormalizer
{
    /// <summary>Marker comment attached to normalized constructors (idempotency).</summary>
    private const string NormalizedMarker = "// cs2j-ctor-normalized";

    /// <summary>Normalizes constructors of all readonly structs in the compilation unit.</summary>
    public static CompilationUnitSyntax Normalize(CompilationUnitSyntax root)
    {
        var rewriter = new ReadOnlyStructCtorNormalizer();
        return (CompilationUnitSyntax)rewriter.Visit(root)!;
    }

    /// <summary>Normalizes constructors of a single readonly struct declaration.</summary>
    public static StructDeclarationSyntax NormalizeStruct(StructDeclarationSyntax node)
    {
        return NormalizeStructCore(node);
    }

    private sealed class ReadOnlyStructCtorNormalizer : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        {
            node = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
            if (!node.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
                return node;
            return NormalizeStructCore(node);
        }
    }

    private static StructDeclarationSyntax NormalizeStructCore(StructDeclarationSyntax node)
    {
        // Collect instance fields: name -> declared type syntax.
        var fields = new Dictionary<string, TypeSyntax>();
        foreach (var fieldDecl in node.Members.OfType<FieldDeclarationSyntax>())
        {
            if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) ||
                                              m.IsKind(SyntaxKind.ConstKeyword)))
                continue;
            foreach (var variable in fieldDecl.Declaration.Variables)
                fields[variable.Identifier.Text] = fieldDecl.Declaration.Type;
        }
        // Get-only auto-properties are also assignable inside constructors and must be
        // copied by the `this = expr` expansion.
        foreach (var prop in node.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (prop.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;
            var accessors = prop.AccessorList?.Accessors;
            if (accessors == null || accessors.Value.Count != 1) continue;
            var getter = accessors.Value[0];
            if (!getter.IsKind(SyntaxKind.GetAccessorDeclaration)) continue;
            if (getter.Body != null || getter.ExpressionBody != null) continue;
            fields[prop.Identifier.Text] = prop.Type;
        }

        if (fields.Count == 0)
            return node;

        var newMembers = node.Members;
        var changed = false;
        for (var i = 0; i < newMembers.Count; i++)
        {
            if (newMembers[i] is ConstructorDeclarationSyntax { Body: not null } ctor)
            {
                // Expand `this = expr;` first (Java cannot assign to `this`). The
                // expansion reads the struct's fields, so shadow-local normalization must
                // NOT defer field assignments past the expansion call — constructors
                // with expansions keep their direct (possibly repeated) assignments;
                // the Java side drops `final` from fields assigned more than once.
                var expanded = ExpandThisAssignments(ctor, fields);
                if (expanded != ctor)
                {
                    newMembers = newMembers.Replace(newMembers[i], expanded);
                    changed = true;
                    continue;
                }

                // Idempotency: skip shadow-local normalization for constructors
                // normalized by a previous pass.
                if (ctor.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                                                     t.ToString().Contains("cs2j-ctor-normalized")))
                    continue;

                var normalized = NormalizeConstructor(ctor, fields);
                if (normalized != ctor)
                {
                    newMembers = newMembers.Replace(newMembers[i], normalized);
                    changed = true;
                }
            }
        }

        return changed ? node.WithMembers(newMembers) : node;
    }

    /// <summary>
    /// Expands whole-instance reassignments inside constructors:
    /// <code>
    /// this = expr;  →  var __cs2jSelf = expr;
    ///                  this.f1 = __cs2jSelf.f1;
    ///                  ... (one line per instance field/get-only auto-property)
    /// </code>
    /// Java has no equivalent of assigning to <c>this</c>; the field-wise copy keeps
    /// the semantics for both plain constructors and the static-factory fallback.
    /// </summary>
    private static ConstructorDeclarationSyntax ExpandThisAssignments(
        ConstructorDeclarationSyntax ctor, Dictionary<string, TypeSyntax> fields)
    {
        var body = ctor.Body!;
        var hasThisAssignment = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is ThisExpressionSyntax);
        if (!hasThisAssignment)
            return ctor;

        // Pick a temp name that collides with nothing in the constructor.
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var param in ctor.ParameterList.Parameters)
            reserved.Add(param.Identifier.Text);
        foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            reserved.Add(declarator.Identifier.Text);
        var tempName = "__cs2jSelf";
        var suffix = 0;
        while (reserved.Contains(tempName))
            tempName = "__cs2jSelf" + (++suffix);

        var rewriter = new ThisAssignmentExpander(tempName, fields.Keys.ToList());
        var newBody = (BlockSyntax)rewriter.Visit(body)!;
        return ctor.WithBody(newBody);
    }

    /// <summary>
    /// Replaces every <c>this = expr;</c> statement with a temp declaration plus
    /// field-wise copy statements.
    /// </summary>
    private sealed class ThisAssignmentExpander : CSharpSyntaxRewriter
    {
        private readonly string _tempName;
        private readonly List<string> _memberNames;

        public ThisAssignmentExpander(string tempName, List<string> memberNames)
        {
            _tempName = tempName;
            _memberNames = memberNames;
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            if (node.Expression is AssignmentExpressionSyntax assignment &&
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                assignment.Left is ThisExpressionSyntax)
            {
                var statements = new List<StatementSyntax>
                {
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")
                                .WithTrailingTrivia(SyntaxFactory.Space))
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(_tempName)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                                        (ExpressionSyntax)Visit(assignment.Right)!)))))
                };
                foreach (var member in _memberNames)
                {
                    statements.Add(SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.ThisExpression(),
                                SyntaxFactory.IdentifierName(member)),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(_tempName),
                                SyntaxFactory.IdentifierName(member)))));
                }
                return SyntaxFactory.Block(statements).WithTriviaFrom(node);
            }

            return base.VisitExpressionStatement(node);
        }
    }

    private static ConstructorDeclarationSyntax NormalizeConstructor(
        ConstructorDeclarationSyntax ctor, Dictionary<string, TypeSyntax> fields)
    {
        var body = ctor.Body!;

        // Count assignments per field (simple, compound, ++/--), including chained
        // assignments (nested AssignmentExpressionSyntax nodes are descendants too).
        var assignmentCounts = new Dictionary<string, int>();
        foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var name = GetFieldAccessName(assignment.Left, fields);
            if (name != null)
                assignmentCounts[name] = assignmentCounts.GetValueOrDefault(name) + 1;
        }
        foreach (var operand in body.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                     .Select(u => u.Operand)
                     .Concat(body.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                         .Where(u => u.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression)
                         .Select(u => u.Operand)))
        {
            var name = GetFieldAccessName(operand, fields);
            if (name != null)
                assignmentCounts[name] = assignmentCounts.GetValueOrDefault(name) + 1;
        }

        var multiAssigned = assignmentCounts
            .Where(kv => kv.Value > 1)
            .Select(kv => kv.Key)
            .ToList();
        if (multiAssigned.Count == 0)
            return ctor;

        // Reserved names: parameters, declared locals, foreach iteration variables.
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var param in ctor.ParameterList.Parameters)
            reserved.Add(param.Identifier.Text);
        foreach (var declarator in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            reserved.Add(declarator.Identifier.Text);
        foreach (var forEach in body.DescendantNodes().OfType<ForEachStatementSyntax>())
            reserved.Add(forEach.Identifier.Text);

        // Choose local names. When the field name does not collide, a local with the
        // same name shadows the field: all bare references inside the constructor
        // automatically bind to the local. When it collides (e.g. a constructor
        // parameter has the same name), use a prefixed name; bare references then
        // already bind to the parameter, and only `this.field` accesses the field.
        var localNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fieldName in multiAssigned)
        {
            var candidate = reserved.Contains(fieldName) ? "__cs2j_" + fieldName : fieldName;
            localNames[fieldName] = candidate;
            reserved.Add(candidate);
        }

        // Redirect all `this.field` accesses to the local variable.
        body = (BlockSyntax)new ThisFieldToLocalRewriter(localNames).Visit(body)!;

        // Field state now accumulates in the shadow locals and only reaches `this` at
        // the very end of the constructor, so any `this.M(...)` invocation executed
        // before that would observe STALE (default) field values — e.g. Rectangle's
        // `this = this.Add(point)` expansions must see the already-accumulated box.
        // Flush the shadow locals back into `this` immediately before every such call.
        body = (BlockSyntax)new FlushLocalsBeforeThisCallsRewriter(localNames.Values.ToList()).Visit(body)!;

        // Prepend shadow local declarations initialized to default(T).
        var declarations = new List<StatementSyntax>();
        foreach (var fieldName in multiAssigned)
        {
            var fieldType = fields[fieldName];
            var declaration = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(fieldType)
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(localNames[fieldName])
                                .WithInitializer(SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.DefaultExpression(fieldType))))))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            declarations.Add(declaration);
        }

        // Append single final assignments: this.field = local;
        var trailing = new List<StatementSyntax>();
        foreach (var fieldName in multiAssigned)
        {
            var assignment = SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.ThisExpression(),
                            SyntaxFactory.IdentifierName(fieldName)),
                        SyntaxFactory.IdentifierName(localNames[fieldName])))
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            trailing.Add(assignment);
        }

        body = body.WithStatements(body.Statements.InsertRange(0, declarations).AddRange(trailing));
        return ctor
            .WithBody(body)
            .WithLeadingTrivia(ctor.GetLeadingTrivia()
                .Add(SyntaxFactory.Comment(NormalizedMarker))
                .Add(SyntaxFactory.CarriageReturnLineFeed));
    }

    /// <summary>
    /// Returns the field name when the expression is a direct field access
    /// (<c>field</c> or <c>this.field</c>); otherwise null.
    /// </summary>
    private static string? GetFieldAccessName(ExpressionSyntax expression, Dictionary<string, TypeSyntax> fields)
    {
        var name = expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.Text,
            _ => null
        };
        return name != null && fields.ContainsKey(name) ? name : null;
    }

    /// <summary>Replaces <c>this.field</c> with the corresponding local identifier.</summary>
    private sealed class ThisFieldToLocalRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _localNames;

        public ThisFieldToLocalRewriter(Dictionary<string, string> localNames)
        {
            _localNames = localNames;
        }

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Expression is ThisExpressionSyntax &&
                node.Name is IdentifierNameSyntax name &&
                _localNames.TryGetValue(name.Identifier.Text, out var localName))
            {
                return SyntaxFactory.IdentifierName(localName).WithTriviaFrom(node);
            }

            return base.VisitMemberAccessExpression(node);
        }
    }

    /// <summary>
    /// Inserts <c>this.field = local;</c> flush statements before every statement that
    /// invokes an instance method on <c>this</c>. After shadow-local redirection the
    /// accumulated field state lives only in the locals until the constructor's final
    /// assignments, so a <c>this.M(...)</c> call would otherwise read default values.
    /// </summary>
    private sealed class FlushLocalsBeforeThisCallsRewriter : CSharpSyntaxRewriter
    {
        private readonly List<string> _fieldNames;
        private readonly List<string> _localNames;

        public FlushLocalsBeforeThisCallsRewriter(List<string> localNames)
        {
            // localName == shadow local; fieldName == struct member. Locals created by
            // the shadow pass keep the field name unless prefixed with __cs2j_.
            _localNames = localNames;
            _fieldNames = localNames
                .Select(local => local.StartsWith("__cs2j_", StringComparison.Ordinal)
                    ? local.Substring("__cs2j_".Length)
                    : local)
                .ToList();
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            node = (BlockSyntax)base.VisitBlock(node)!;

            var statements = new List<StatementSyntax>(node.Statements.Count);
            foreach (var statement in node.Statements)
            {
                if (ContainsThisInstanceCall(statement))
                {
                    statements.AddRange(BuildFlushStatements());
                }

                statements.Add(statement);
            }

            return node.WithStatements(SyntaxFactory.List(statements));
        }

        private static bool ContainsThisInstanceCall(StatementSyntax statement)
        {
            return statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                   memberAccess.Expression is ThisExpressionSyntax);
        }

        private IEnumerable<StatementSyntax> BuildFlushStatements()
        {
            for (var i = 0; i < _fieldNames.Count; i++)
            {
                yield return SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.ThisExpression(),
                                SyntaxFactory.IdentifierName(_fieldNames[i])),
                            SyntaxFactory.IdentifierName(_localNames[i])))
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            }
        }
    }
}
