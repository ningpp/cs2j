using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Updates call sites of migrated void→struct methods:
/// - `s.Method(args);` → `s = s.Method(args);`
/// - `arr[i].Method(args);` → `arr[i] = arr[i].Method(args);`
/// - Return-this methods need no call-site update (semantically compatible).
/// Only updates calls where the receiver type matches the migrated struct.
/// </summary>
public sealed class CallSiteUpdater : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, string> _migratedMethodToStruct;
    private readonly SemanticModel _model;

    public CallSiteUpdater(Dictionary<string, string> migratedMethodToStruct, SemanticModel model)
    {
        _migratedMethodToStruct = migratedMethodToStruct;
        _model = model;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;
            if (_migratedMethodToStruct.TryGetValue(methodName, out var structName))
            {
                var receiver = memberAccess.Expression;
                if (IsReceiverOfStructType(receiver, structName))
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
        }

        return base.VisitExpressionStatement(node);
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
