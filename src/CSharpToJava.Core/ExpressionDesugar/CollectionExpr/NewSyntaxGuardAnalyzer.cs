using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// CE-GUARD (design §2.5): inventory / explicit rejection of C# 11/12 syntax
/// the family does not lower — interface <c>static abstract/virtual</c>
/// members (0585), <c>scoped</c> (0586) and generic attributes (0587);
/// collection expressions inside expression trees are defended by the rule
/// itself (0588).
///
/// M3 runs the semantic inventory in the SAME Analyze pass as the
/// collection-site analysis and never rewrites any guarded node. The
/// diagnostics flow into the family's ordinary atomic gate: a strict guard
/// declaration Error makes the whole file Changed=false even when sibling
/// collection sites could be rewritten.
///
/// Call-site inventory (0585 Warning) is symbol-based; it only runs for files
/// the cheap syntax scan already marked dirty (collection sites / guard
/// declarations / scoped tokens / generic attributes). A file containing
/// nothing but static-abstract call sites is still a valid byte-preserved
/// copy, so its Warning-only inventory is not a correctness gate.
/// </summary>
internal static class NewSyntaxGuardAnalyzer
{
    private const string AttributeMetadataName = "System.Attribute";

    /// <summary>
    /// Cheap syntax-only trigger: true if the file may contain any CE-GUARD
    /// syntax. Deliberately over-inclusive (semantic precision lives in
    /// <see cref="Analyze"/>).
    /// </summary>
    public static bool ContainsGuardSyntax(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(IsGuardNode)
           || root.DescendantTokens()
               .Any(token => token.IsKind(SyntaxKind.ScopedKeyword));

    /// <summary>
    /// Single-node CE-GUARD predicate shared with
    /// <see cref="ExpressionDesugarTriggerScan"/> so the family dirty gate can
    /// evaluate the guard trigger inside its one combined traversal instead of
    /// a dedicated walk. Deliberately over-inclusive (semantic precision lives
    /// in <see cref="Analyze"/>).
    /// </summary>
    public static bool IsGuardNode(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => IsInterfaceStaticAbstractOrVirtual(m),
        PropertyDeclarationSyntax p => IsInterfaceStaticAbstractOrVirtual(p),
        IndexerDeclarationSyntax i => IsInterfaceStaticAbstractOrVirtual(i),
        EventDeclarationSyntax e => IsInterfaceStaticAbstractOrVirtual(e),
        EventFieldDeclarationSyntax ef => IsInterfaceStaticAbstractOrVirtual(ef),
        OperatorDeclarationSyntax o => IsInterfaceStaticAbstractOrVirtual(o),
        ConversionOperatorDeclarationSyntax co => IsInterfaceStaticAbstractOrVirtual(co),
        ClassDeclarationSyntax c => IsGenericAttributeDeclarationSyntax(c),
        RecordDeclarationSyntax r => IsGenericAttributeDeclarationSyntax(r),
        AttributeSyntax a => HasGenericAttributeName(a),
        _ => false,
    };

    /// <summary>
    /// Semantic inventory (design §2.5). Emits 0585/0586/0587 diagnostics and
    /// fills the guard statistics buckets. Called once per dirty file from
    /// <see cref="CollectionExprRule.Analyze"/>; a null model means the family
    /// already took the safe 0571 skip path.
    /// </summary>
    public static void Analyze(RuleAnalysisContext context)
    {
        var model = context.SemanticModel;
        if (model == null)
            return;

        var strict = context.Options.CollectionExpr.Strict;
        var root = context.Root;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case OperatorDeclarationSyntax op:
                    CheckDeclaration(
                        context, model.GetDeclaredSymbol(op), node, strict,
                        GuardKind.Operator);
                    break;

                case ConversionOperatorDeclarationSyntax cop:
                    CheckDeclaration(
                        context, model.GetDeclaredSymbol(cop), node, strict,
                        GuardKind.ConversionOperator);
                    break;

                case MethodDeclarationSyntax method:
                    CheckDeclaration(
                        context, model.GetDeclaredSymbol(method), node, strict,
                        GuardKind.Method);
                    break;

                case PropertyDeclarationSyntax property:
                    // The property is the single declaration site; its
                    // accessor symbols carry the same flags and are not
                    // counted again.
                    CheckDeclaration(
                        context, model.GetDeclaredSymbol(property), node, strict,
                        GuardKind.Property);
                    break;

                case IndexerDeclarationSyntax indexer:
                    CheckDeclaration(
                        context, model.GetDeclaredSymbol(indexer), node, strict,
                        GuardKind.Indexer);
                    break;

                case EventDeclarationSyntax @event:
                    CheckDeclaration(
                        context, model.GetDeclaredSymbol(@event), node, strict,
                        GuardKind.Event);
                    break;

                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        CheckDeclaration(
                            context, model.GetDeclaredSymbol(variable), node, strict,
                            GuardKind.Event);
                    }
                    break;

                // Generic attribute DECLARATION (a generic class/record whose
                // base chain reaches System.Attribute).
                case ClassDeclarationSyntax classDeclaration
                    when classDeclaration.TypeParameterList != null:
                    CheckGenericAttributeDeclaration(
                        context, model.GetDeclaredSymbol(classDeclaration), node, strict);
                    break;

                case RecordDeclarationSyntax recordDeclaration
                    when recordDeclaration.TypeParameterList != null:
                    CheckGenericAttributeDeclaration(
                        context, model.GetDeclaredSymbol(recordDeclaration), node, strict);
                    break;

                // Generic attribute APPLICATION ([X<int>()]).
                case AttributeSyntax attribute when HasGenericAttributeName(attribute):
                    CheckGenericAttributeApplication(context, model, attribute, strict);
                    break;

                // 0585 call/use sites through an interface type.
                case MemberAccessExpressionSyntax memberAccess:
                    CheckUseSite(
                        context, model.GetSymbolInfo(memberAccess).Symbol,
                        memberAccess, strict);
                    foreach (var candidate in model.GetSymbolInfo(memberAccess).CandidateSymbols)
                        CheckUseSite(context, candidate, memberAccess, strict);
                    break;

                case ElementAccessExpressionSyntax elementAccess:
                    CheckUseSite(
                        context, model.GetSymbolInfo(elementAccess).Symbol,
                        elementAccess, strict);
                    break;

                // Static abstract/virtual operators (generic math): binary,
                // unary, compound-assignment and explicit-conversion nodes
                // resolve to the interface operator IMethodSymbol.
                case BinaryExpressionSyntax binary:
                    CheckUseSite(
                        context, model.GetSymbolInfo(binary).Symbol, binary, strict);
                    break;

                case AssignmentExpressionSyntax assignment:
                    // An event hookup (I.Event += h) hands back the IEventSymbol,
                    // already counted via its MemberAccessExpression; only
                    // underlying operator IMethodSymbols (+= / -= / *= ...)
                    // are operator call sites here.
                    if (model.GetSymbolInfo(assignment).Symbol is IMethodSymbol)
                    {
                        CheckUseSite(
                            context, model.GetSymbolInfo(assignment).Symbol,
                            assignment, strict);
                    }
                    break;

                case PrefixUnaryExpressionSyntax prefixUnary:
                    CheckUseSite(
                        context, model.GetSymbolInfo(prefixUnary).Symbol, prefixUnary, strict);
                    break;

                case PostfixUnaryExpressionSyntax postfixUnary:
                    CheckUseSite(
                        context, model.GetSymbolInfo(postfixUnary).Symbol, postfixUnary, strict);
                    break;

                case CastExpressionSyntax cast:
                    CheckUseSite(
                        context, model.GetSymbolInfo(cast).Symbol, cast, strict);
                    break;
            }
        }

        AnalyzeScopedTokens(context, strict, root);
    }

    // ---- 0585: static abstract/virtual interface members ----

    private enum GuardKind
    {
        Method,
        Property,
        Indexer,
        Event,
        Operator,
        ConversionOperator,
    }

    private static void CheckDeclaration(
        RuleAnalysisContext context,
        ISymbol? symbol,
        SyntaxNode node,
        bool strict,
        GuardKind kind)
    {
        if (!IsStaticAbstractOrVirtualInterfaceMember(symbol))
            return;

        context.Statistics.CollectionExpr.GuardStaticAbstractDeclarations++;
        context.Report(
            CollectionExprDiagnostics.ResolveSeverity(
                CollectionExprDiagnosticCodes.CS2J0585,
                strict,
                atDeclaration: true),
            CollectionExprDiagnosticCodes.CS2J0585,
            string.Format(
                CollectionExprDiagnostics.StaticAbstractMessage,
                KindText(kind),
                symbol!.ToDisplayString()),
            node);
    }

    private static void CheckUseSite(
        RuleAnalysisContext context,
        ISymbol? symbol,
        SyntaxNode node,
        bool strict)
    {
        if (!IsStaticAbstractOrVirtualInterfaceMember(symbol))
            return;

        // A member declaration node is not a use site (defensive: GetSymbolInfo
        // on some declaration shapes can hand back the declared symbol).
        if (node is MemberDeclarationSyntax)
            return;

        var stats = context.Statistics.CollectionExpr;
        stats.GuardStaticAbstractCallSites++;

        context.Report(
            CollectionExprDiagnostics.ResolveSeverity(
                CollectionExprDiagnosticCodes.CS2J0585,
                strict,
                atDeclaration: false),
            CollectionExprDiagnosticCodes.CS2J0585,
            string.Format(
                CollectionExprDiagnostics.StaticAbstractCallMessage,
                UseSiteKindText(symbol),
                symbol!.ToDisplayString()),
            node);
    }

    private static string UseSiteKindText(ISymbol symbol) => symbol switch
    {
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        IMethodSymbol { MethodKind: MethodKind.Conversion } => "conversion operator",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.BuiltinOperator }
            => "operator",
        IMethodSymbol => "method",
        _ => "member",
    };

    /// <summary>
    /// Symbol test (never text-based): a static member that is abstract or
    /// virtual AND is declared by an interface. This is exactly the feature
    /// shape Java has no equivalent for (design §2.5 row 1).
    /// </summary>
    private static bool IsStaticAbstractOrVirtualInterfaceMember(ISymbol? symbol)
    {
        if (symbol is null || !symbol.IsStatic)
            return false;

        if (!symbol.IsAbstract && !symbol.IsVirtual)
            return false;

        return symbol.ContainingType?.TypeKind == TypeKind.Interface;
    }

    private static string KindText(GuardKind kind) => kind switch
    {
        GuardKind.Method => "method",
        GuardKind.Property => "property",
        GuardKind.Indexer => "indexer",
        GuardKind.Event => "event",
        GuardKind.Operator => "operator",
        GuardKind.ConversionOperator => "conversion operator",
        _ => "member",
    };

    // ---- 0587: generic attributes ----

    private static void CheckGenericAttributeDeclaration(
        RuleAnalysisContext context,
        INamedTypeSymbol? type,
        SyntaxNode node,
        bool strict)
    {
        if (type is not { IsGenericType: true } || !DerivesFromAttribute(type))
            return;

        context.Statistics.CollectionExpr.GuardGenericAttributeDeclarations++;
        context.Report(
            CollectionExprDiagnostics.ResolveSeverity(
                CollectionExprDiagnosticCodes.CS2J0587,
                strict,
                atDeclaration: true),
            CollectionExprDiagnosticCodes.CS2J0587,
            string.Format(
                CollectionExprDiagnostics.GenericAttributeMessage,
                type.ToDisplayString()),
            node);
    }

    private static void CheckGenericAttributeApplication(
        RuleAnalysisContext context,
        SemanticModel model,
        AttributeSyntax attribute,
        bool strict)
    {
        var genericName = attribute.Name.DescendantNodesAndSelf()
            .OfType<GenericNameSyntax>()
            .FirstOrDefault();
        if (genericName == null)
            return;

        // Generic attributes are rejected by the baseline compiler, so the
        // name can fail to bind normally; candidates (and binding the
        // AttributeSyntax node itself) still expose the constructor symbol.
        var nameInfo = model.GetSymbolInfo(genericName);
        var attributeInfo = model.GetSymbolInfo(attribute);
        var constructor =
            nameInfo.Symbol as IMethodSymbol
            ?? attributeInfo.Symbol as IMethodSymbol
            ?? nameInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault()
            ?? attributeInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        var attributeType = constructor?.ContainingType;

        // Final defensive fallback: resolve the attribute type symbol through
        // the generic name's type binding when no constructor is available.
        if (attributeType == null)
        {
            var typeInfo = model.GetTypeInfo(genericName);
            attributeType = (typeInfo.Type ?? typeInfo.ConvertedType) as INamedTypeSymbol;
        }

        // Symbol verification: the name is a generic attribute class. Broken
        // input (baseline CS8668 territory) may only expose candidates; the
        // over-inclusive cheap syntax trigger plus candidate symbols still
        // reaches the inventory.
        if (attributeType is not { IsGenericType: true } || !DerivesFromAttribute(attributeType))
            return;

        context.Statistics.CollectionExpr.GuardGenericAttributeApplications++;
        context.Report(
            CollectionExprDiagnostics.ResolveSeverity(
                CollectionExprDiagnosticCodes.CS2J0587,
                strict,
                atDeclaration: false),
            CollectionExprDiagnosticCodes.CS2J0587,
            string.Format(
                CollectionExprDiagnostics.GenericAttributeMessage,
                attributeType.ToDisplayString()),
            attribute);
    }

    private static bool DerivesFromAttribute(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current != null)
        {
            if (current.ToDisplayString() == AttributeMetadataName)
                return true;

            current = current.BaseType;
        }

        return false;
    }

    // ---- 0586: scoped ----

    private static void AnalyzeScopedTokens(
        RuleAnalysisContext context,
        bool strict,
        CompilationUnitSyntax root)
    {
        foreach (var token in root.DescendantTokens())
        {
            if (!token.IsKind(SyntaxKind.ScopedKeyword))
                continue;

            // Parameter form -> Info (C01 strips downstream, counting only).
            // Local declarations and every other shape -> Warning (C06/C01).
            var isParameter = token.Parent.IsKind(SyntaxKind.Parameter);

            if (isParameter)
                context.Statistics.CollectionExpr.GuardScopedParameterSites++;
            else
                context.Statistics.CollectionExpr.GuardScopedLocalSites++;

            context.Report(
                CollectionExprDiagnostics.ResolveSeverity(
                    CollectionExprDiagnosticCodes.CS2J0586,
                    strict,
                    parameterShape: isParameter),
                CollectionExprDiagnosticCodes.CS2J0586,
                string.Format(
                    CollectionExprDiagnostics.ScopedMessage,
                    isParameter ? "parameter" : "local/other-form"),
                token.Parent);
        }
    }

    // ---- Syntax-only cheap scan helpers (M0 shape, kept over-inclusive) ----

    /// <summary>
    /// Interface member (or field-like event) declared <c>static abstract</c>
    /// or <c>static virtual</c>. Syntax-only: parent must be an interface and
    /// the modifier list carries static + (abstract|virtual).
    /// </summary>
    private static bool IsInterfaceStaticAbstractOrVirtual(MemberDeclarationSyntax member)
    {
        if (member.Parent is not InterfaceDeclarationSyntax)
            return false;

        var modifiers = member.Modifiers;
        var isStatic = modifiers.Any(t => t.IsKind(SyntaxKind.StaticKeyword));
        var isAbstractOrVirtual = modifiers.Any(t => t.IsKind(SyntaxKind.AbstractKeyword))
            || modifiers.Any(t => t.IsKind(SyntaxKind.VirtualKeyword));
        return isStatic && isAbstractOrVirtual;
    }

    /// <summary>
    /// Generic attribute declaration: a generic class/record declaration whose
    /// base list mentions an Attribute-suffixed name (cheap name match;
    /// <see cref="Analyze"/> resolves the base symbol).
    /// </summary>
    private static bool IsGenericAttributeDeclarationSyntax(TypeDeclarationSyntax typeDeclaration)
    {
        if (typeDeclaration.TypeParameterList == null
            || typeDeclaration.BaseList == null)
        {
            return false;
        }

        return typeDeclaration.BaseList.Types.Any(baseType =>
            baseType.Type is NameSyntax name && NameEndsWithAttribute(name));
    }

    /// <summary>
    /// Generic attribute application: the attribute name (possibly qualified)
    /// contains a generic name such as <c>X&lt;int&gt;</c>.
    /// </summary>
    private static bool HasGenericAttributeName(AttributeSyntax attribute)
        => attribute.Name.DescendantNodesAndSelf().OfType<GenericNameSyntax>().Any();

    private static bool NameEndsWithAttribute(NameSyntax name)
    {
        var text = name switch
        {
            GenericNameSyntax generic => generic.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.Text,
            _ => name.ToString(),
        };
        return text == "Attribute" || text.EndsWith("Attribute", StringComparison.Ordinal);
    }
}
