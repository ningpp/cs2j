using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Minimal statement-level rewriter for external writes to target readonly structs:
///   obj.Field = v   -> obj = obj.WithField(v)
///   obj.Field op= v -> obj = obj.WithField(obj.Field op v)
///   obj.Field++ / -- (statement level) -> obj = obj.WithField(obj.Field +/- 1)
/// All other nodes are returned unchanged. Target confirmation is semantic.
/// Anything more complex (tuple assignments, chained assignments, embedded
/// assignment values, unary mutations nested in expressions, object initializers,
/// read getter rewrites, L3 reconstruction, casts) is not handled here and must
/// be normalized by earlier preprocessing passes.
/// </summary>
public sealed class AssignmentRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, HashSet<string>> _structFields;
    private readonly HashSet<string> _targetFieldNames = new();
    private readonly SemanticModel? _semanticModel;
    private readonly Dictionary<string, string> _variableTypes = new();
    private readonly Dictionary<string, HashSet<string>> _qualifiedNames = new(StringComparer.Ordinal);

    public AssignmentRewriter(
        Dictionary<string, HashSet<string>> structFields,
        SemanticModel? semanticModel,
        IReadOnlySet<string>? propertyAccessStructs = null,
        IReadOnlySet<string>? withChainStructs = null,
        IReadOnlyDictionary<string, (HashSet<string> Props, List<string> CtorParams)>? l3Structs = null,
        IReadOnlyDictionary<string, HashSet<string>>? qualifiedNames = null)
    {
        _structFields = structFields;
        _semanticModel = semanticModel;
        if (qualifiedNames != null)
        {
            foreach (var kvp in qualifiedNames)
                _qualifiedNames[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.Ordinal);
        }
        // propertyAccessStructs / withChainStructs / l3Structs are accepted for
        // source compatibility with existing call sites; the minimal rewrite shape
        // assumes every target struct exposes WithXxx(...) methods and property reads.
        foreach (var fields in structFields.Values)
        foreach (var field in fields)
            _targetFieldNames.Add(field);
    }

    /// <summary>
    /// Builds a map of variable names to their declared types. Must be called
    /// before the tree is visited; used only as a fallback when the semantic
    /// model cannot resolve a receiver.
    /// </summary>
    public void BuildVariableTypeMap(SyntaxNode root)
    {
        _variableTypes.Clear();
        foreach (var node in root.DescendantNodes())
        {
            if (node is LocalDeclarationStatementSyntax localDecl
                && localDecl.Declaration.Variables.Count == 1)
            {
                var variable = localDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(localDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                    _variableTypes[variable.Identifier.Text] = varTypeName;
                else if (variable.Initializer?.Value is ObjectCreationExpressionSyntax objCreate)
                    _variableTypes[variable.Identifier.Text] = GetTypeName(objCreate.Type);
            }
            else if (node is FieldDeclarationSyntax fieldDecl
                     && fieldDecl.Declaration.Variables.Count == 1)
            {
                var variable = fieldDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(fieldDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                    _variableTypes[variable.Identifier.Text] = varTypeName;
            }
        }
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // Statement-level member writes only.
        if (node.Parent is not ExpressionStatementSyntax)
            return base.VisitAssignmentExpression(node);

        if (node.Left is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name is not IdentifierNameSyntax fieldName
            || !_targetFieldNames.Contains(fieldName.Identifier.Text)
            || !IsModifiableLValue(memberAccess.Expression))
        {
            return base.VisitAssignmentExpression(node);
        }

        var fieldNameText = fieldName.Identifier.Text;
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return base.VisitAssignmentExpression(node);

        // Writes inside the struct's own instance members manage themselves
        // (WithXxx bodies, constructors, property accessors).
        if (memberAccess.Expression is not IdentifierNameSyntax
            && InsideOwnInstanceMember(node, structName))
        {
            return base.VisitAssignmentExpression(node);
        }
        if (memberAccess.Expression is ThisExpressionSyntax
            && !InsideTargetType(node, structName))
        {
            return base.VisitAssignmentExpression(node);
        }

        var receiver = memberAccess.Expression;
        ExpressionSyntax newValue;
        switch (node.Kind())
        {
            case SyntaxKind.SimpleAssignmentExpression:
                newValue = (ExpressionSyntax)Visit(node.Right)!;
                break;
            case SyntaxKind.AddAssignmentExpression:
            case SyntaxKind.SubtractAssignmentExpression:
            case SyntaxKind.MultiplyAssignmentExpression:
            case SyntaxKind.DivideAssignmentExpression:
            case SyntaxKind.ModuloAssignmentExpression:
            case SyntaxKind.AndAssignmentExpression:
            case SyntaxKind.ExclusiveOrAssignmentExpression:
            case SyntaxKind.OrAssignmentExpression:
            case SyntaxKind.LeftShiftAssignmentExpression:
            case SyntaxKind.RightShiftAssignmentExpression:
                var readAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver.WithoutTrivia(),
                    SyntaxFactory.IdentifierName(fieldNameText));
                var rhs = ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!);
                newValue = SyntaxFactory.BinaryExpression(
                    GetBinaryKind(node.Kind()), readAccess, rhs);
                break;
            default:
                return base.VisitAssignmentExpression(node);
        }

        return BuildWriteAssignment(receiver, fieldNameText, newValue, node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Parent is ExpressionStatementSyntax
            && node.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            && node.Operand is MemberAccessExpressionSyntax memberAccess)
        {
            return RewriteStatementLevelUnary(memberAccess, node,
                node.Kind() == SyntaxKind.PreIncrementExpression)
                ?? base.VisitPrefixUnaryExpression(node);
        }
        return base.VisitPrefixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Parent is ExpressionStatementSyntax
            && node.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression
            && node.Operand is MemberAccessExpressionSyntax memberAccess)
        {
            return RewriteStatementLevelUnary(memberAccess, node,
                node.Kind() == SyntaxKind.PostIncrementExpression)
                ?? base.VisitPostfixUnaryExpression(node);
        }
        return base.VisitPostfixUnaryExpression(node);
    }

    private SyntaxNode? RewriteStatementLevelUnary(
        MemberAccessExpressionSyntax memberAccess, ExpressionSyntax original, bool isIncrement)
    {
        if (memberAccess.Name is not IdentifierNameSyntax fieldName
            || !_targetFieldNames.Contains(fieldName.Identifier.Text)
            || !IsModifiableLValue(memberAccess.Expression))
        {
            return null;
        }

        var fieldNameText = fieldName.Identifier.Text;
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return null;
        if (memberAccess.Expression is not IdentifierNameSyntax
            && InsideOwnInstanceMember(original, structName))
        {
            return null;
        }
        if (memberAccess.Expression is ThisExpressionSyntax
            && !InsideTargetType(original, structName))
        {
            return null;
        }

        var receiver = memberAccess.Expression;
        var readAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver.WithoutTrivia(),
            SyntaxFactory.IdentifierName(fieldNameText));
        var newValue = SyntaxFactory.BinaryExpression(
            isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
            readAccess,
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));

        return BuildWriteAssignment(receiver, fieldNameText, newValue, original);
    }

    /// <summary>
    /// receiver.Member = value  ->  receiver = receiver.WithMember(value)
    /// </summary>
    private static AssignmentExpressionSyntax BuildWriteAssignment(
        ExpressionSyntax receiver, string memberName, ExpressionSyntax value, SyntaxNode triviaFrom)
    {
        var withCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName("With" + memberName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(value))));

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            receiver.WithoutTrivia(),
            withCall).WithTriviaFrom(triviaFrom);
    }

    private static SyntaxKind GetBinaryKind(SyntaxKind compoundKind)
        => compoundKind switch
        {
            SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
            SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
            SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
            SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
            SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
            SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
            SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
            SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
            SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
            _ => throw new ArgumentOutOfRangeException(nameof(compoundKind))
        };

    /// <summary>
    /// Parenthesize a binary RHS so compound-assignment precedence is preserved:
    /// x.F /= a * b must become x = x.WithF(x.F / (a * b)).
    /// </summary>
    private static ExpressionSyntax ParenthesizeIfBinary(ExpressionSyntax expr)
        => expr is BinaryExpressionSyntax ? SyntaxFactory.ParenthesizedExpression(expr) : expr;

    private static bool IsModifiableLValue(ExpressionSyntax expression)
        => expression switch
        {
            IdentifierNameSyntax => true,
            ThisExpressionSyntax => true,
            ElementAccessExpressionSyntax ea => IsModifiableLValue(ea.Expression),
            MemberAccessExpressionSyntax ma => IsModifiableLValue(ma.Expression),
            _ => false
        };

    private static bool InsideTargetType(SyntaxNode node, string structName)
    {
        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return containingType != null && containingType.Identifier.Text == structName;
    }

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
    /// Resolves the receiver's type to a target struct name. Returns null when the
    /// receiver resolves to a non-target type (or cannot be resolved). Semantic
    /// model is the primary source; the prebuilt variable map is the fallback.
    /// </summary>
    private string? TryGetTargetStructName(ExpressionSyntax expression)
    {
        if (_semanticModel != null)
        {
            try
            {
                var type = _semanticModel.GetTypeInfo(expression).Type;
                if (type != null)
                {
                    if (type.TypeKind is TypeKind.Error or not TypeKind.Struct)
                        return null;

                    if (_qualifiedNames.Count > 0
                        && type is INamedTypeSymbol namedType)
                    {
                        return _qualifiedNames.TryGetValue(type.Name, out var names)
                               && names.Contains(GetQualifiedTypeName(namedType))
                            ? type.Name
                            : null;
                    }
                    return _structFields.ContainsKey(type.Name) ? type.Name : null;
                }
            }
            catch (ArgumentException)
            {
                // Synthesized node not present in the modeled tree — fall through.
            }
        }

        var rootId = GetRootIdentifier(expression);
        if (rootId != null
            && _variableTypes.TryGetValue(rootId.Identifier.Text, out var typeName)
            && _structFields.ContainsKey(typeName))
        {
            return typeName;
        }
        return null;
    }

    private static IdentifierNameSyntax? GetRootIdentifier(ExpressionSyntax expression)
        => expression switch
        {
            IdentifierNameSyntax id => id,
            MemberAccessExpressionSyntax ma => GetRootIdentifier(ma.Expression),
            ElementAccessExpressionSyntax ea => GetRootIdentifier(ea.Expression),
            _ => null
        };

    private static string GetTypeName(TypeSyntax type)
        => type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text == "var" ? string.Empty : id.Identifier.Text,
            QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => string.Empty
        };

    /// <summary>
    /// Qualified name from a resolved symbol: namespace + containing-type chain + name.
    /// </summary>
    internal static string GetQualifiedTypeName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var t = (ITypeSymbol?)type; t is INamedTypeSymbol nts; t = nts.ContainingType)
            parts.Add(nts.Name);
        for (var ns = type.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            parts.Add(ns.Name);
        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>
    /// Qualified name from syntax: namespace + containing-type chain + name.
    /// </summary>
    public static string GetQualifiedStructName(StructDeclarationSyntax node)
    {
        var parts = new List<string>();
        for (var n = (SyntaxNode?)node; n != null; n = n.Parent)
        {
            if (n is TypeDeclarationSyntax t)
                parts.Add(t.Identifier.Text);
            else if (n is BaseNamespaceDeclarationSyntax ns)
                parts.Add(ns.Name.ToString());
        }
        parts.Reverse();
        return string.Join(".", parts);
    }
}
