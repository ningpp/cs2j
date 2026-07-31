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
            // No mutating methods — eligible for L1/L2/L3/L4

            // Pattern E: public fields with external assignments → L4 PublicFieldToProperty
            if (fields.Any(f => f.DeclaredAccessibility == Accessibility.Public))
            {
                var root = syntax.SyntaxTree.GetRoot();
                if (_callGraph.HasExternalPublicFieldAssignment(symbol, root))
                {
                    if (_options.EnablePublicFieldConversion)
                        return new(ConversionLevel.PublicFieldToProperty, StructPattern.PublicFields, true,
                            "public fields assigned externally - converting to properties with WithXxx methods", name);
                    return new(ConversionLevel.NotConvertible, StructPattern.PublicFields, false,
                        "public fields assigned externally (public field conversion disabled)", name);
                }
            }

            // Guard: non-private fields assigned externally cannot be made readonly (for other patterns)
            if (fields.Any(f => f.DeclaredAccessibility != Accessibility.Private &&
                                f.DeclaredAccessibility != Accessibility.NotApplicable &&
                                f.DeclaredAccessibility != Accessibility.Public))
            {
                var root = syntax.SyntaxTree.GetRoot();
                if (_callGraph.HasExternalPublicFieldAssignment(symbol, root))
                    return new(ConversionLevel.NotConvertible, StructPattern.DataContainer, false,
                        "non-private fields assigned externally", name);
            }

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
            // Guard: if any field is assigned multiple times in a constructor, the struct
            // cannot be made readonly at all (Java final fields allow only one assignment).
            if (HasMultipleFieldAssignmentsInCtor(fields, syntax))
                return new(ConversionLevel.NotConvertible, StructPattern.DataContainer, false,
                    "field assigned multiple times in constructor", name);

            if (_options.EnableDtoConversion)
            {
                return new(ConversionLevel.DataContainer, StructPattern.DataContainer, true,
                    "data container with public setters", name);
            }

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
        // Get the containing type so we only detect mutations to THIS struct's fields,
        // not to local variables' fields (e.g. localPoint.X += 1 is NOT mutating).
        var methodSymbol = _model.GetDeclaredSymbol(method);
        var containingType = methodSymbol?.ContainingType;
        if (containingType == null) return false;

        // A method is mutating if it assigns to instance fields/properties of the containing type
        var hasAssignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
        {
            var targetSymbol = _model.GetSymbolInfo(a.Left).Symbol;
            if (targetSymbol is IFieldSymbol { IsStatic: false } f)
                return SymbolEqualityComparer.Default.Equals(f.ContainingType, containingType);
            if (targetSymbol is IPropertySymbol { IsStatic: false, SetMethod: not null } p)
                return SymbolEqualityComparer.Default.Equals(p.ContainingType, containingType);
            return false;
        });

        // Check prefix/postfix ++/-- on instance fields of the containing type
        var hasUnaryMutation = method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Any(u =>
        {
            var s = _model.GetSymbolInfo(u.Operand).Symbol;
            if (s is IFieldSymbol { IsStatic: false } f)
                return SymbolEqualityComparer.Default.Equals(f.ContainingType, containingType);
            if (s is IPropertySymbol { IsStatic: false } p)
                return SymbolEqualityComparer.Default.Equals(p.ContainingType, containingType);
            return false;
        }) || method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().Any(u =>
        {
            if (u.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
                return false;
            var s = _model.GetSymbolInfo(u.Operand).Symbol;
            if (s is IFieldSymbol { IsStatic: false } f)
                return SymbolEqualityComparer.Default.Equals(f.ContainingType, containingType);
            if (s is IPropertySymbol { IsStatic: false } p)
                return SymbolEqualityComparer.Default.Equals(p.ContainingType, containingType);
            return false;
        });

        // Compound assignment (+=, -=, etc.) to fields of the containing type
        var hasCompoundAssignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
        {
            if (a.Kind() == SyntaxKind.SimpleAssignmentExpression) return false;
            var targetSymbol = _model.GetSymbolInfo(a.Left).Symbol;
            if (targetSymbol is IFieldSymbol { IsStatic: false } f)
                return SymbolEqualityComparer.Default.Equals(f.ContainingType, containingType);
            if (targetSymbol is IPropertySymbol { IsStatic: false } p)
                return SymbolEqualityComparer.Default.Equals(p.ContainingType, containingType);
            return false;
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

        // Java final fields can only be assigned once per constructor path.
        // If any field is assigned more than once in any constructor, the struct
        // cannot be made readonly (would produce illegal Java code).
        foreach (var ctor in ctors)
        {
            var assignedFieldNames = new HashSet<string>();
            foreach (var assignment in ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var name = assignment.Left switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => null
                };
                if (name == null) continue;
                if (!fields.Any(f => f.Name == name)) continue;
                if (!assignedFieldNames.Add(name))
                    return false; // multiple assignments to same field
            }

            // Also count ++/-- as assignments
            foreach (var unary in ctor.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                .Select(u => u.Operand)
                .Concat(ctor.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                    .Select(u => u.Operand)))
            {
                var name = unary switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => null
                };
                if (name == null) continue;
                if (!fields.Any(f => f.Name == name)) continue;
                if (!assignedFieldNames.Add(name))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if any field is assigned more than once in any constructor.
    /// Such structs cannot be made readonly because Java final fields allow only one assignment.
    /// </summary>
    private static bool HasMultipleFieldAssignmentsInCtor(IReadOnlyList<IFieldSymbol> fields, StructDeclarationSyntax syntax)
    {
        if (fields.Count == 0) return false;

        var ctors = syntax.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        foreach (var ctor in ctors)
        {
            var assignedFieldNames = new HashSet<string>();
            foreach (var assignment in ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var name = assignment.Left switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => null
                };
                if (name == null) continue;
                if (!fields.Any(f => f.Name == name)) continue;
                if (!assignedFieldNames.Add(name))
                    return true;
            }

            foreach (var operand in ctor.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                .Select(u => u.Operand)
                .Concat(ctor.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                    .Select(u => u.Operand)))
            {
                var name = operand switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => null
                };
                if (name == null) continue;
                if (!fields.Any(f => f.Name == name)) continue;
                if (!assignedFieldNames.Add(name))
                    return true;
            }
        }

        return false;
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
