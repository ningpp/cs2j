using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45 M2 display-class syntax shapes (design §4.2, D5/D7/D9/D19). Everything
/// here is additive to the C32 holder shape: read-only capture fields, the
/// `_cs2jThis` field + constructor (D7), migrated `Lambda_&lt;k&gt;` instance methods
/// (D9) and the full class for pure read-only/this groups. The C32 field-only
/// holder emission itself stays in <see cref="HolderSyntaxGenerator"/> untouched,
/// so groups without C45 additions keep byte-identical classes (D2 red line).
/// </summary>
internal sealed class DisplayClassSyntaxEmitter
{
    /// <summary>`public &lt;T&gt; _cs2jCap_&lt;name&gt;;` — same field shape as C32 holders (D5/D2).</summary>
    public FieldDeclarationSyntax BuildCaptureField(CapturedSymbolPlan symbol)
        => SyntaxFactory.FieldDeclaration(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(symbol.TypeDisplayFullyQualified),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(symbol.FieldName))));

    /// <summary>`private readonly THost _cs2jThis;` (D7).</summary>
    public FieldDeclarationSyntax BuildThisField(string hostTypeDisplay)
        => SyntaxFactory.FieldDeclaration(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)),
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(hostTypeDisplay),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(ClosureLiftNaming.ThisFieldName))));

    /// <summary>D4: `private readonly &lt;ParentClass&gt; _cs2jParent;`.</summary>
    public FieldDeclarationSyntax BuildParentField(string parentTypeDisplay)
        => SyntaxFactory.FieldDeclaration(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)),
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(parentTypeDisplay),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(ClosureLiftNaming.ParentFieldName))));

    /// <summary>`public &lt;Class&gt;(&lt;THost&gt; @this) { _cs2jThis = @this; }` (D7).</summary>
    public ConstructorDeclarationSyntax BuildThisConstructor(
        string className,
        string hostTypeDisplay)
        => SyntaxFactory.ConstructorDeclaration(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.Identifier(className),
                SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("@this"))
                        .WithType(SyntaxFactory.ParseTypeName(hostTypeDisplay)))),
                initializer: null,
                SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(ClosureLiftNaming.ThisFieldName),
                        SyntaxFactory.IdentifierName("@this")))),
                SyntaxFactory.Token(SyntaxKind.None));

    /// <summary>D4: `public &lt;Class&gt;(&lt;ParentClass&gt; parent) { _cs2jParent = parent; }`.</summary>
    public ConstructorDeclarationSyntax BuildParentConstructor(
        string className,
        string parentTypeDisplay)
        => SyntaxFactory.ConstructorDeclaration(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)),
                SyntaxFactory.Identifier(className),
                SyntaxFactory.ParameterList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("parent"))
                        .WithType(SyntaxFactory.ParseTypeName(parentTypeDisplay)))),
                initializer: null,
                SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(ClosureLiftNaming.ParentFieldName),
                        SyntaxFactory.IdentifierName("parent")))),
                SyntaxFactory.Token(SyntaxKind.None));

    /// <summary>`internal &lt;ret&gt; Lambda_&lt;k&gt;(...)` — built by the lifter; only the
    /// deterministic modifiers/order contract lives here.</summary>
    public ClassDeclarationSyntax BuildDisplayClass(
        DisplayClassPlan group,
        string hostTypeDisplay,
        IEnumerable<MethodDeclarationSyntax> methods)
    {
        var members = new List<MemberDeclarationSyntax>();
        members.AddRange(group.Symbols.Select(BuildCaptureField));
        if (group.Parent != null)
        {
            // D4 nested class: the parent display instance is the only ctor
            // input; host `this` (if any) is reached via the parent chain.
            members.Add(BuildParentField(group.Parent.QualifiedClassName));
            members.Add(BuildParentConstructor(group.ClassName, group.Parent.QualifiedClassName));
        }
        else if (group.CapturesThis)
        {
            members.Add(BuildThisField(hostTypeDisplay));
            members.Add(BuildThisConstructor(group.ClassName, hostTypeDisplay));
        }

        members.AddRange(methods);

        var declaration = SyntaxFactory.ClassDeclaration(group.ClassName)
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword))
            .WithMembers(SyntaxFactory.List(members))
            .WithAdditionalAnnotations(ClosureLiftAnnotations.HolderClass(group.ClassName));

        // D19: method type parameters are redeclared as the class's own arity
        // (same C32 rule); host-type parameters are already in nested-class scope.
        if (group.MethodTypeParameters.Count > 0)
        {
            declaration = declaration.WithTypeParameterList(
                SyntaxFactory.TypeParameterList(
                    SyntaxFactory.SeparatedList(
                        group.MethodTypeParameters.Select(SyntaxFactory.TypeParameter))));
        }

        return declaration;
    }

    /// <summary>`var __cs2jClosureN = new Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;([this]);` (D14②).</summary>
    public LocalDeclarationStatementSyntax BuildInstanceDeclaration(DisplayClassPlan group)
    {
        var arguments = group.CapturesThis
            ? SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(SyntaxFactory.ThisExpression())))
            : SyntaxFactory.ArgumentList();

        var objectCreation = SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName(group.ClassName + group.TypeArgumentSuffix))
            .WithArgumentList(arguments);

        var declarator = SyntaxFactory.VariableDeclarator(group.InstanceName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(objectCreation));

        return SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(VarType(), SyntaxFactory.SingletonSeparatedList(declarator)))
            .WithAdditionalAnnotations(ClosureLiftAnnotations.HolderInstance(group.InstanceName));
    }

    /// <summary>`__cs2jClosureN._cs2jCap_x = &lt;value&gt;;` — read-only snapshot (D5/D14).</summary>
    public ExpressionStatementSyntax BuildSnapshotAssignment(
        DisplayClassPlan group,
        CapturedSymbolPlan symbol,
        ExpressionSyntax value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(group.InstanceName),
                    SyntaxFactory.IdentifierName(symbol.FieldName)),
                value));

    /// <summary>
    /// D4: `var __cs2jClosureN = new &lt;OuterQual&gt;.&lt;Child&gt;(this);` created at the
    /// entry of a migrated parent method's block (foreach bodies naturally make
    /// this per-iteration).
    /// </summary>
    public static LocalDeclarationStatementSyntax BuildNestedInstanceDeclaration(
        string instanceName,
        string qualifiedChildType)
    {
        var objectCreation = SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName(qualifiedChildType))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression()))));

        var declarator = SyntaxFactory.VariableDeclarator(instanceName)
            .WithInitializer(SyntaxFactory.EqualsValueClause(objectCreation));

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(VarType(), SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    /// <summary>D4/D8: `new &lt;DelType&gt;(__cs2jClosureN.Lambda_k)` for a nested site.</summary>
    public static ObjectCreationExpressionSyntax BuildNestedDelegateCreation(
        string delegateTypeDisplay,
        string instanceName,
        string methodName)
        => SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName(delegateTypeDisplay))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(instanceName),
                        SyntaxFactory.IdentifierName(methodName))))));

    private static IdentifierNameSyntax VarType()
        // Same spelling rule as HolderSyntaxGenerator (contextual keyword token).
        => SyntaxFactory.IdentifierName("var");
}
