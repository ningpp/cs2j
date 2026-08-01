using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Rewrites external field assignments to WithXxx method calls:
/// - obj.Field = value → obj = obj.WithField(value)
/// - obj.Field += value → obj = obj.WithField(obj.Field + value)
/// - obj.Field++ → obj = obj.WithField(obj.Field + 1)
/// - obj.Field-- → obj = obj.WithField(obj.Field - 1)
/// - new S { F = v } → new S(f: v) (when constructor exists)
///
/// Uses semantic model to verify that the receiver's type is actually one of the
/// target structs, preventing incorrect conversion of classes with same-named fields.
/// </summary>
public sealed class AssignmentRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, HashSet<string>> _structFields = new(); // structName -> fieldNames
    private readonly HashSet<string> _targetFieldNames = new(); // all field names across all target structs
    private readonly SemanticModel? _semanticModel;
    private readonly Dictionary<string, string> _variableTypes = new(); // variableName -> typeName
    // L5 structs: properties are public get-only (not private fields with getXxx() getters).
    // Reads from these structs should use property access (obj.Prop), not getXxx().
    private readonly HashSet<string> _propertyAccessStructs = new();

    public AssignmentRewriter(
        Dictionary<string, HashSet<string>> structFields,
        SemanticModel? semanticModel,
        IReadOnlySet<string>? propertyAccessStructs = null)
    {
        _structFields = structFields;
        _semanticModel = semanticModel;
        if (propertyAccessStructs != null)
            _propertyAccessStructs.UnionWith(propertyAccessStructs);
        // Collect all field names for quick lookup
        foreach (var fields in structFields.Values)
        {
            foreach (var field in fields)
            {
                _targetFieldNames.Add(field);
            }
        }
    }

    /// <summary>
    /// Builds a map of variable names to their declared types from the original syntax tree.
    /// This must be called BEFORE the tree is modified by other rewriters.
    /// </summary>
    public void BuildVariableTypeMap(SyntaxNode root)
    {
        _variableTypes.Clear();
        foreach (var node in root.DescendantNodes())
        {
            // Handle local variable declarations: Type x = ...; or var x = ...;
            if (node is LocalDeclarationStatementSyntax localDecl &&
                localDecl.Declaration.Variables.Count == 1)
            {
                var variable = localDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(localDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                {
                    _variableTypes[variable.Identifier.Text] = varTypeName;
                }
                else if (variable.Initializer?.Value != null)
                {
                    // For 'var' declarations, try to get the type from the initializer
                    var initTypeName = GetTypeNameFromExpression(variable.Initializer.Value);
                    if (!string.IsNullOrEmpty(initTypeName))
                    {
                        _variableTypes[variable.Identifier.Text] = initTypeName;
                    }
                }
            }
            // Handle field declarations: Type fieldName;
            else if (node is FieldDeclarationSyntax fieldDecl &&
                     fieldDecl.Declaration.Variables.Count == 1)
            {
                var variable = fieldDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(fieldDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                {
                    _variableTypes[variable.Identifier.Text] = varTypeName;
                }
            }
        }
    }

    private static string GetTypeNameFromExpression(ExpressionSyntax expr)
    {
        if (expr is ObjectCreationExpressionSyntax objCreate)
        {
            return GetTypeName(objCreate.Type);
        }
        return string.Empty;
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Left is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitAssignmentExpression(node);

        if (memberAccess.Name is not IdentifierNameSyntax fieldName)
            return base.VisitAssignmentExpression(node);

        var fieldNameText = fieldName.Identifier.Text;

        // Quick check: field name must be in our target fields
        if (!_targetFieldNames.Contains(fieldNameText))
            return base.VisitAssignmentExpression(node);

        // Don't convert if this is inside the struct itself (struct can access its own
        // private fields, and rewriting WithXxx method bodies creates self-recursion).
        // This mirrors the check in VisitMemberAccessExpression for reads.
        var containingStruct = node.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
        if (containingStruct != null && _structFields.ContainsKey(containingStruct.Identifier.Text))
            return base.VisitAssignmentExpression(node);

        // Syntactic check: receiver must be a simple identifier or element access
        // (to ensure we're modifying a variable, not a property return value)
        if (!IsModifiableLValue(memberAccess.Expression))
            return base.VisitAssignmentExpression(node);

        // Semantic check: verify the receiver's type is actually one of our target structs
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return base.VisitAssignmentExpression(node);

        var withMethodName = "With" + fieldNameText;
        var receiver = memberAccess.Expression;

        // For compound assignments, the read of obj.Field should use the getter method.
        // L5 structs use property access (obj.Prop); L4 structs use getXxx().
        var getterAccess = CreateReadAccess(receiver, fieldNameText, structName);

        // Handle different assignment types
        ExpressionSyntax newValue;
        switch (node.Kind())
        {
            case SyntaxKind.SimpleAssignmentExpression:
                // Visit the right side to convert any field reads within it
                newValue = (ExpressionSyntax)Visit(node.Right)!;
                break;
            case SyntaxKind.AddAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.AddExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.SubtractAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.SubtractExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.MultiplyAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.MultiplyExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.DivideAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.DivideExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.ModuloAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.ModuloExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.AndAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.BitwiseAndExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.ExclusiveOrAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.ExclusiveOrExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.OrAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.BitwiseOrExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.LeftShiftAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.LeftShiftExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.RightShiftAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.RightShiftExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            default:
                return base.VisitAssignmentExpression(node);
        }

        // Generate: receiver = receiver.WithField(newValue)
        var withCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(withMethodName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(newValue))));

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            receiver.WithoutTrivia(),
            withCall).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
            return base.VisitPrefixUnaryExpression(node);

        if (node.Operand is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitPrefixUnaryExpression(node);

        var result = HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PreIncrementExpression);
        // HandleUnaryMutation returns null when checks fail; fall back to base visit to avoid null nodes
        return result ?? base.VisitPrefixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Kind() is not (SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression))
            return base.VisitPostfixUnaryExpression(node);

        if (node.Operand is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitPostfixUnaryExpression(node);

        var result = HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PostIncrementExpression);
        // HandleUnaryMutation returns null when checks fail; fall back to base visit to avoid null nodes
        return result ?? base.VisitPostfixUnaryExpression(node);
    }

    /// <summary>
    /// Converts read accesses to converted struct fields: obj.Field → obj.getField().
    /// Write accesses are handled by VisitAssignmentExpression/VisitPrefixUnaryExpression/VisitPostfixUnaryExpression
    /// which return without calling base, so their children are not visited here.
    /// </summary>
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // Only handle simple member access (obj.Field)
        if (node.Name is not IdentifierNameSyntax fieldName)
            return base.VisitMemberAccessExpression(node);

        var fieldNameText = fieldName.Identifier.Text;

        // Quick check: field name must be in our target fields
        if (!_targetFieldNames.Contains(fieldNameText))
            return base.VisitMemberAccessExpression(node);

        // Semantic check: verify the receiver's type is actually one of our target structs
        var structName = TryGetTargetStructName(node.Expression);
        if (structName == null)
        {
            // Fallback: for simple identifiers, check enclosing method parameters syntactically
            if (node.Expression is IdentifierNameSyntax receiverId)
                structName = TryGetParameterStructType(receiverId.Identifier.Text, node);
            if (structName == null)
                return base.VisitMemberAccessExpression(node);
        }

        // Don't convert if this is inside the struct itself (struct can access its own private fields)
        var containingStruct = node.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
        if (containingStruct != null && _structFields.ContainsKey(containingStruct.Identifier.Text))
            return base.VisitMemberAccessExpression(node);

        // L5 structs have public get-only properties — reads should stay as property access (obj.Prop),
        // not be rewritten to getXxx() which doesn't exist.
        if (_propertyAccessStructs.Contains(structName))
            return base.VisitMemberAccessExpression(node);

        // L4 structs: convert obj.Field → obj.getField()
        var getterName = "get" + fieldNameText;
        var getterCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)Visit(node.Expression)!,
                SyntaxFactory.IdentifierName(getterName)),
            SyntaxFactory.ArgumentList())
            .WithTriviaFrom(node);

        return getterCall;
    }

    /// <summary>
    /// Syntactic fallback: checks if a variable name is a parameter of a target struct type
    /// by looking at the enclosing method's parameter list. Returns the struct name or null.
    /// </summary>
    private string? TryGetParameterStructType(string varName, SyntaxNode node)
    {
        var enclosingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (enclosingMethod != null)
        {
            foreach (var param in enclosingMethod.ParameterList.Parameters)
            {
                if (param.Identifier.Text == varName && param.Type != null)
                {
                    var typeName = GetTypeName(param.Type);
                    if (!string.IsNullOrEmpty(typeName) && _structFields.ContainsKey(typeName))
                        return typeName;
                }
            }
        }

        // Also check enclosing constructor
        var enclosingCtor = node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (enclosingCtor != null)
        {
            foreach (var param in enclosingCtor.ParameterList.Parameters)
            {
                if (param.Identifier.Text == varName && param.Type != null)
                {
                    var typeName = GetTypeName(param.Type);
                    if (!string.IsNullOrEmpty(typeName) && _structFields.ContainsKey(typeName))
                        return typeName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a read access expression for a struct field/property.
    /// L4 structs: obj.getField() (private field with getter method).
    /// L5 structs: obj.Field (public get-only property, no getter method).
    /// </summary>
    private ExpressionSyntax CreateReadAccess(ExpressionSyntax receiver, string fieldName, string structName)
    {
        if (_propertyAccessStructs.Contains(structName))
        {
            // L5: property access
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(fieldName));
        }

        // L4: getter method call
        var getterName = "get" + fieldName;
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(getterName)),
            SyntaxFactory.ArgumentList());
    }

    private SyntaxNode? HandleUnaryMutation(MemberAccessExpressionSyntax memberAccess,
        ExpressionSyntax originalNode, bool isIncrement)
    {
        if (memberAccess.Name is not IdentifierNameSyntax fieldName)
            return null;

        var fieldNameText = fieldName.Identifier.Text;

        // Quick check: field name must be in our target fields
        if (!_targetFieldNames.Contains(fieldNameText))
            return null;

        // Don't convert if this is inside the struct itself (struct can access its own
        // private fields, and rewriting WithXxx method bodies creates self-recursion).
        // This mirrors the check in VisitMemberAccessExpression for reads.
        var containingStruct = originalNode.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
        if (containingStruct != null && _structFields.ContainsKey(containingStruct.Identifier.Text))
            return null;

        // Syntactic check: receiver must be a simple identifier or element access
        if (!IsModifiableLValue(memberAccess.Expression))
            return null;

        // Semantic check: verify the receiver's type is actually one of our target structs
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return null;

        var withMethodName = "With" + fieldNameText;
        var receiver = memberAccess.Expression;

        // obj.Field++ → obj = obj.WithField(obj.getField() + 1)
        // L5 structs use property access (obj.Prop); L4 structs use getXxx().
        var getterAccess = CreateReadAccess(receiver, fieldNameText, structName);

        var increment = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(1));

        var newValue = SyntaxFactory.BinaryExpression(
            isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
            getterAccess,
            increment);

        var withCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(withMethodName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(newValue))));

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            receiver.WithoutTrivia(),
            withCall).WithTriviaFrom(originalNode);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (node.Initializer == null || !node.Initializer.Expressions.Any())
            return base.VisitObjectCreationExpression(node);

        // Check if the type name matches one of our target structs
        var typeName = GetTypeName(node.Type);
        if (!_structFields.ContainsKey(typeName))
            return base.VisitObjectCreationExpression(node);

        var fields = _structFields[typeName];
        var hasPublicFieldInit = node.Initializer.Expressions
            .OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax id && fields.Contains(id.Identifier.Text));

        if (!hasPublicFieldInit)
            return base.VisitObjectCreationExpression(node);

        // Extract field name to parameter name mapping
        var arguments = new List<ArgumentSyntax>();
        foreach (var expr in node.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
        {
            if (expr.Left is IdentifierNameSyntax id && fields.Contains(id.Identifier.Text))
            {
                var paramName = ToCamelCase(id.Identifier.Text);
                arguments.Add(SyntaxFactory.Argument(expr.Right)
                    .WithNameColon(SyntaxFactory.NameColon(
                        SyntaxFactory.IdentifierName(paramName))));
            }
        }

        return node.WithInitializer(null)
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
    }

    /// <summary>
    /// Checks if the expression is a modifiable l-value (simple variable or array element).
    /// </summary>
    private static bool IsModifiableLValue(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax => true,
            ElementAccessExpressionSyntax ea => IsModifiableLValue(ea.Expression),
            MemberAccessExpressionSyntax ma => IsModifiableLValue(ma.Expression),
            _ => false
        };
    }

    /// <summary>
    /// Wraps a BinaryExpression in parentheses to preserve C# compound-assignment semantics.
    /// For example, `c.X /= 2.0 * (mb - ma)` means `c.X = c.X / (2.0 * (mb - ma))`.
    /// Without parenthesization, the generated `c.getX() / 2.0 * (mb - ma)` would parse
    /// as `(c.getX() / 2.0) * (mb - ma)` — changing the semantics.
    /// </summary>
    private static ExpressionSyntax ParenthesizeIfBinary(ExpressionSyntax expr)
    {
        if (expr is BinaryExpressionSyntax)
            return SyntaxFactory.ParenthesizedExpression(expr);
        return expr;
    }

    private static string GetTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text == "var" ? string.Empty : id.Identifier.Text,
            QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => string.Empty
        };
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Resolves the target struct name for an expression, or null if not a target struct.
    /// Used to determine whether field/property assignments should be converted to With* calls.
    /// This prevents incorrect conversion when a class has fields with the same names as a struct.
    /// </summary>
    private string? TryGetTargetStructName(ExpressionSyntax expression)
    {
        // For simple identifiers, use the variable type map (fast path)
        if (expression is IdentifierNameSyntax simpleId)
        {
            var varName = simpleId.Identifier.Text;
            if (_variableTypes.TryGetValue(varName, out var typeName))
            {
                if (_structFields.ContainsKey(typeName))
                    return typeName;
            }
        }

        // For complex expressions (member access chains like p1.aPlusCorner),
        // use the semantic model to get the actual type of the receiver
        if (_semanticModel != null)
        {
            try
            {
                var typeInfo = _semanticModel.GetTypeInfo(expression);
                var type = typeInfo.Type;

                if (type != null && _structFields.ContainsKey(type.Name))
                {
                    return type.Name;
                }
            }
            catch (ArgumentException)
            {
                // Node not in syntax tree - fall through to default
            }
        }

        // Fallback: try root identifier for cases where semantic model is unavailable
        var identifierName = GetRootIdentifier(expression);
        if (identifierName != null)
        {
            var rootVarName = identifierName.Identifier.Text;
            if (_variableTypes.TryGetValue(rootVarName, out var rootTypeName))
            {
                if (_structFields.ContainsKey(rootTypeName))
                    return rootTypeName;
            }
        }

        // Default: don't convert if we can't determine the type
        return null;
    }

    /// <summary>
    /// Gets the root identifier name from an expression chain.
    /// For "info.MoreInfo.Path", returns the "info" identifier.
    /// For "o.Path", returns the "o" identifier.
    /// </summary>
    private static IdentifierNameSyntax? GetRootIdentifier(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax id => id,
            MemberAccessExpressionSyntax ma => GetRootIdentifier(ma.Expression),
            ElementAccessExpressionSyntax ea => GetRootIdentifier(ea.Expression),
            _ => null
        };
    }
}
