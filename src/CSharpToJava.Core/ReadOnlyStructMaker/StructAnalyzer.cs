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
    private readonly IReadOnlySet<INamedTypeSymbol> _boxingReachableStructs;
    private readonly CrossFileAccessIndex? _externalAccessIndex;

    public StructAnalyzer(
        ReadOnlyStructMakerOptions options,
        SemanticModel model,
        IReadOnlySet<INamedTypeSymbol>? boxingReachableStructs = null,
        CrossFileAccessIndex? externalAccessIndex = null)
    {
        _options = options;
        _model = model;
        _callGraph = new CallGraphBuilder(model, externalAccessIndex);
        _boxingReachableStructs = boxingReachableStructs
            ?? new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        _externalAccessIndex = externalAccessIndex;
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
    /// True when the struct type is passed by <c>ref</c>/<c>out</c>/<c>in</c> as a
    /// parameter of methods declared in OTHER types, and one of those methods
    /// mutates the parameter's members heavily (8+ writes). Every such write is
    /// rewritten into an <c>obj = obj.WithXxx(...)</c> chain whose temporaries
    /// bloat the JIT frames of hot recursive serializer methods several times over —
    /// deeply nested payloads (e.g. StreamTests.DeepNestedJsonFileTest, depth 324)
    /// then overflow the 1 MB test stack although the original code handles them
    /// fine. Lightly-mutated ref parameters (e.g. <c>void Update(ref Pixel p)
    /// { p.X++; }</c>) are unaffected and keep converting.
    /// </summary>
    private bool HasExternalRefParamPassing(INamedTypeSymbol structSymbol)
    {
        if (_externalAccessIndex != null)
            return _externalAccessIndex.HasExternalRefParamPassing(structSymbol);

        var compilation = _model.Compilation;
        var target = structSymbol.OriginalDefinition;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var method in tree.GetRoot().DescendantNodes()
                         .OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = model.GetDeclaredSymbol(method);
                if (methodSymbol == null) continue;
                if (SymbolEqualityComparer.Default.Equals(
                        methodSymbol.ContainingType?.OriginalDefinition, target))
                {
                    continue; // the struct's own methods
                }
                foreach (var parameter in method.ParameterList.Parameters)
                {
                    if (!parameter.Modifiers.Any(m =>
                            m.IsKind(SyntaxKind.RefKeyword) ||
                            m.IsKind(SyntaxKind.OutKeyword) ||
                            m.IsKind(SyntaxKind.InKeyword)))
                    {
                        continue;
                    }
                    try
                    {
                        var type = model.GetTypeInfo(parameter.Type).Type;
                        if (type == null ||
                            !SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, target))
                        {
                            continue;
                        }
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    // Count writes to the parameter's members: p.F = v / p.F++ /
                    // p.F op= v / p.F[..] = v — each becomes a With-chain.
                    var paramName = parameter.Identifier.Text;
                    int chainCount = 0;
                    foreach (var assign in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                    {
                        if (IsParamMemberWrite(assign.Left, paramName)) chainCount++;
                    }
                    foreach (var unary in method.DescendantNodes()
                                 .OfType<PrefixUnaryExpressionSyntax>()
                                 .Cast<ExpressionSyntax>()
                                 .Concat(method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()))
                    {
                        if (unary is PrefixUnaryExpressionSyntax pre && IsParamMemberWrite(pre.Operand, paramName) ||
                            unary is PostfixUnaryExpressionSyntax post && IsParamMemberWrite(post.Operand, paramName))
                        {
                            chainCount++;
                        }
                    }
                    if (chainCount >= 8)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static bool IsParamMemberWrite(ExpressionSyntax expr, string paramName)
    {
        while (expr is MemberAccessExpressionSyntax ma)
        {
            expr = ma.Expression;
        }
        return expr is IdentifierNameSyntax id && id.Identifier.Text == paramName;
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
        var isForced = _options.ForceReadOnlyQualifiedNames.Count > 0 &&
                       _options.ForceReadOnlyQualifiedNames.Contains(AssignmentRewriter.GetQualifiedTypeName(symbol));

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

        // L0: instance field-like events. The compiler-generated backing field
        // is mutable instance state, so readonly structs cannot contain them
        // (CS8342). Such structs are infeasible as-is (SV051 residual).
        if (syntax.Members.OfType<EventFieldDeclarationSyntax>()
            .Any(e => !e.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))))
            return new(ConversionLevel.Skip, StructPattern.MutableMethodsNonMigratable, false,
                "instance field-like event(s) (CS8342: not allowed in readonly struct, skipped)", name);

        // L0: opt-out attribute
        if (HasOptOutAttribute(symbol))
            return new(ConversionLevel.Skip, StructPattern.AlreadyReadonly, false,
                $"[{_options.OptOutAttributeName}] attribute (skipped)", name);

        // L0: structs passed by ref/out/in to methods of other types. External
        // callers' mutation rewrites (obj = obj.WithXxx(...)) bloat the JIT frames
        // of hot recursive methods, overflowing the test stack on deeply nested
        // payloads the original code handles fine. Keep them mutable.
        if (HasExternalRefParamPassing(symbol))
            return new(ConversionLevel.Skip, StructPattern.MutableMethodsNonMigratable, false,
                "passed by ref/out to external methods (frame-size preservation)", name);

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
            var methodSyntax = GetMethodSyntaxInTree(method, _model.SyntaxTree);
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

        // === Reflection-safety skip ===
        // Applies only to structs without migrating methods: those are classified as
        // L1-L4, where L3 (DTO) removes public setters and L4 removes public field
        // write access. If such a struct's state is reachable from an object-like
        // context (boxed directly, or nested inside a boxed object graph),
        // reflection-based consumers (serializers, mappers, source-generated
        // descriptors) may write its members at runtime; removing the write targets
        // breaks them, so the struct keeps its original mutable shape. Types
        // explicitly annotated for serialization/mapping are likewise skipped.
        // Structs with mutating methods follow the L5 method-migration path, whose
        // mutation is encapsulated in methods rather than reflectively-writable
        // members, so they are not subject to this skip.
        if (mutatingMethods.Count == 0 && !isForced)
        {
            var hasMutableState = mutableProperties.Count > 0 ||
                                  fields.Any(f => !f.IsReadOnly);
            if (hasMutableState)
            {
                if (_boxingReachableStructs.Contains(symbol.OriginalDefinition))
                {
                    return new(ConversionLevel.Skip, StructPattern.DataContainer, false,
                        "mutable state reachable through boxing (reflection mutation risk)", name);
                }

                if (HasSerializationAttribute(symbol))
                {
                    return new(ConversionLevel.Skip, StructPattern.DataContainer, false,
                        "serialization/mapping attribute present (reflection mutation risk)", name);
                }
            }
        }

        if (mutatingMethods.Count == 0)
        {
            // No mutating methods — eligible for L1/L2/L3/L4

            // Guard: methods that assign to `this` (e.g. `this = default`) cannot be
            // converted to readonly because `this` is immutable in a readonly struct.
            // This catches cases like Dispose() methods that reset the struct state.
            if (HasThisAssignment(syntax))
                return new(ConversionLevel.NotConvertible, StructPattern.MutableMethodsNonMigratable, false,
                    "method assigns to 'this'", name);

            // Guard: non-private fields passed as ref/out arguments from outside the
            // struct (e.g. MemoryMarshal.CreateSpan(ref s.Item1!, 4) over a raw memory
            // block struct) cannot become properties — properties are not ref-returning,
            // so L3/L4 field conversion would break the call sites with CS0206.
            if (nonPrivateFields.Count > 0 &&
                _callGraph.HasExternalRefFieldAccess(symbol, syntax.SyntaxTree.GetRoot()))
                return new(ConversionLevel.NotConvertible, StructPattern.PublicFields, false,
                    "non-private fields accessed by ref/out externally (CS0206 risk)", name);

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

            // Guard: non-private setters on properties whose getter derives state from
            // multiple storages (e.g. Indented => (_optionsMask & IndentBit) != 0). The
            // L3 conversion turns the property get-only, but the derived state cannot be
            // reconstructed from constructor parameters, so external writes to it would
            // break (CS0200). Direct backing-field getters (get => _field) are fine —
            // the backing storage has a constructor parameter.
            if (HasUnrecoverableSetterProps(syntax))
                return new(ConversionLevel.NotConvertible, StructPattern.DataContainer, false,
                    "property with custom derived getter and public setter (state not recoverable via constructor)", name);

            // Guard: setters whose bodies contain validation or other logic beyond a
            // plain `_field = value;` write (e.g. `if (value > Max) throw ...;`). The
            // L3 conversion removes setters and the generated constructor assigns raw
            // values — silently dropping the validation (e.g. JsonReaderOptions).
            if (HasValidationSetterProps(syntax))
                return new(ConversionLevel.NotConvertible, StructPattern.DataContainer, false,
                    "property setter contains validation logic (state not recoverable via constructor)", name);

            // Guard: [JsonConstructor]-attributed structs are deserialized by
            // System.Text.Json through the annotated constructor PLUS property-setter
            // assignment for the remaining members. The L3 conversion removes those
            // setters, breaking round-trips, so keep such structs mutable.
            if (symbol.InstanceConstructors.Any(c => c.GetAttributes().Any(a =>
                    a.AttributeClass?.Name is "JsonConstructorAttribute" or "JsonConstructor")))
                return new(ConversionLevel.NotConvertible, StructPattern.DataContainer, false,
                    "has [JsonConstructor] (System.Text.Json deserialization target)", name);

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
            var nonMigratableReason = CheckNonMigratable(symbol, syntax, fields, mutatingMethods, methodSyntaxMap);
            if (nonMigratableReason != null)
                return new(ConversionLevel.NotConvertible, StructPattern.MutableMethodsNonMigratable, false,
                    nonMigratableReason, name);

            // Build method migration infos
            var migrations = new List<MethodMigrationInfo>();
            foreach (var method in mutatingMethods)
            {
                var methodSyntax = GetMethodSyntaxInTree(method, _model.SyntaxTree);
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

            // A migrated method that implicitly implements an interface member is
            // invoked through interface dispatch on a boxed instance (e.g. foreach
            // calls IEnumerator.MoveNext(), tests call ITestClass.Initialize()).
            // The explicit implementation shim cannot write the migrated struct
            // result back through the box (this = ... is CS1604 in a readonly
            // struct), so the mutation is silently lost. Keep such structs mutable.
            if (interfaceMigrations.Count > 0)
            {
                return new(ConversionLevel.NotConvertible, StructPattern.MutableMethodsNonMigratable, false,
                    "mutating methods implement interface members (state cannot be written back through interface dispatch)", name);
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
    internal static bool IsTransitiveMigrationCandidate(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
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

    /// <summary>
    /// Returns the method declaration whose syntax lives in <paramref name="tree"/>.
    /// Partial types can merge declarations across files (e.g. a ref-assembly stub
    /// next to the real implementation); only the declaration in the analyzed tree
    /// is valid for this semantic model. Returns null when the method is declared
    /// exclusively in other trees (stub-only members are skipped).
    /// </summary>
    private static MethodDeclarationSyntax? GetMethodSyntaxInTree(IMethodSymbol method, SyntaxTree tree)
    {
        return method.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax()).OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(s => s.SyntaxTree == tree);
    }

    private bool HasOptOutAttribute(INamedTypeSymbol symbol)
    {
        return symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == _options.OptOutAttributeName ||
            a.AttributeClass?.Name == _options.OptOutAttributeName + "Attribute");
    }

    /// <summary>
    /// True when the type carries an attribute that marks it for reflective
    /// serialization/mapping processing. Such types have their members written by
    /// serializers at runtime, so converting them to readonly breaks deserialization.
    /// Covers [Serializable], [DataContract], XML/JSON serializers, and custom
    /// serializer-marker attributes such as YamlDotNet's [YamlSerializable].
    /// </summary>
    private static bool HasSerializationAttribute(INamedTypeSymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            var name = attribute.AttributeClass?.Name;
            if (name == null) continue;

            if (name.Contains("Serializ", StringComparison.OrdinalIgnoreCase))
                return true;

            switch (name)
            {
                case "DataContractAttribute":
                case "XmlTypeAttribute":
                case "XmlRootAttribute":
                case "JsonObjectAttribute":
                case "JsonClassAttribute":
                case "ProtoContractAttribute":
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the struct has a non-private setter on a property whose getter is a
    /// custom body deriving state from storage (not a direct backing-field return).
    /// Such properties cannot be reconstructed from constructor parameters after the
    /// L3 conversion removes their setters.
    /// </summary>
    private static bool HasUnrecoverableSetterProps(StructDeclarationSyntax syntax)
    {
        foreach (var prop in syntax.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (prop.AccessorList == null) continue;
            var setter = prop.AccessorList.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            if (setter == null) continue;
            if (setter.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) continue;
            var getter = prop.AccessorList.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            if (getter == null) continue;
            // Auto-property getter — the property itself is the storage: recoverable.
            if (getter.Body == null && getter.ExpressionBody == null) continue;

            // Custom getter: recoverable only when it directly returns a single
            // backing identifier (get => _field; / get { return _field; }).
            var returnExpr = getter.ExpressionBody?.Expression
                ?? getter.Body?.DescendantNodes().OfType<ReturnStatementSyntax>().SingleOrDefault()?.Expression;
            if (returnExpr is not IdentifierNameSyntax)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the struct has a non-private setter whose body is not a plain
    /// backing-field write (e.g. contains validation like <c>if (value &gt; Max)
    /// throw ...;</c> or <c>Debug.Assert(...)</c>). The L3 conversion removes
    /// setters and its generated constructor assigns raw values, silently dropping
    /// the validation — so such structs must not be converted.
    /// </summary>
    private static bool HasValidationSetterProps(StructDeclarationSyntax syntax)
    {
        foreach (var prop in syntax.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (prop.AccessorList == null) continue;
            var setter = prop.AccessorList.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            if (setter == null) continue;
            if (setter.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) continue;
            if (setter.Body == null && setter.ExpressionBody == null) continue; // auto-prop

            if (setter.Body == null) return true; // expression-bodied setter: not a plain write

            // Plain backing write: exactly one assignment of `value` to a field.
            var statements = setter.Body.Statements;
            if (statements.Count != 1) return true;
            if (statements[0] is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assign })
                return true;
            if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression)) return true;
            if (assign.Right is not IdentifierNameSyntax { Identifier.Text: "value" }) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the method body contains a statement-level invocation of a known
    /// mutating method via implicit this (<c>Add(x);</c>) or explicit this (<c>this.Add(x);</c>).
    /// Truly recursive calls (resolving to the method itself) are ignored; calls to
    /// same-name overloads are delegations and count as mutations.
    /// </summary>
    internal bool CallsMutatingMethod(MethodDeclarationSyntax method,
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

    internal bool IsMutating(MethodDeclarationSyntax method)
        => IsMutatingCore(method, new HashSet<ISymbol>(SymbolEqualityComparer.Default));

    private bool IsMutatingCore(MethodDeclarationSyntax method, HashSet<ISymbol> activeChain)
    {
        // Get the containing type so we only detect mutations to THIS struct's fields,
        // not to local variables' fields (e.g. localPoint.X += 1 is NOT mutating).
        var methodSymbol = _model.GetDeclaredSymbol(method);
        var containingType = methodSymbol?.ContainingType;
        if (containingType == null) return false;
        // 按调用路径派生的闭环集合：兄弟调用分支各自携带祖先链但互不污染，
        // 菱形调用图（A→B→D 与 A→C→D）里 D 仍能被每条路径独立判定。
        if (methodSymbol != null && activeChain.Contains(methodSymbol.OriginalDefinition))
            return false; // 递归调用闭环：不再沿此路径判定变异
        var chain = new HashSet<ISymbol>(activeChain, SymbolEqualityComparer.Default)
            { methodSymbol!.OriginalDefinition };

        // A method is mutating if it assigns to instance fields/properties of
        // the containing type THROUGH `this` (implicit or explicit). Writes
        // whose receiver is a local/parameter copy (the functional
        // `var copy = this; copy.X = v; return copy;` idiom) do not mutate the
        // receiver instance and must not force method migration.
        var hasAssignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
        {
            if (a.Kind() != SyntaxKind.SimpleAssignmentExpression) return false;
            if (!IsThisStateTarget(a.Left)) return false;
            var targetSymbol = _model.GetSymbolInfo(a.Left).Symbol;
            if (targetSymbol is IFieldSymbol { IsStatic: false } f)
                return SymbolEqualityComparer.Default.Equals(f.ContainingType, containingType);
            if (targetSymbol is IPropertySymbol { IsStatic: false, SetMethod: not null } p)
                return SymbolEqualityComparer.Default.Equals(p.ContainingType, containingType);
            return false;
        });

        // Check prefix/postfix ++/-- on instance fields of the containing type.
        // NOTE: the null-forgiving operator (x!) is also a PostfixUnaryExpressionSyntax
        // (SuppressNullableWarningExpression) but is NOT a mutation — only the
        // increment/decrement kinds qualify.
        var hasUnaryMutation = method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Any(u =>
        {
            if (u.Kind() is not (SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression))
                return false;
            if (!IsThisStateTarget(u.Operand)) return false;
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
            if (!IsThisStateTarget(u.Operand)) return false;
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
            if (!IsThisStateTarget(a.Left)) return false;
            var targetSymbol = _model.GetSymbolInfo(a.Left).Symbol;
            if (targetSymbol is IFieldSymbol { IsStatic: false } f)
                return SymbolEqualityComparer.Default.Equals(f.ContainingType, containingType);
            if (targetSymbol is IPropertySymbol { IsStatic: false } p)
                return SymbolEqualityComparer.Default.Equals(p.ContainingType, containingType);
            return false;
        });

        // ref/out argument targets a field/property of the containing type
        // (e.g. Array.Resize(ref _array, n)) — the callee mutates the field, so the
        // method mutates the struct state and must be migrated.
        var hasRefOutFieldMutation = method.DescendantNodes().OfType<ArgumentSyntax>().Any(a =>
        {
            if (!a.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                !a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                return false;
            if (!IsThisStateTarget(a.Expression)) return false;
            var targetSymbol = _model.GetSymbolInfo(a.Expression).Symbol;
            if (targetSymbol is IFieldSymbol { IsStatic: false } f)
                return SymbolEqualityComparer.Default.Equals(f.ContainingType, containingType);
            if (targetSymbol is IPropertySymbol { IsStatic: false } p)
                return SymbolEqualityComparer.Default.Equals(p.ContainingType, containingType);
            return false;
        });

        // 在 this 状态字段上调用非 readonly 的 struct 实例方法（如
        // `Start.Translate(dx, dy);`）：readonly 化后字段变 readonly，C# 会对接收者
        // 做防御性副本，变异静默丢失（readonly struct 直接改写语义）。被调方在同树
        // 有源码时递归判定其是否真的变异自身状态（Peek 等查询方法不算），跨树/外部
        // 不可见体保守判变异——该 struct 交给 C07 clone 兜底，不做 readonly 改写。
        var hasMutatingCallOnThisStateField =
            method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                IsMutatingCallOnThisState(invocation, methodSymbol!, chain));

        return hasAssignment || hasUnaryMutation || hasCompoundAssignment || hasRefOutFieldMutation
            || hasMutatingCallOnThisStateField;
    }

    private bool IsMutatingCallOnThisState(
        InvocationExpressionSyntax invocation, IMethodSymbol caller, HashSet<ISymbol> activeChain)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;
        if (!IsThisStateTarget(memberAccess.Expression))
            return false;

        var callee = _model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (callee is not { IsStatic: false, IsReadOnly: false }
            || callee.MethodKind != MethodKind.Ordinary)
            return false;
        if (callee.ContainingType?.TypeKind != TypeKind.Struct)
            return false;
        if (SymbolEqualityComparer.Default.Equals(
                callee.OriginalDefinition, caller.OriginalDefinition))
            return false; // 同方法递归，由 activeChain 之外的常规路径处理
        if (activeChain.Contains(callee.OriginalDefinition))
            return false;

        var calleeReference = callee.DeclaringSyntaxReferences
            .FirstOrDefault(r => r.SyntaxTree == invocation.SyntaxTree);
        if (calleeReference?.GetSyntax() is not MethodDeclarationSyntax calleeSyntax)
            return true; // 外部/跨树不可见体：保守判变异

        return IsMutatingCore(calleeSyntax, activeChain);
    }

    /// <summary>
    /// True when the assignment/argument expression denotes state reached
    /// through the current instance: an implicit-this identifier
    /// (<c>X = v</c>) or an explicit <c>this.X</c> chain. Writes through
    /// locals/parameters (<c>copy.X = v</c>) are not receiver-state mutations.
    /// </summary>
    private static bool IsThisStateTarget(ExpressionSyntax target)
    {
        // Implicit this (bare field/property identifier).
        if (target is IdentifierNameSyntax)
            return true;

        if (target is not MemberAccessExpressionSyntax memberAccess)
            return false;

        var receiver = memberAccess.Expression;
        while (true)
        {
            switch (receiver)
            {
                case ThisExpressionSyntax:
                    return true;
                case MemberAccessExpressionSyntax ma:
                    receiver = ma.Expression;
                    continue;
                case ElementAccessExpressionSyntax ea:
                    receiver = ea.Expression;
                    continue;
                case ParenthesizedExpressionSyntax pe:
                    receiver = pe.Expression;
                    continue;
                default:
                    // Local/parameter/other receiver — a copy, not this state.
                    return false;
            }
        }
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

            // Check ++/-- on fields (null-forgiving `x!` is also a postfix unary
            // node but is not a mutation)
            if (method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                .Where(u => u.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression)
                .Any(u => IsFieldAccess(u.Operand, fields)))
                return false;
            if (method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                .Where(u => u.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression)
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
                .Where(u => u.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression)
                .Select(u => u.Operand)
                .Concat(ctor.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                    .Where(u => u.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression)
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
        IReadOnlyList<IFieldSymbol> fields,
        List<IMethodSymbol>? mutatingMethods = null,
        Dictionary<IMethodSymbol, MethodDeclarationSyntax>? methodSyntaxMap = null)
    {
        // NOTE: array fields are allowed — a readonly array field keeps the reference
        // immutable while element mutation remains legal (same as Java final arrays).
        // NOTE: public/internal fields are allowed — they are converted to get-only
        // properties with WithXxx methods during L5 rewriting.
        // NOTE: interfaces with mutating methods are allowed — explicit interface
        // implementations delegating to the migrated methods are generated.

        // OtherReturnToOut migration returns the mutated state through an out
        // parameter that every call site must capture. When the struct's OWN members
        // consume those mutations internally — bare implicit-this calls (the out
        // status gets discarded) or own fields passed as ref/out arguments (the
        // ref-hoisting writes back after the whole statement, so reads in the same
        // expression still see the old value) — the state is silently lost. Keep
        // such structs mutable instead of generating broken code.
        if (mutatingMethods != null && mutatingMethods.Count > 0)
        {
            var otherReturnNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in mutatingMethods)
            {
                if (method.ReturnsVoid) continue;
                if (methodSyntaxMap != null && methodSyntaxMap.TryGetValue(method, out var decl) &&
                    decl.Body?.DescendantNodes().OfType<ReturnStatementSyntax>()
                        .Any(r => r.Expression is ThisExpressionSyntax) == true)
                    continue; // returns this — signature unchanged, safe
                otherReturnNames.Add(method.Name);
            }

            if (mutatingMethods.Count > 0)
            {
                var fieldNames = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var member in syntax.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;
                    foreach (var invocation in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        // Bare/this-receiver call to an other-return mutating method:
                        // the internal call site cannot capture the out status.
                        if (otherReturnNames.Count > 0)
                        {
                            switch (invocation.Expression)
                            {
                                case IdentifierNameSyntax id when otherReturnNames.Contains(id.Identifier.Text):
                                    return "other-return mutating methods called internally (mutation state cannot be preserved)";
                                case MemberAccessExpressionSyntax
                                {
                                    Expression: ThisExpressionSyntax
                                } ma when otherReturnNames.Contains(ma.Name.Identifier.Text):
                                    return "other-return mutating methods called internally (mutation state cannot be preserved)";
                            }
                        }

                        // Own fields passed as ref/out arguments inside a larger
                        // expression (not a statement-root call): the ref-hoisting
                        // write-back lands after the whole statement, too late for
                        // reads in the same expression.
                        foreach (var arg in invocation.ArgumentList.Arguments)
                        {
                            if (!arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                                !arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                                continue;
                            var argName = arg.Expression switch
                            {
                                IdentifierNameSyntax id => id.Identifier.Text,
                                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.Text,
                                _ => null
                            };
                            if (argName != null && fieldNames.Contains(argName) &&
                                invocation.Parent is not ExpressionStatementSyntax)
                            {
                                return "own fields passed as ref/out args inside expressions (mutation state cannot be preserved)";
                            }
                        }
                    }
                }
            }
        }

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

        // Methods that assign to `this` (e.g. `this = default` or `this = newStruct`)
        // cannot be migrated to readonly because `this` is immutable in a readonly struct.
        // Check override methods too — some override ToString/Equals and modify state.
        if (HasThisAssignment(syntax))
            return "method assigns to 'this'";

        // Mutating (non-readonly) struct instance method invoked on an own field:
        // `Start.Translate(dx, dy);`. The functional migration only carries direct
        // assignments/compounds on `copy` into the rebuilt struct — nested-field
        // method mutations have no write-back shape, so the migrated body would
        // silently drop them. Keep the struct mutable and let the C07 clone pass
        // preserve the semantics.
        foreach (var member in syntax.Members.OfType<MethodDeclarationSyntax>())
        {
            if (member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;
            foreach (var invocation in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;
                if (!IsThisStateTarget(memberAccess.Expression)) continue;
                if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee
                    || callee.IsStatic || callee.IsReadOnly)
                    continue;
                if (callee.ContainingType?.TypeKind != TypeKind.Struct) continue;
                return "mutating struct method invoked on an own field (nested field mutation cannot be migrated)";
            }
        }

        return null;
    }

    /// <summary>
    /// Detects methods that directly assign to `this` (e.g., `this = default;` or
    /// `this = new Value(...);`). Such patterns are illegal in a readonly struct
    /// because `this` is treated as a readonly variable.
    /// </summary>
    private static bool HasThisAssignment(StructDeclarationSyntax syntax)
    {
        foreach (var node in syntax.DescendantNodes())
        {
            if (node is not AssignmentExpressionSyntax assignment)
                continue;
            if (assignment.Left is ThisExpressionSyntax)
                return true;
        }
        return false;
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
