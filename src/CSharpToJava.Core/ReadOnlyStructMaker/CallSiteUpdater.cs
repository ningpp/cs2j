using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Updates call sites of migrated void→struct methods:
/// - `s.Method(args);` → `s = s.Method(args);`
/// - `arr[i].Method(args);` → `arr[i] = arr[i].Method(args);`
/// - Bare calls in constructors: `Method(args);` → `var __tmp = this.Method(args); this._f = __tmp._f; ...`
/// - Return-this methods need no call-site update (semantically compatible).
/// Only updates calls where the receiver type matches the migrated struct.
/// </summary>
public sealed class CallSiteUpdater : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, string> _migratedMethodToStruct;
    private readonly Dictionary<string, List<string>> _structFieldNames;
    private readonly Dictionary<string, string> _outMigratedMethodToStruct;
    private readonly Dictionary<string, HashSet<int>> _migratedArities; // "Struct.Method" -> arities of migrated overloads
    private readonly Dictionary<string, HashSet<int>> _allMethodArities; // "Struct.Method" -> arities of ALL overloads
    private readonly HashSet<IMethodSymbol> _migratedSymbols;
    private readonly SemanticModel _model;

    public CallSiteUpdater(Dictionary<string, string> migratedMethodToStruct, SemanticModel model,
        Dictionary<string, List<string>>? structFieldNames = null,
        Dictionary<string, string>? outMigratedMethodToStruct = null,
        Dictionary<string, HashSet<int>>? migratedArities = null,
        Dictionary<string, HashSet<int>>? allMethodArities = null,
        IEnumerable<IMethodSymbol>? migratedSymbols = null)
    {
        _migratedMethodToStruct = migratedMethodToStruct;
        _structFieldNames = structFieldNames ?? new Dictionary<string, List<string>>();
        _outMigratedMethodToStruct = outMigratedMethodToStruct ?? new Dictionary<string, string>();
        _migratedArities = migratedArities ?? new Dictionary<string, HashSet<int>>();
        _allMethodArities = allMethodArities ?? new Dictionary<string, HashSet<int>>();
        _migratedSymbols = migratedSymbols != null
            ? new HashSet<IMethodSymbol>(migratedSymbols, SymbolEqualityComparer.Default)
            : new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        _model = model;
    }

    /// <summary>
    /// Appends <c>, out receiver</c> to calls of methods migrated with an out parameter
    /// (OtherReturnToOut). Works in any expression context:
    /// <c>cache.FilterBlock(b)</c> → <c>cache.FilterBlock(b, out cache)</c>.
    /// </summary>
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (_outMigratedMethodToStruct.Count > 0 &&
            node.Expression is MemberAccessExpressionSyntax memberAccess &&
            !node.ArgumentList.Arguments.Any(a => a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            var methodName = memberAccess.Name.Identifier.Text;
            if (_outMigratedMethodToStruct.TryGetValue(methodName, out var structName) &&
                IsMigratedCallTarget(node, methodName, structName) &&
                IsModifiableOutTarget(memberAccess.Expression) &&
                IsReceiverOfStructType(memberAccess.Expression, structName))
            {
                var outArgument = SyntaxFactory.Argument(memberAccess.Expression.WithoutTrivia())
                    .WithRefKindKeyword(
                        SyntaxFactory.Token(SyntaxKind.OutKeyword).WithTrailingTrivia(SyntaxFactory.Space));
                node = node.AddArgumentListArguments(outArgument);
            }
        }

        return base.VisitInvocationExpression(node);
    }

    /// <summary>
    /// True when the invocation resolves to a method on a readonly-migrated struct
    /// that returns that struct type (a mutation surrogate whose result must not be
    /// discarded). Constructors and WithXxx helpers are naturally included; factory
    /// methods on a different receiver type never match because the receiver type
    /// must equal the return type.
    /// </summary>
    private bool IsStructReturningCall(InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess)
    {
        // Never reassign constants/statics or chained fluents that already capture the
        // result (those are not expression statements to begin with).
        try
        {
            if (_model.GetSymbolInfo(invocation).Symbol is IMethodSymbol methodSymbol &&
                methodSymbol.MethodKind == MethodKind.Ordinary &&
                !methodSymbol.IsStatic &&
                methodSymbol.ReturnType is INamedTypeSymbol returnType &&
                returnType.TypeKind == TypeKind.Struct &&
                returnType.IsReadOnly &&
                methodSymbol.ContainingType != null &&
                SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, returnType))
            {
                var receiverType = _model.GetTypeInfo(memberAccess.Expression).Type;
                if (receiverType != null &&
                    SymbolEqualityComparer.Default.Equals(receiverType.OriginalDefinition, returnType.OriginalDefinition))
                {
                    // The receiver must be an assignable l-value (variable/field chain).
                    return memberAccess.Expression is IdentifierNameSyntax
                        || memberAccess.Expression is MemberAccessExpressionSyntax
                        || memberAccess.Expression is ElementAccessExpressionSyntax;
                }
            }
        }
        catch (ArgumentException)
        {
        }
        return false;
    }

    /// <summary>
    /// Determines whether an invocation resolves to a MIGRATED overload. Structs often
    /// keep unmigrated overloads with the same name (e.g. Rectangle.Add(Rectangle) stays
    /// void while Add(Point) returns Rectangle), so matching by name alone is unsafe.
    /// Uses the semantic model when available; otherwise falls back to arity analysis,
    /// rewriting only when every overload of that name/arity was migrated.
    /// </summary>
    private bool IsMigratedCallTarget(InvocationExpressionSyntax invocation, string methodName, string structName)
    {
        // Semantic resolution (works for nodes that still live in the modeled tree).
        try
        {
            if (_model.GetSymbolInfo(invocation).Symbol is IMethodSymbol methodSymbol &&
                methodSymbol.ContainingType?.Name == structName)
            {
                // 1) Single-file pass on the ORIGINAL tree: exact migrated-symbol match.
                if (_migratedSymbols.Count > 0)
                    return _migratedSymbols.Contains(methodSymbol.OriginalDefinition);

                // 2) Post-conversion tree: migrated overloads return the struct type or
                // carry the out newStatus parameter.
                if (methodSymbol.ReturnType.Name == structName)
                    return true;
                if (_outMigratedMethodToStruct.ContainsKey(methodName) &&
                    methodSymbol.Parameters.Any(p => p.RefKind == RefKind.Out && p.Type.Name == structName))
                    return true;
                // Resolved to a non-migrated overload — must not rewrite.
                return false;
            }
        }
        catch (ArgumentException)
        {
            // Node not in the semantic model's tree — fall through to arity check.
        }

        // Fallback: rewrite only when every overload of this name was migrated
        // (no unmigrated overload exists that could share the call).
        var arity = invocation.ArgumentList.Arguments.Count;
        var key = structName + "." + methodName;
        if (_migratedArities.TryGetValue(key, out var migrated) &&
            _allMethodArities.TryGetValue(key, out var all))
        {
            return migrated.Contains(arity) && migrated.SetEquals(all);
        }
        return true;
    }

    /// <summary>
    /// Checks whether the expression is a valid <c>out</c> argument target
    /// (variable, field/property chain, or array element).
    /// </summary>
    private static bool IsModifiableOutTarget(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax ma => IsModifiableOutTarget(ma.Expression),
        ElementAccessExpressionSyntax ea => IsModifiableOutTarget(ea.Expression),
        ThisExpressionSyntax => true,
        _ => false
    };

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;
            if (_migratedMethodToStruct.TryGetValue(methodName, out var structName))
            {
                var receiver = memberAccess.Expression;
                if (IsMigratedCallTarget(invocation, methodName, structName) &&
                    IsReceiverOfStructType(receiver, structName))
                {
                    // s.Method(args) → s = s.Method(args)
                    var assignment = SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        receiver.WithoutTrivia(),
                        invocation.WithoutTrivia())
                        .WithTriviaFrom(node.Expression);

                    return node.WithExpression(assignment);
                }
            }

            // Any method on a migrated readonly struct that RETURNS the struct type is
            // a mutation surrogate (WithXxx-style); discarding its result would lose
            // the update. Rewrite `s.M(args);` → `s = s.M(args);` for those too.
            if (IsStructReturningCall(invocation, memberAccess))
            {
                var receiver2 = memberAccess.Expression;
                var assignment2 = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    receiver2.WithoutTrivia(),
                    invocation.WithoutTrivia())
                    .WithTriviaFrom(node.Expression);
                return node.WithExpression(assignment2);
            }
        }

        // Handle bare invocations (implicit this) inside the struct's own constructors/methods
        if (node.Expression is InvocationExpressionSyntax bareInvocation &&
            bareInvocation.Expression is IdentifierNameSyntax bareMethodName)
        {
            var name = bareMethodName.Identifier.Text;
            if (_migratedMethodToStruct.TryGetValue(name, out var structName2))
            {
                var containingStruct = node.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
                if (containingStruct != null && containingStruct.Identifier.Text == structName2)
                {
                    // We're inside the struct itself — replace bare call with field assignments
                    var containingCtor = node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
                    var containingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                    // Inside a MIGRATED method of the same struct, a bare call to another
                    // migrated overload must feed the method's accumulator local
                    // (`var result = this;`) rather than being discarded. Without this,
                    // e.g. Rectangle.Add(Rectangle) calls Add(Point) and drops the result,
                    // leaving the bounding box un-expanded.
                    if (containingCtor == null && containingMethod != null &&
                        _migratedMethodToStruct.ContainsKey(containingMethod.Identifier.Text) &&
                        IsMigratedCallTarget(bareInvocation, name, structName2))
                    {
                        var accumulator = FindAccumulatorLocal(containingMethod);
                        if (accumulator != null)
                        {
                            var rewrittenCall = SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName(accumulator),
                                    bareMethodName.WithoutTrivia()),
                                bareInvocation.ArgumentList);
                            var accumulatorAssignment = SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(accumulator),
                                rewrittenCall)
                                .WithTriviaFrom(node.Expression);
                            return node.WithExpression(accumulatorAssignment);
                        }
                    }

                    // Only handle if inside a constructor or a non-migrated method, and
                    // only when the call resolves to a migrated overload.
                    if ((containingCtor != null || (containingMethod != null &&
                        !_migratedMethodToStruct.ContainsKey(containingMethod.Identifier.Text))) &&
                        IsMigratedCallTarget(bareInvocation, name, structName2))
                    {
                        return BuildBareCallReplacement(node, bareInvocation, structName2);
                    }
                }
            }
        }

        return base.VisitExpressionStatement(node);
    }

    /// <summary>
    /// Replaces a bare call `Method(args);` inside a constructor with a whole-struct
    /// reassignment:
    ///   this = this.Method(args);        (all fields assigned before the call)
    ///   this = default(S).Method(args);  (fields not yet assigned — CS0188 safe)
    /// Assigning to `this` is legal in struct constructors once all fields are
    /// definitely assigned, and avoids repeated per-field assignments that would be
    /// illegal for Java final fields.
    /// </summary>
    private SyntaxNode BuildBareCallReplacement(
        ExpressionStatementSyntax originalNode,
        InvocationExpressionSyntax invocation,
        string structName)
    {
        var ctor = originalNode.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctor?.Body == null)
            return originalNode;

        var fieldNamesOfStruct = _structFieldNames.GetValueOrDefault(structName) ?? new List<string>();

        var allAssignedBefore = ctor.Initializer?.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) == true;
        if (!allAssignedBefore && fieldNamesOfStruct.Count > 0)
        {
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var statement in ctor.Body.Statements)
            {
                if (statement == originalNode) break;
                foreach (var assignment in statement.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
                {
                    var name = assignment.Left switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.Text,
                        _ => null
                    };
                    if (name != null)
                        assigned.Add(name);
                }
            }
            allAssignedBefore = fieldNamesOfStruct.All(assigned.Contains);
        }

        var receiver = allAssignedBefore
            ? (ExpressionSyntax)SyntaxFactory.ThisExpression()
            : SyntaxFactory.DefaultExpression(SyntaxFactory.IdentifierName(structName));

        var call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                ((IdentifierNameSyntax)invocation.Expression).WithoutTrivia()),
            invocation.ArgumentList);

        return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.ThisExpression(),
                    call))
            .WithTriviaFrom(originalNode);
    }

    /// <summary>
    /// Finds the accumulator local of a migrated method, i.e. the local variable
    /// initialized from <c>this</c> (<c>var result = this;</c>). Returns null when no
    /// such local exists.
    /// </summary>
    private static string? FindAccumulatorLocal(MethodDeclarationSyntax method)
    {
        if (method.Body == null)
        {
            return null;
        }

        foreach (var declaration in method.Body.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            foreach (var variable in declaration.Variables)
            {
                if (variable.Initializer?.Value is ThisExpressionSyntax)
                {
                    return variable.Identifier.Text;
                }
            }
        }

        return null;
    }

    private bool IsReceiverOfStructType(ExpressionSyntax receiver, string structName)
    {
        // Try semantic model first (works when node is in the original tree)
        try
        {
            var typeInfo = _model.GetTypeInfo(receiver);
            if (typeInfo.Type != null)
                return typeInfo.Type.Name == structName;
        }
        catch (ArgumentException)
        {
            // Node not in semantic model's tree — fall through to syntactic check
        }

        // Syntactic fallback: find the variable declaration and check its type
        return IsReceiverOfStructTypeSyntactic(receiver, structName);
    }

    private static bool IsReceiverOfStructTypeSyntactic(ExpressionSyntax receiver, string structName)
    {
        // Get the identifier name from the receiver
        string? variableName = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            ElementAccessExpressionSyntax ea => ea.Expression switch
            {
                IdentifierNameSyntax eid => eid.Identifier.Text,
                _ => null
            },
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => null
        };

        if (variableName == null) return false;

        bool isArrayElement = receiver is ElementAccessExpressionSyntax;

        // Search enclosing block for variable declaration
        var enclosingBlock = receiver.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        if (enclosingBlock != null)
        {
            foreach (var declaration in enclosingBlock.DescendantNodes().OfType<VariableDeclarationSyntax>())
            {
                foreach (var variable in declaration.Variables)
                {
                    if (variable.Identifier.Text == variableName)
                    {
                        return IsTypeMatch(declaration.Type, structName, isArrayElement,
                            variable.Initializer?.Value);
                    }
                }
            }
        }

        // Check method parameters
        var containingMethod = receiver.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (containingMethod != null)
        {
            foreach (var param in containingMethod.ParameterList.Parameters)
            {
                if (param.Identifier.Text == variableName && param.Type != null)
                {
                    return IsTypeMatch(param.Type, structName, isArrayElement, null);
                }
            }
        }

        // Also check fields in the containing class/struct
        var containingType = receiver.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType != null)
        {
            foreach (var field in containingType.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    if (variable.Identifier.Text == variableName)
                        return IsTypeMatch(field.Declaration.Type, structName, isArrayElement,
                            variable.Initializer?.Value);
                }
            }
        }

        return false;
    }

    private static bool IsTypeMatch(TypeSyntax typeSyntax, string structName, bool isArrayElement,
        ExpressionSyntax? initializer)
    {
        // For 'var', check the initializer expression type
        if (typeSyntax is IdentifierNameSyntax { Identifier.Text: "var" })
        {
            if (initializer is ObjectCreationExpressionSyntax creation)
            {
                var createdType = ExtractTypeName(creation.Type);
                if (isArrayElement)
                    return false; // var x = new Size[10] → element type is Size, but hard to detect
                return createdType == structName;
            }
            return false;
        }

        var typeName = ExtractTypeName(typeSyntax);

        if (isArrayElement)
        {
            // For array element access, the variable type should be an array of the struct
            if (typeSyntax is ArrayTypeSyntax arrayType)
            {
                var elementType = ExtractTypeName(arrayType.ElementType);
                return elementType == structName;
            }
            // If not explicitly an array type, no match
            return false;
        }

        return typeName == structName;
    }

    private static string? ExtractTypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax generic => generic.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        PredefinedTypeSyntax predefined => predefined.Keyword.Text,
        _ => null
    };
}
