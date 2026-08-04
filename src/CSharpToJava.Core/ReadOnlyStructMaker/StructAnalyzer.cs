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
    IReadOnlyList<MethodMigrationInfo>? MethodMigrations = null,
    IReadOnlyList<InterfaceMigrationInfo>? InterfaceMigrations = null,
    bool HasNonPrivateFields = false,
    bool NeedsWithMethods = false);

internal sealed record MethodMigrationInfo(
    IMethodSymbol Method,
    MigrationType Type,
    MethodDeclarationSyntax Syntax);

/// <summary>
/// Records a mutating method that implicitly implements an interface member.
/// After migration the signature changes, so an explicit interface implementation
/// delegating to the migrated method must be generated to keep the contract.
/// </summary>
internal sealed record InterfaceMigrationInfo(
    IMethodSymbol Method,
    IMethodSymbol InterfaceMember,
    MigrationType Type);

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
        var result = AnalyzeCore(symbol, syntax);
        // Structs whose fields are passed as ref/out arguments (e.g. mult(ref t, ref p0.aRot))
        // need ctor-based WithXxx methods once readonly so the hoisted temps can be
        // written back (CS0192 handling in the project preprocessor).
        if (result.ShouldRewrite && result.Level != ConversionLevel.MethodMigrate &&
            HasSelfRefFieldAccess(symbol, syntax))
        {
            result = result with { NeedsWithMethods = true };
        }
        return result;
    }

    /// <summary>
    /// Detects invocations inside the struct declaration that pass one of the struct's
    /// own fields as a ref/out argument (receiver resolves to this struct type, or `this`).
    /// </summary>
    private bool HasSelfRefFieldAccess(INamedTypeSymbol symbol, StructDeclarationSyntax syntax)
    {
        var fieldNames = symbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (fieldNames.Count == 0) return false;

        foreach (var argument in syntax.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                continue;
            if (argument.Expression is not MemberAccessExpressionSyntax ma ||
                ma.Name is not IdentifierNameSyntax fieldNameId)
                continue;
            if (!fieldNames.Contains(fieldNameId.Identifier.Text))
                continue;
            if (ma.Expression is ThisExpressionSyntax)
                return true;
            try
            {
                var type = _model.GetTypeInfo(ma.Expression).Type;
                if (type != null &&
                    SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, symbol.OriginalDefinition))
                    return true;
            }
            catch (ArgumentException)
            {
            }
        }
        return false;
    }

    private AnalyzeResult AnalyzeCore(INamedTypeSymbol symbol, StructDeclarationSyntax syntax)
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
        var methodSyntaxMap = new Dictionary<IMethodSymbol, MethodDeclarationSyntax>(SymbolEqualityComparer.Default);
        foreach (var method in methods)
        {
            var methodSyntax = method.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (methodSyntax == null) continue;
            methodSyntaxMap[method] = methodSyntax;
            if (IsMutating(methodSyntax))
                mutatingMethods.Add(method);
        }

        // Transitive detection: a void method that only delegates to mutating methods
        // (e.g. Rectangle.Add(Rectangle) calling Add(Point) twice) is also mutating and
        // must be migrated, otherwise its callers cannot capture the updated value.
        // Return-this delegating methods (e.g. Rectangle.Pad calling PadWidth/PadHeight)
        // qualify too: after migration the delegatees return new values, and leaving
        // the delegator untouched silently discards those results.
        bool changed = true;
        while (changed)
        {
            changed = false;
            var mutatingNames = mutatingMethods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var method in methods)
            {
                if (mutatingMethods.Contains(method)) continue;
                if (!methodSyntaxMap.TryGetValue(method, out var methodDecl)) continue;
                if (!IsTransitiveMigrationCandidate(method, methodDecl))
                    continue;
                if (CallsMutatingMethod(methodDecl, mutatingNames, method))
                {
                    mutatingMethods.Add(method);
                    changed = true;
                }
            }
        }

        // Detect public/internal setters that mutate
        var mutableProperties = properties
            .Where(p => p.SetMethod != null &&
                        p.SetMethod.DeclaredAccessibility != Accessibility.Private &&
                        !p.SetMethod.IsInitOnly).ToList();

        // === Classification logic ===
        // Key insight: mutable properties (public setters) are handled by L3 (data container),
        // NOT by L5 (method migration). Only mutating METHODS trigger L5/L7.

        // Fields accessible from outside (public/internal/protected). These are used
        // both for L4 classification and to flag L5 structs that need field conversion.
        var nonPrivateFields = fields
            .Where(f => f.DeclaredAccessibility != Accessibility.Private &&
                        f.DeclaredAccessibility != Accessibility.NotApplicable)
            .ToList();

        if (mutatingMethods.Count == 0)
        {
            // No mutating methods — eligible for L1/L2/L3/L4

            // Pattern E: non-private fields (public/internal/protected) with external
            // assignments → L4 PublicFieldToProperty. Fields become get-only properties
            // and external writes are rewritten to WithXxx() calls.
            if (nonPrivateFields.Count > 0)
            {
                var root = syntax.SyntaxTree.GetRoot();
                if (_callGraph.HasExternalPublicFieldAssignment(symbol, root))
                {
                    if (_options.EnablePublicFieldConversion)
                        return new(ConversionLevel.PublicFieldToProperty, StructPattern.PublicFields, true,
                            "non-private fields assigned externally - converting to properties with WithXxx methods", name);
                    return new(ConversionLevel.NotConvertible, StructPattern.PublicFields, false,
                        "non-private fields assigned externally (public field conversion disabled)", name);
                }
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
            // Guard: constructors with multiple assignments to the same field cannot be
            // normalized for L3 (existing constructors are kept and would emit illegal
            // Java final-field double assignments).
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

            // Detect implicit interface implementations among migrated methods. After
            // migration their signatures change (void → struct return, or added out
            // parameter), so explicit interface implementations must be generated to
            // preserve the interface contract.
            var interfaceMigrations = new List<InterfaceMigrationInfo>();
            foreach (var migration in migrations)
            {
                if (migration.Type == MigrationType.ThisToStruct)
                    continue; // signature unchanged — no explicit impl needed

                foreach (var iface in symbol.AllInterfaces)
                {
                    foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                    {
                        var impl = symbol.FindImplementationForInterfaceMember(member);
                        if (SymbolEqualityComparer.Default.Equals(impl, migration.Method) &&
                            migration.Method.ExplicitInterfaceImplementations.Length == 0)
                        {
                            interfaceMigrations.Add(new InterfaceMigrationInfo(
                                migration.Method, member, migration.Type));
                        }
                    }
                }
            }

            return new(ConversionLevel.MethodMigrate, StructPattern.MutableMethods, true,
                $"has {migrations.Count} migrating methods", name, migrations,
                interfaceMigrations, nonPrivateFields.Count > 0);
        }
    }

    /// <summary>
    /// True for methods eligible to join the migration set transitively: void
    /// delegating methods and methods that return the containing struct via
    /// <c>return this;</c>. Both would silently discard the migrated delegatees'
    /// return values if left unmigrated. Methods with other return types are
    /// excluded (their migration would change signatures via out-parameters).
    /// </summary>
    private static bool IsTransitiveMigrationCandidate(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
    {
        if (methodDecl.ReturnType is PredefinedTypeSyntax pts &&
            pts.Keyword.IsKind(SyntaxKind.VoidKeyword))
            return true;

        if (method.ReturnsVoid)
            return false;

        var returnsThis =
            methodDecl.Body?.DescendantNodes().OfType<ReturnStatementSyntax>()
                .Any(r => r.Expression is ThisExpressionSyntax) == true ||
            methodDecl.ExpressionBody?.Expression is ThisExpressionSyntax;

        return returnsThis &&
               SymbolEqualityComparer.Default.Equals(method.ReturnType, method.ContainingType);
    }

    private bool HasOptOutAttribute(INamedTypeSymbol symbol)
    {
        return symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == _options.OptOutAttributeName ||
            a.AttributeClass?.Name == _options.OptOutAttributeName + "Attribute");
    }

    /// <summary>
    /// True when the method body contains a statement-level invocation of a known
    /// mutating method via implicit this (<c>Add(x);</c>) or explicit this (<c>this.Add(x);</c>).
    /// Truly recursive calls (resolving to the method itself) are ignored; calls to
    /// same-name overloads are delegations and count as mutations.
    /// </summary>
    private bool CallsMutatingMethod(MethodDeclarationSyntax method,
        HashSet<string> mutatingNames, IMethodSymbol ownMethod)
    {
        var ownName = ownMethod.Name;
        var ownArity = method.ParameterList.Parameters.Count;
        foreach (var statement in method.DescendantNodes().OfType<ExpressionStatementSyntax>())
        {
            if (statement.Expression is not InvocationExpressionSyntax invocation)
                continue;
            string? calledName = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.Text,
                _ => null
            };
            if (calledName == null || !mutatingNames.Contains(calledName))
                continue;

            // Same-name calls need overload disambiguation: semantic resolution tells
            // whether the call targets this method (recursion) or another overload
            // (delegation). Without a resolved symbol, fall back to arity comparison.
            if (calledName == ownName)
            {
                var resolved = _model.GetSymbolInfo(invocation).Symbol;
                if (resolved != null)
                {
                    if (SymbolEqualityComparer.Default.Equals(resolved.OriginalDefinition, ownMethod.OriginalDefinition))
                        continue; // true recursion
                }
                else if (invocation.ArgumentList.Arguments.Count == ownArity)
                {
                    continue; // unresolved, assume recursion when arities match
                }
            }

            return true;
        }
        return false;
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

        // Multiple assignments to the same field within a constructor are allowed:
        // C# readonly fields may be assigned repeatedly inside constructors, and the
        // ConstructorNormalizer rewrites such constructors to single-assignment form
        // (shadow locals) so the generated Java final fields remain legal.

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
        // NOTE: array fields are allowed — a readonly array field keeps the reference
        // immutable while element mutation remains legal (same as Java final arrays).
        // NOTE: public/internal fields are allowed — they are converted to get-only
        // properties with WithXxx methods during L5 rewriting.
        // NOTE: interfaces with mutating methods are allowed — explicit interface
        // implementations delegating to the migrated methods are generated.

        // Has virtual/override/abstract methods that are NOT merely overriding
        // System.Object members (ToString/Equals/GetHashCode). Overriding object
        // members is always safe for an immutable struct: those overrides are
        // pure (read-only) by nature, and Java's final class will simply inherit
        // the same overrides. Genuine virtual/abstract members break value-type
        // semantics (polymorphism, dynamic dispatch) and must stay non-migratable.
        var methods = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic);
        if (methods.Any(m => IsGenuinelyVirtualOrAbstract(m)))
            return "has virtual/override methods";

        // Fields modified through ref/out parameter
        var root = syntax.SyntaxTree.GetRoot();
        if (_callGraph.IsModifiedThroughRef(symbol, root))
            return "fields mutated through ref parameter";

        return null;
    }

    /// <summary>
    /// A method blocks migration only when it introduces genuine polymorphism
    /// (virtual/abstract/override of a non-object member). The following are
    /// excluded because they are safe for an immutable struct:
    /// - Interface implementations (both explicit and implicit): structs are
    ///   sealed, so interface dispatch does not introduce polymorphism.
    /// - Overrides of System.Object / System.ValueType members (ToString/Equals/
    ///   GetHashCode): pure read-only operations.
    /// Genuine virtual/abstract members break value-type semantics (polymorphism,
    /// dynamic dispatch) and must stay non-migratable.
    /// </summary>
    private static bool IsGenuinelyVirtualOrAbstract(IMethodSymbol method)
    {
        if (!method.IsVirtual && !method.IsOverride && !method.IsAbstract)
            return false;

        // Explicit interface implementations are safe — they don't introduce
        // polymorphism (structs are sealed) and are dispatched via the interface.
        if (method.ExplicitInterfaceImplementations.Any())
            return false;

        // Implicit interface implementations are also safe — check if this method
        // implements any interface member of the containing type.
        var containingType = method.ContainingType;
        if (containingType != null)
        {
            foreach (var iface in containingType.AllInterfaces)
            {
                foreach (var member in iface.GetMembers())
                {
                    var impl = containingType.FindImplementationForInterfaceMember(member);
                    if (SymbolEqualityComparer.Default.Equals(impl, method))
                        return false;
                }
            }
        }

        // Overriding a System.Object / System.ValueType member (ToString/Equals/
        // GetHashCode) is safe — those overrides are pure read-only operations.
        // A struct overrides these via System.ValueType, so both special types
        // must be accepted.
        if (method.IsOverride)
        {
            var overriddenType = method.OverriddenMethod?.ContainingType?.SpecialType;
            if (overriddenType == SpecialType.System_Object ||
                overriddenType == SpecialType.System_ValueType)
                return false;
        }

        return true;
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

    /// <summary>
    /// Checks if the field declaration has preprocessor directives in its descendant tokens' leading trivia.
    /// </summary>
    private static bool FieldHasPreprocessorDirective(FieldDeclarationSyntax field)
    {
        foreach (var token in field.DescendantTokens())
        {
            if (token.HasLeadingTrivia)
            {
                foreach (var t in token.LeadingTrivia)
                {
                    if (t.IsKind(SyntaxKind.IfDirectiveTrivia) ||
                        t.IsKind(SyntaxKind.ElseDirectiveTrivia) ||
                        t.IsKind(SyntaxKind.ElifDirectiveTrivia) ||
                        t.IsKind(SyntaxKind.EndIfDirectiveTrivia))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
