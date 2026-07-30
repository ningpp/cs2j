using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed record AnalyzeResult(
    ConversionLevel Level,
    StructPattern Pattern,
    bool ShouldRewrite,
    string Reason,
    string? QualifiedName,
    IReadOnlyList<MethodMigrationInfo>? MethodMigrations = null);

internal sealed record MethodMigrationInfo(
    IMethodSymbol Method,
    MigrationType Type,
    MethodDeclarationSyntax Syntax);

internal sealed class StructAnalyzer
{
    private readonly ReadOnlyStructMakerOptions _options;
    private readonly SemanticModel _model;
    private readonly CallGraphBuilder _callGraph;

    public StructAnalyzer(ReadOnlyStructMakerOptions options, SemanticModel model)
    {
        _options = options;
        _model = model;
        _callGraph = new CallGraphBuilder(model);
    }

    public AnalyzeResult Analyze(INamedTypeSymbol symbol, StructDeclarationSyntax syntax)
    {
        var name = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // L0: already readonly
        if (syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                "already readonly struct (skipped)", name);

        // L0: ref struct
        if (syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                "ref struct (skipped)", name);

        // L0: partial struct
        if (syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                "partial struct (skipped)", name);

        // L0: opt-out attribute
        if (HasOptOutAttribute(symbol))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                $"[{_options.OptOutAttributeName}] attribute (skipped)", name);

        // Gather members
        var fields = symbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared).ToList();
        var properties = symbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer).ToList();
        var methods = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic).ToList();

        // Detect mutating instance methods
        var mutatingMethods = new List<IMethodSymbol>();
        foreach (var method in methods)
        {
            var methodSyntax = method.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (methodSyntax != null && IsMutating(methodSyntax))
                mutatingMethods.Add(method);
        }

        // Detect public/internal setters that mutate
        var mutableProperties = properties
            .Where(p => p.SetMethod != null &&
                        p.SetMethod.DeclaredAccessibility != Accessibility.Private &&
                        !p.SetMethod.IsInitOnly).ToList();

        // === Classification logic ===
        // Key insight: mutable properties (public setters) are handled by L3 (data container),
        // NOT by L5 (method migration). Only mutating METHODS trigger L5/L7.

        if (mutatingMethods.Count == 0)
        {
            // No mutating methods — eligible for L1/L2/L3

            // Pattern C: properties with private setters only (no public setters)
            if (mutableProperties.Count == 0 &&
                properties.Any(p => p.SetMethod?.DeclaredAccessibility == Accessibility.Private))
                return new(ConversionLevel.PropertyConvert, StructPattern.PrivateSetter, true,
                    "all setters are private", name);

            // Pattern B: all fields already readonly, no mutable properties
            if (mutableProperties.Count == 0 && fields.Count > 0 && fields.All(f => f.IsReadOnly))
                return new(ConversionLevel.DirectAdd, StructPattern.AlreadyReadonly, true,
                    "all fields already readonly", name);

            // Pattern A: all fields only assigned in constructor, no mutable properties
            if (mutableProperties.Count == 0 && fields.Count > 0 && AllFieldsOnlyAssignedInCtor(fields, syntax))
                return new(ConversionLevel.DirectAdd, StructPattern.FullImmutable, true,
                    "all fields only assigned in constructor", name);

            // Empty struct or get-only properties only → direct add
            if (mutableProperties.Count == 0 && fields.Count == 0 &&
                properties.All(p => p.SetMethod == null))
                return new(ConversionLevel.DirectAdd, StructPattern.FullImmutable, true,
                    "no mutable state", name);

            // Pattern D: data container (has public setters or mutable non-public fields)
            if (_options.EnableDtoConversion)
                return new(ConversionLevel.DataContainer, StructPattern.DataContainer, true,
                    "data container with public setters", name);

            // DTO disabled and has mutable properties → not convertible
            if (mutableProperties.Count > 0)
                return new(ConversionLevel.NotConvertible, StructPattern.DataContainer, false,
                    "data container (DTO conversion disabled)", name);
        }

        // Has mutating methods → L5 or L7
        {
            if (!_options.EnableMethodMigration)
                return new(ConversionLevel.NotConvertible, StructPattern.MutableMethodsNonMigratable, false,
                    "has mutating methods (method migration disabled)", name);

            // Check for non-migratable conditions
            var nonMigratableReason = CheckNonMigratable(symbol, syntax, fields);
            if (nonMigratableReason != null)
                return new(ConversionLevel.NotConvertible, StructPattern.MutableMethodsNonMigratable, false,
                    nonMigratableReason, name);

            // Build method migration infos
            var migrations = new List<MethodMigrationInfo>();
            foreach (var method in mutatingMethods)
            {
                var methodSyntax = method.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (methodSyntax == null) continue;

                var migrationType = ClassifyMigrationType(method, methodSyntax);
                migrations.Add(new MethodMigrationInfo(method, migrationType, methodSyntax));
            }

            return new(ConversionLevel.MethodMigrate, StructPattern.MutableMethods, true,
                $"has {migrations.Count} migrating methods", name, migrations);
        }
    }

    private bool HasOptOutAttribute(INamedTypeSymbol symbol)
    {
        return symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == _options.OptOutAttributeName ||
            a.AttributeClass?.Name == _options.OptOutAttributeName + "Attribute");
    }

    private bool IsMutating(MethodDeclarationSyntax method)
    {
        // A method is mutating if it assigns to instance fields/properties
        var hasAssignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
        {
            var targetSymbol = _model.GetSymbolInfo(a.Left).Symbol;
            return targetSymbol is IFieldSymbol { IsStatic: false } or
                   IPropertySymbol { IsStatic: false, SetMethod: not null };
        });

        var hasIncrementDecrement = method.DescendantNodes()
            .OfType<PostfixUnaryExpressionSyntax>()
            .Concat(method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                .Select(p => (PostfixUnaryExpressionSyntax?)null!)
                .Where(_ => false)) // placeholder - handle both types below
            .Any();

        // Check prefix/postfix ++/-- on instance fields
        var hasUnaryMutation = method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Any(u =>
        {
            var s = _model.GetSymbolInfo(u.Operand).Symbol;
            return s is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false };
        }) || method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().Any(u =>
        {
            if (u.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
                return false;
            var s = _model.GetSymbolInfo(u.Operand).Symbol;
            return s is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false };
        });

        // Compound assignment (+=, -=, etc.)
        var hasCompoundAssignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
        {
            if (a.Kind() == SyntaxKind.SimpleAssignmentExpression) return false;
            var targetSymbol = _model.GetSymbolInfo(a.Left).Symbol;
            return targetSymbol is IFieldSymbol { IsStatic: false } or
                   IPropertySymbol { IsStatic: false };
        });

        return hasAssignment || hasUnaryMutation || hasCompoundAssignment;
    }

    private static bool AllFieldsOnlyAssignedInCtor(IReadOnlyList<IFieldSymbol> fields, StructDeclarationSyntax syntax)
    {
        if (fields.Count == 0) return true;

        // Must have at least one constructor that assigns fields
        var ctors = syntax.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (ctors.Count == 0) return false;

        // Verify no assignments to fields outside constructors
        var nonCtorMethods = syntax.Members.OfType<MethodDeclarationSyntax>().ToList();
        var propertySetters = syntax.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
            .ToList();

        foreach (var method in nonCtorMethods)
        {
            var assignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Concat(method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                    .Select(u => (AssignmentExpressionSyntax?)null!)
                    .Where(_ => false))
                .ToList();

            if (assignments.Any(a => IsFieldAccess(a.Left, fields)))
                return false;

            // Check ++/-- on fields
            if (method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                .Any(u => IsFieldAccess(u.Operand, fields)))
                return false;
            if (method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                .Any(u => IsFieldAccess(u.Operand, fields)))
                return false;
        }

        // Check property setters that assign to fields
        foreach (var prop in propertySetters)
        {
            var setter = prop.AccessorList!.Accessors.First(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            if (setter.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => IsFieldAccess(a.Left, fields)))
                return false;
        }

        return true;
    }

    private static bool IsFieldAccess(ExpressionSyntax expr, IReadOnlyList<IFieldSymbol> fields)
    {
        var name = expr switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            _ => null
        };
        return name != null && fields.Any(f => f.Name == name);
    }

    private string? CheckNonMigratable(INamedTypeSymbol symbol, StructDeclarationSyntax syntax,
        IReadOnlyList<IFieldSymbol> fields)
    {
        // Has array fields → heavy mutable state
        if (fields.Any(f => f.Type is IArrayTypeSymbol))
            return "contains array field (heavy mutable state)";

        // Has public fields (externally assignable)
        if (fields.Any(f => f.DeclaredAccessibility == Accessibility.Public))
            return "has public fields (externally assignable)";

        // Has virtual/override methods
        var methods = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic);
        if (methods.Any(m => m.IsVirtual || m.IsOverride || m.IsAbstract))
            return "has virtual/override methods";

        // Fields modified through ref/out parameter
        var root = syntax.SyntaxTree.GetRoot();
        if (_callGraph.IsModifiedThroughRef(symbol, root))
            return "fields mutated through ref parameter";

        // Public fields assigned externally
        if (_callGraph.HasExternalPublicFieldAssignment(symbol, root))
            return "public fields assigned externally";

        // Implements interface with mutating methods
        if (symbol.AllInterfaces.Any())
        {
            foreach (var iface in symbol.AllInterfaces)
            {
                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    var impl = symbol.FindImplementationForInterfaceMember(member);
                    if (impl is IMethodSymbol implMethod)
                    {
                        var implSyntax = implMethod.DeclaringSyntaxReferences
                            .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();
                        if (implSyntax != null && IsMutating(implSyntax))
                            return "implements interface with mutating method";
                    }
                }
            }
        }

        return null;
    }

    private static MigrationType ClassifyMigrationType(IMethodSymbol method, MethodDeclarationSyntax syntax)
    {
        if (method.ReturnsVoid)
            return MigrationType.VoidToStruct;

        // Check if method returns 'this'
        var returnsThis = syntax.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression is ThisExpressionSyntax);
        if (returnsThis)
            return MigrationType.ThisToStruct;

        return MigrationType.OtherReturnToOut;
    }
}
