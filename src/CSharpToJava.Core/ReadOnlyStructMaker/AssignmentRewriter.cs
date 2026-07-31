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
/// Note: This rewriter uses syntactic checks only (no semantic model) because
/// it runs after ReadOnlyStructRewriter has modified the syntax tree.
/// </summary>
public sealed class AssignmentRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, HashSet<string>> _structFields = new(); // structName -> fieldNames
    private readonly HashSet<string> _targetFieldNames = new(); // all field names across all target structs

    public AssignmentRewriter(Dictionary<string, HashSet<string>> structFields, SemanticModel _)
    {
        _structFields = structFields;
        // Collect all field names for quick lookup
        foreach (var fields in structFields.Values)
        {
            foreach (var field in fields)
            {
                _targetFieldNames.Add(field);
            }
        }
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

        var withMethodName = "With" + fieldNameText;
        var receiver = memberAccess.Expression;

        // Handle different assignment types
        ExpressionSyntax newValue;
        switch (node.Kind())
        {
            case SyntaxKind.SimpleAssignmentExpression:
                newValue = node.Right;
                break;
            case SyntaxKind.AddAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.AddExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.SubtractAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.SubtractExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.MultiplyAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.MultiplyExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.DivideAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.DivideExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.ModuloAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.ModuloExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.AndAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.BitwiseAndExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.ExclusiveOrAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.ExclusiveOrExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.OrAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.BitwiseOrExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.LeftShiftAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.LeftShiftExpression, memberAccess, node.Right);
                break;
            case SyntaxKind.RightShiftAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.RightShiftExpression, memberAccess, node.Right);
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

        return HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PreIncrementExpression);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Kind() is not (SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression))
            return base.VisitPostfixUnaryExpression(node);

        if (node.Operand is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitPostfixUnaryExpression(node);

        return HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PostIncrementExpression);
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

        var withMethodName = "With" + fieldNameText;
        var receiver = memberAccess.Expression;

        // obj.Field++ → obj = obj.WithField(obj.Field + 1)
        var increment = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(1));

        var newValue = SyntaxFactory.BinaryExpression(
            isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
            memberAccess,
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

    private static string GetTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
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
}
