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
    // Structs that actually have WithXxx methods — only these may be targets of
    // object-initializer-to-With-chain rewrites.
    private readonly HashSet<string> _withChainStructs = new();

    public AssignmentRewriter(
        Dictionary<string, HashSet<string>> structFields,
        SemanticModel? semanticModel,
        IReadOnlySet<string>? propertyAccessStructs = null,
        IReadOnlySet<string>? withChainStructs = null)
    {
        _structFields = structFields;
        _semanticModel = semanticModel;
        if (propertyAccessStructs != null)
            _propertyAccessStructs.UnionWith(propertyAccessStructs);
        if (withChainStructs != null)
            _withChainStructs.UnionWith(withChainStructs);
        else
            _withChainStructs.UnionWith(structFields.Keys); // legacy behavior: assume all have With methods
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

        // Syntactic check: receiver must be a simple identifier or element access
        // (to ensure we're modifying a variable, not a property return value)
        if (!IsModifiableLValue(memberAccess.Expression))
            return base.VisitAssignmentExpression(node);

        // Semantic check: verify the receiver's type is actually one of our target structs
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return base.VisitAssignmentExpression(node); // Resolved to non-target type - don't rewrite
        if (structName == string.Empty)
        {
            // Type unresolvable (e.g., generic base class field). Use field-name fallback.
            structName = TryGetStructNameByFieldNameAny(fieldNameText);
            if (structName == null)
                return base.VisitAssignmentExpression(node);
        }

        // Don't convert inside non-static members of the struct itself (WithXxx method
        // bodies and migrated methods manage their own writes). STATIC members take
        // struct-typed parameters/locals and must be rewritten like external code.
        if (InsideOwnInstanceMember(node, structName))
            return base.VisitAssignmentExpression(node);

        // A `this.X = ...` write inside a type that is NOT one of the target structs
        // must never be rewritten (the member belongs to that class, not a struct).
        // This guards against unresolvable receiver types falling back to field-name
        // guessing (e.g. a class field sharing a struct field's name).
        if (memberAccess.Expression is ThisExpressionSyntax &&
            !InsideTargetType(node, structName))
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
        if (structName == null || structName == string.Empty)
        {
            // For reads, don't use field-name fallback (too risky for static access like Direction.South)
            // Only try syntactic parameter check for simple identifiers
            if (node.Expression is IdentifierNameSyntax receiverId)
                structName = TryGetParameterStructType(receiverId.Identifier.Text, node);
            if (structName == null || structName == string.Empty)
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

        // Syntactic check: receiver must be a simple identifier or element access
        if (!IsModifiableLValue(memberAccess.Expression))
            return null;

        // Semantic check: verify the receiver's type is actually one of our target structs
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return null; // Resolved to non-target type - don't rewrite
        if (structName == string.Empty)
        {
            // Type unresolvable. Use field-name fallback.
            structName = TryGetStructNameByFieldNameAny(fieldNameText);
            if (structName == null)
                return null;
        }

        // Skip non-static members of the struct itself (see VisitAssignmentExpression).
        if (InsideOwnInstanceMember(originalNode, structName))
            return null;

        // `this.X` inside a non-target type is never a struct field write.
        if (memberAccess.Expression is ThisExpressionSyntax &&
            !InsideTargetType(originalNode, structName))
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
        var initAssignments = node.Initializer.Expressions
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax id && fields.Contains(id.Identifier.Text))
            .ToList();
        if (initAssignments.Count == 0)
            return base.VisitObjectCreationExpression(node);

        // Only rewrite initializers whose members ALL have a matching entry; partial
        // initializers rely on default values and are handled by the default ctor + With chain.

        // Prefer a With-method chain: new S { F = v } → new S().WithF(v)
        // This is robust regardless of constructor parameter names (CS1739-safe) and
        // keeps default values for members not mentioned in the initializer.
        // Only for structs that actually have WithXxx methods.
        if (_withChainStructs.Contains(typeName) && _propertyAccessStructs.Contains(typeName))
        {
            ExpressionSyntax expr = node.WithInitializer(null).WithArgumentList(
                SyntaxFactory.ArgumentList());
            foreach (var assignment in initAssignments)
            {
                var memberName = ((IdentifierNameSyntax)assignment.Left).Identifier.Text;
                expr = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        expr,
                        SyntaxFactory.IdentifierName("With" + memberName)),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument((ExpressionSyntax)Visit(assignment.Right)!))));
            }
            return expr.WithTriviaFrom(node);
        }

        // Legacy path (L4 getXxx structs): convert to named-argument constructor call.
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
    /// True when the enclosing type declaration (class/struct) is the given target
    /// struct. Used to protect `this.X` writes inside unrelated classes.
    /// </summary>
    private static bool InsideTargetType(SyntaxNode node, string structName)
    {
        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return containingType != null && containingType.Identifier.Text == structName;
    }

    /// <summary>
    /// True when the node sits inside a NON-STATIC member (method/property/constructor)
    /// of the target struct itself. Writes there are managed by the struct's own
    /// migration (WithXxx bodies, ctor backing-field rewrites). Static members operate
    /// on struct-typed parameters/locals and behave like external code.
    /// </summary>
    private static bool InsideOwnInstanceMember(SyntaxNode node, string structName)
    {
        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null || containingType.Identifier.Text != structName)
            return false;

        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member switch
        {
            ConstructorDeclarationSyntax => true,
            MethodDeclarationSyntax m => !m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)),
            PropertyDeclarationSyntax p => !p.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)),
            _ => false
        };
    }

    /// <summary>
    /// Checks if the expression is a modifiable l-value (simple variable or array element).
    /// </summary>
    private static bool IsModifiableLValue(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax => true,
            ThisExpressionSyntax => true,
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
    /// Returns: struct name if target, null if resolved to non-target, empty string if unresolvable.
    /// </summary>
    private string? TryGetTargetStructName(ExpressionSyntax expression)
    {
        // Use semantic model FIRST (handles using aliases like P2 -> Point correctly)
        if (_semanticModel != null)
        {
            try
            {
                var typeInfo = _semanticModel.GetTypeInfo(expression);
                var type = typeInfo.Type;

                if (type != null && type.TypeKind != TypeKind.Error)
                {
                    if (_structFields.ContainsKey(type.Name))
                        return type.Name;
                    return null; // Resolved to non-target type
                }
            }
            catch (ArgumentException)
            {
                // Node not in syntax tree - fall through
            }
        }

        // Fallback: variable type map (fast path, but doesn't resolve aliases)
        if (expression is IdentifierNameSyntax simpleId)
        {
            var varName = simpleId.Identifier.Text;
            if (_variableTypes.TryGetValue(varName, out var typeName))
            {
                if (_structFields.ContainsKey(typeName))
                    return typeName;
                // Don't return null here - the type map might have an alias.
                // Fall through to unresolvable.
            }
        }

        // Fallback: try root identifier
        var identifierName = GetRootIdentifier(expression);
        if (identifierName != null)
        {
            var rootVarName = identifierName.Identifier.Text;
            if (_variableTypes.TryGetValue(rootVarName, out var rootTypeName))
            {
                if (_structFields.ContainsKey(rootTypeName))
                    return rootTypeName;
                // The root variable's type IS known and it is not a target struct
                // (e.g. a class with same-named fields). Never fall back to
                // field-name guessing in this case.
                return null;
            }
        }

        // Type truly unresolvable
        return string.Empty;
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

    /// <summary>
    /// Last-resort fallback for WRITE operations: finds a target struct containing the field name.
    /// No length restriction (writes MUST be rewritten for readonly correctness).
    /// Still requires the field to be unique to one target struct to minimize false positives.
    /// </summary>
    private string? TryGetStructNameByFieldNameAny(string fieldName)
    {
        string? match = null;
        int matchCount = 0;
        foreach (var (structName, fields) in _structFields)
        {
            if (fields.Contains(fieldName))
            {
                match = structName;
                matchCount++;
            }
        }
        return matchCount == 1 ? match : null;
    }

    /// <summary>
    /// Conservative fallback: only applies to distinctive field names (length > 3).
    /// Used for contexts where false positives are more dangerous.
    /// </summary>
    private string? TryGetStructNameByFieldName(string fieldName)
    {
        if (fieldName.Length <= 3)
            return null;
        return TryGetStructNameByFieldNameAny(fieldName);
    }
}
