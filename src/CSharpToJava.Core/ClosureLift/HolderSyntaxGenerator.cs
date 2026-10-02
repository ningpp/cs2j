using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Annotation helpers (design §4.1 idempotence). Annotations live on in-memory
/// nodes only and never survive the text round-trip; the durable re-entry
/// guards are the reserved name prefixes (__cs2jClosure* / __cs2jArr_ /
/// Cs2jDisplayClass_* / _cs2jCap_), which CaptureAnalyzer already skips.
/// </summary>
internal static class ClosureLiftAnnotations
{
    public const string Kind = "cs2j_closure_lift";

    public static SyntaxAnnotation HolderClass(string className) => new(Kind, "class:" + className);

    public static SyntaxAnnotation HolderInstance(string instanceName) => new(Kind, "instance:" + instanceName);
}

/// <summary>
/// Builds the holder shape (design §4.4): private sealed nested class with
/// public mutable fields; instance declaration + field-assignment statements;
/// and the non-default single-element T[] comparator shape.
/// No method/property members are emitted — the class is the strict
/// field-only subset of a C45 display class (design §3.4).
/// </summary>
internal sealed class HolderSyntaxGenerator
{
    /// <summary>
    /// private sealed class Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;
    /// {
    ///     public &lt;T&gt; _cs2jCap_&lt;name&gt;;
    /// }
    /// </summary>
    public ClassDeclarationSyntax BuildHolderClass(HolderGroupPlan group)
    {
        var fields = group.Symbols
            .Select(s => (MemberDeclarationSyntax)SyntaxFactory.FieldDeclaration(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(s.TypeDisplayFullyQualified),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(s.FieldName)))))
            .ToArray();

        var declaration = SyntaxFactory.ClassDeclaration(group.ClassName)
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword))
            .WithMembers(SyntaxFactory.List(fields))
            .WithAdditionalAnnotations(ClosureLiftAnnotations.HolderClass(group.ClassName));

        // Generic method capture: the nested holder cannot see method arity from
        // its class-level nesting position, so redeclare the same parameters.
        if (group.MethodTypeParameters.Count > 0)
        {
            declaration = declaration.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(
                    SyntaxFactory.SeparatedList(
                        group.MethodTypeParameters.Select(SyntaxFactory.TypeParameter))));
        }

        return declaration;
    }

    /// <summary>var __cs2jClosureN = new Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;();</summary>
    public LocalDeclarationStatementSyntax BuildHolderInstanceDeclaration(HolderGroupPlan group)
    {
        var objectCreation = SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName(group.ClassName + group.TypeArgumentSuffix))
            .WithArgumentList(SyntaxFactory.ArgumentList());

        var declarator = SyntaxFactory.VariableDeclarator(group.InstanceName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(objectCreation));

        return SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(VarType(), SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithAdditionalAnnotations(ClosureLiftAnnotations.HolderInstance(group.InstanceName));
    }

    /// <summary>__cs2jClosureN._cs2jCap_x = value;</summary>
    public ExpressionStatementSyntax BuildFieldAssignment(
        string instanceName,
        string fieldName,
        ExpressionSyntax value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                BuildFieldAccess(instanceName, fieldName),
                value));

    /// <summary>__cs2jClosureN._cs2jCap_x — the only site shape C45 must keep (design §3.4).</summary>
    public MemberAccessExpressionSyntax BuildFieldAccess(string instanceName, string fieldName)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(instanceName),
            SyntaxFactory.IdentifierName(fieldName));

    /// <summary>
    /// Array form (non-default): T[] __cs2jArr_x = new T[1];
    /// One storage per symbol even within one scope (design D1② tradeoff note).
    /// </summary>
    public LocalDeclarationStatementSyntax BuildArrayStorageDeclaration(CapturedSymbolPlan symbol)
    {
        var elementType = SyntaxFactory.ParseTypeName(symbol.TypeDisplayFullyQualified);

        var newExpression = SyntaxFactory.ArrayCreationExpression(
            SyntaxFactory.ArrayType(elementType)
                .WithRankSpecifiers(SyntaxFactory.SingletonList(
                    // new T[1]: a fixed one-slot box. The slot zero-inits like
                    // an uninitialized local; Task 9/10 append an explicit [0]
                    // assignment at the declaration site when an initializer
                    // exists. (new T[] without size or initializer is illegal.)
                    SyntaxFactory.ArrayRankSpecifier(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(1)))))));

        var declarator = SyntaxFactory.VariableDeclarator(symbol.StorageName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(newExpression));

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(VarType(), SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    /// <summary>__cs2jArr_x[0] = value;</summary>
    public ExpressionStatementSyntax BuildArrayAssignment(string storageName, ExpressionSyntax value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                BuildArrayElementAccess(storageName),
                value));

    /// <summary>__cs2jArr_x[0] — array-form read/write/ref site shape.</summary>
    public ElementAccessExpressionSyntax BuildArrayElementAccess(string storageName)
        => SyntaxFactory.ElementAccessExpression(SyntaxFactory.IdentifierName(storageName))
            .WithArgumentList(SyntaxFactory.BracketedArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(0))))));

    private static IdentifierNameSyntax VarType()
        // IdentifierName requires an IdentifierToken; SyntaxKind.VarKeyword is a
        // contextual-keyword token and throws ArgumentException("identifier").
        // Spelling the identifier "var" parses as var in an implicit local.
        => SyntaxFactory.IdentifierName("var");
}
