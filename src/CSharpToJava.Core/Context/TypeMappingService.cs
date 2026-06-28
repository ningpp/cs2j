using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Provides all type mapping logic: C# types → Java types, namespace → package,
/// import tracking, and caches. Extracted from ConversionContext.
/// </summary>
public class TypeMappingService
{
    private enum TypeReferenceContext
    {
        Default,
        DeclarationHeader
    }

    private readonly ConversionOptions _options;
    private readonly TypeMapping.TypeMappingRegistry _typeMappings;
    private readonly DiagnosticCollector _diagnostics;
    private readonly HashSet<string> _importedTypes;
    private readonly Func<string> _getCurrentNamespace;
    private readonly Func<INamespaceSymbol?> _getGlobalNamespace;
    private readonly Func<string, bool> _tryGetSynthesizedRecordMatch;
    private readonly Func<INamedTypeSymbol, string?> _getAssemblyScopedTypeName;
    private readonly Func<INamedTypeSymbol?> _getCurrentEnclosingType;

    /// <summary>
    /// Per-file type symbol → Java type cache. Cleared per file via ClearCache().
    /// </summary>
    public Dictionary<ITypeSymbol, TypeMappingCacheEntry> TypeCache { get; } = new();

    public sealed record TypeMappingCacheEntry(string JavaType, IReadOnlyList<string> Imports);

    /// <summary>
    /// [Flags] enum names mapped to int/long in Java. Scoped to this conversion context.
    /// </summary>
    private readonly HashSet<string> _flagsEnumNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _flagsEnumValueTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Enum names that have explicit integer values (need getValue()/fromValue() instead of ordinal()/values()[]).
    /// Scoped to this conversion context.
    /// </summary>
    private readonly HashSet<string> _explicitValueEnumNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _explicitValueEnumValueTypes = new(StringComparer.Ordinal);

    private readonly Func<string, ITypeSymbol?> _resolveAlias;

    public TypeMappingService(
        ConversionOptions options,
        TypeMapping.TypeMappingRegistry typeMappings,
        DiagnosticCollector diagnostics,
        HashSet<string> importedTypes,
        Func<string> getCurrentNamespace,
        Func<INamespaceSymbol?> getGlobalNamespace,
        Func<string, bool> tryGetSynthesizedRecordMatch,
        Func<INamedTypeSymbol, string?>? getAssemblyScopedTypeName = null,
        Func<string, ITypeSymbol?>? resolveAlias = null,
        Func<INamedTypeSymbol?>? getCurrentEnclosingType = null)
    {
        _options = options;
        _typeMappings = typeMappings;
        _diagnostics = diagnostics;
        _importedTypes = importedTypes;
        _resolveAlias = resolveAlias ?? (_ => null);
        _getCurrentNamespace = getCurrentNamespace;
        _getGlobalNamespace = getGlobalNamespace;
        _tryGetSynthesizedRecordMatch = tryGetSynthesizedRecordMatch;
        _getAssemblyScopedTypeName = getAssemblyScopedTypeName ?? (_ => null);
        _getCurrentEnclosingType = getCurrentEnclosingType ?? (() => null);
    }

    public void RegisterFlagsEnum(string enumName) => RegisterFlagsEnum(enumName, "int");
    public void RegisterFlagsEnum(string enumName, string valueType)
    {
        _flagsEnumNames.Add(enumName);
        _flagsEnumValueTypes[enumName] = NormalizeEnumValueType(valueType);
    }
    public bool IsFlagsEnum(string enumName) => _flagsEnumNames.Contains(enumName);
    public string GetFlagsEnumValueType(string enumName)
        => _flagsEnumValueTypes.TryGetValue(enumName, out var valueType) ? valueType : "int";

    public void RegisterExplicitValueEnum(string enumName) => RegisterExplicitValueEnum(enumName, "int");
    public void RegisterExplicitValueEnum(string enumName, string valueType)
    {
        _explicitValueEnumNames.Add(enumName);
        _explicitValueEnumValueTypes[enumName] = NormalizeEnumValueType(valueType);
    }
    public bool IsExplicitValueEnum(string enumName) => _explicitValueEnumNames.Contains(enumName);
    public string GetExplicitValueEnumValueType(string enumName)
        => _explicitValueEnumValueTypes.TryGetValue(enumName, out var valueType) ? valueType : "int";

    private static string NormalizeEnumValueType(string valueType)
        => valueType == "long" ? "long" : "int";

    /// <summary>
    /// Clear per-file caches (TypeCache). Called before each file conversion.
    /// </summary>
    public void ClearCache()
    {
        TypeCache.Clear();
    }

    // ─── Public mapping methods ───

    public string MapType(ITypeSymbol typeSymbol)
    {
        if (TypeCache.TryGetValue(typeSymbol, out var cached))
        {
            foreach (var import in cached.Imports)
                AddImport(import);
            return cached.JavaType;
        }

        var importsBefore = _importedTypes.ToHashSet(StringComparer.Ordinal);
        var result = MapTypeInternal(typeSymbol, TypeReferenceContext.Default);
        var addedImports = _importedTypes
            .Where(import => !importsBefore.Contains(import))
            .ToArray();
        TypeCache[typeSymbol] = new TypeMappingCacheEntry(result, addedImports);
        return result;
    }

    public string MapTypeForDeclarationHeader(ITypeSymbol typeSymbol)
    {
        return MapTypeInternal(typeSymbol, TypeReferenceContext.DeclarationHeader);
    }

    public string MapTypeFromSyntax(TypeSyntax typeSyntax)
    {
        if (typeSyntax == null) return "Object";

        // Handle C# pointer types: byte*, char*, int* → MemorySegment
        if (typeSyntax is PointerTypeSyntax)
        {
            AddImport("java.lang.foreign.MemorySegment");
            return "MemorySegment";
        }

        // Handle C# tuple types: (int, string) → Tuple2<Integer, String>
        if (typeSyntax is TupleTypeSyntax tupleType)
        {
            var arity = tupleType.Elements.Count;
            var mappedElements = tupleType.Elements
                .Select(e => BoxPrimitive(MapTypeFromSyntax(e.Type)))
                .ToList();
            var typeArgs = string.Join(", ", mappedElements);
            AddImport($"io.vavr.Tuple{arity}");
            return $"Tuple{arity}<{typeArgs}>";
        }

        return MapTypeFromSyntaxString(typeSyntax.ToString().Trim());
    }

    public string NamespaceToPackage(string ns)
    {
        var normalizedNs = (string.IsNullOrWhiteSpace(ns) || ns == "<global namespace>") ? "" : ns;

        var mapped = _typeMappings.MapNamespace(normalizedNs);
        if (mapped != null)
            return mapped;

        if (string.IsNullOrEmpty(normalizedNs))
            return string.Empty;

        foreach (var (pattern, replacement) in _options.NamespaceMappings)
        {
            if (normalizedNs.StartsWith(pattern))
                return normalizedNs.Replace(pattern, replacement);
        }

        return normalizedNs;
    }

    // ─── Internal mapping implementation ───

    private string MapTypeInternal(ITypeSymbol typeSymbol, TypeReferenceContext referenceContext)
    {
        // Check using alias registry first: if the type name matches a registered
        // alias, resolve via the alias target type (handles project-pipeline aliases).
        // Guard: skip if alias target has the same name (prevents infinite recursion).
        if (_resolveAlias(typeSymbol.Name) is ITypeSymbol aliasTarget
            && aliasTarget.Name != typeSymbol.Name)
            return MapTypeInternal(aliasTarget, referenceContext);

        if (typeSymbol is IPointerTypeSymbol)
        {
            AddImport("java.lang.foreign.MemorySegment");
            return "MemorySegment";
        }

        // LINQ extension method type parameters (TSource, TKey, etc.) can leak
        // into Java when the semantic model cannot fully resolve generics. Map
        // them to Object so they don't produce undeclared-type errors.
        if (typeSymbol is ITypeParameterSymbol tp
            && tp.DeclaringMethod != null
            && tp.DeclaringMethod.ContainingType?.ToDisplayString() == "System.Linq.Enumerable")
            return "Object";

        // IErrorTypeSymbol: unresolved type — try candidate symbols first for
        // better resolution, then fall back to qualified/short name from syntax.
        if (typeSymbol is IErrorTypeSymbol errorType)
        {
            // If Roslyn found exactly one candidate type, use it for accurate mapping.
            if (errorType.CandidateSymbols.Length == 1
                && errorType.CandidateSymbols[0] is ITypeSymbol candidateType
                && candidateType is not IErrorTypeSymbol)
            {
                _diagnostics.Warning(
                    $"Type '{errorType.ToDisplayString()}' resolved via single candidate symbol (reason: {errorType.CandidateReason})",
                    code: "CS2J1001",
                    category: "TypeResolution");
                return MapTypeInternal(candidateType, referenceContext);
            }

            var errorName = errorType.Name;

            // If the error type has type arguments, try the string-based fallback
            // which correctly handles generics, boxing, and simple-name fuzzy lookup.
            var displayString = errorType.ToDisplayString();
            if (displayString.Contains('<') && displayString.EndsWith(">"))
            {
                var stringMapped = MapTypeFromSyntaxString(displayString);
                if (stringMapped != errorName && stringMapped != displayString)
                    return stringMapped;
            }

            // Try via simple-name index with arity when the error type is a named type
            // with type arguments (e.g. IEnumerable<Shape> → lookup "IEnumerable`1").
            if (errorType is INamedTypeSymbol namedError && namedError.TypeArguments.Length > 0)
            {
                var arity = namedError.TypeArguments.Length;
                var fuzzyMapped = _typeMappings.MapTypeBySimpleName(errorName, arity);
                if (fuzzyMapped != errorName)
                {
                    var configKey = _typeMappings.FindConfigKeyBySimpleName(errorName, arity);
                    if (configKey != null) AddImportsForType(configKey);
                    var typeArgs = string.Join(", ",
                        namedError.TypeArguments.Select(t => MapTypeForGeneric(t, referenceContext)));
                    return $"{MapSimpleTypeName(fuzzyMapped)}<{typeArgs}>";
                }
            }

            // Try simple-name fuzzy lookup without arity
            var simpleMapped = _typeMappings.MapTypeBySimpleName(errorName);
            var simpleConfigKey = _typeMappings.FindConfigKeyBySimpleName(errorName);
            if (simpleConfigKey != null)
            {
                AddImportsForType(simpleConfigKey);
                return MapSimpleTypeName(simpleMapped);
            }
            if (simpleMapped != errorName)
                return MapSimpleTypeName(simpleMapped);

            // Try to use a namespace-qualified name to avoid cross-namespace collisions.
            var errorNs = errorType.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrEmpty(errorNs) && errorNs != "<global namespace>"
                && !string.IsNullOrEmpty(errorName) && errorName != "?" && errorName != "var")
            {
                var qualifiedName = $"{errorNs}.{errorName}";
                var qualifiedMapped = _typeMappings.MapType(qualifiedName);
                if (qualifiedMapped != qualifiedName)
                {
                    _diagnostics.Warning(
                        $"Unresolved type '{qualifiedName}' mapped via qualified name lookup",
                        code: "CS2J1001",
                        category: "TypeResolution");
                    AddImportsForType(qualifiedName);
                    return MapSimpleTypeName(qualifiedMapped);
                }
            }

            if (!string.IsNullOrEmpty(errorName) && errorName != "?" && errorName != "var")
            {
                // Try short-name lookup in TypeMappings.json before falling back to the raw name.
                var shortMapped = _typeMappings.MapType(errorName);
                if (shortMapped != errorName)
                {
                    _diagnostics.Warning(
                        $"Unresolved type '{errorName}' mapped via short name lookup to '{shortMapped}'",
                        code: "CS2J1001",
                        category: "TypeResolution");
                    AddImportsForType(errorName);
                    return MapSimpleTypeName(shortMapped);
                }

                // LINQ type parameter names that leaked into generated code as
                // unresolved types — map to Object so they don't cause undeclared-
                // type compilation errors in the Java output.
                if (errorName is "TSource" or "TResult" or "TKey" or "TElement"
                    or "TFirst" or "TSecond" or "TAccumulate")
                    return "Object";

                _diagnostics.Warning(
                    $"Unresolved type '{errorName}' — using short name (cross-namespace collision possible)",
                    code: "CS2J1001",
                    category: "TypeResolution");
                return MapSimpleTypeName(errorName);
            }

            _diagnostics.Warning(
                "Completely unresolved type degraded to Object",
                code: "CS2J1001",
                category: "TypeResolution");
            return "Object";
        }

        // Nullable value types
        if (typeSymbol.OriginalDefinition?.SpecialType == SpecialType.System_Nullable_T
            || typeSymbol.OriginalDefinition?.ToDisplayString() == "System.Nullable")
        {
            var nullableType = (INamedTypeSymbol)typeSymbol;
            if (nullableType.TypeArguments.Length == 0)
            {
                _diagnostics.Warning(
                    $"Unbound generic Nullable<T> without type arguments, mapping to Object",
                    code: "CS2J1001",
                    category: "TypeResolution");
                return "Object";
            }
            var underlyingType = nullableType.TypeArguments[0];
            var javaType = MapTypeInternal(underlyingType, referenceContext);

            if (_options.UseOptionalForNullable)
            {
                AddImport("java.util.Optional");
                return $"Optional<{javaType}>";
            }

            return javaType switch
            {
                "int"     => "Integer",
                "long"    => "Long",
                "double"  => "Double",
                "float"   => "Float",
                "short"   => "Short",
                "byte"    => "Byte",
                "char"    => "Character",
                "boolean" => "Boolean",
                _ => javaType
            };
        }

        // Anonymous types → synthesized record lookup
        if (typeSymbol is INamedTypeSymbol anonymousCheck && anonymousCheck.IsAnonymousType)
        {
            var props = anonymousCheck.GetMembers().OfType<IPropertySymbol>().ToList();
            if (props.Count > 0)
            {
                var fieldParts = new List<string>();
                foreach (var prop in props)
                {
                    var fieldName = JavaNaming.EscapeJavaKeyword(prop.Name);
                    fieldName = char.IsUpper(fieldName[0])
                        ? char.ToLower(fieldName[0]) + fieldName.Substring(1)
                        : fieldName;
                    var propJavaType = prop.Type.IsAnonymousType ? "Object" : MapTypeInternal(prop.Type, referenceContext);
                    fieldParts.Add($"{fieldName}:{propJavaType}");
                }
                var key = string.Join(",", fieldParts);
                if (_tryGetSynthesizedRecordMatch(key))
                    return GetSynthesizedRecordName(key);
            }
            return "Object";
        }

        // Array types
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elementType = MapTypeInternal(arrayType.ElementType, referenceContext);
            // C# byte[] stays Java byte[] for API compatibility
            if (arrayType.ElementType.SpecialType == SpecialType.System_Byte)
                elementType = "byte";
            var brackets = string.Concat(Enumerable.Repeat("[]", arrayType.Rank));
            return elementType + brackets;
        }

        if (typeSymbol is not ITypeParameterSymbol
            && typeSymbol.ContainingType is INamedTypeSymbol outerTypeForNested
            && outerTypeForNested.TypeKind != TypeKind.Error)
        {
            var nestedMapped = TryMapNestedType(typeSymbol, outerTypeForNested, typeSymbol.Name, referenceContext);
            if (nestedMapped != null)
                return nestedMapped;
        }

        // Generic types
        if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            var baseType = namedType.Name;
            var genericTypeNamespace = namedType.ContainingNamespace?.ToDisplayString();

            // Expression<TDelegate> → strip wrapper
            if (namedType.Name == "Expression"
                && namedType.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions"
                && namedType.TypeArguments.Length == 1)
            {
                return MapTypeInternal(namedType.TypeArguments[0], referenceContext);
            }

            var originalDefinition = namedType.OriginalDefinition ?? namedType.ConstructedFrom;

            string fullQualifiedName;
            if (originalDefinition != null)
            {
                var namespaceStr = originalDefinition.ContainingNamespace?.ToDisplayString() ?? "";
                fullQualifiedName = !string.IsNullOrEmpty(namespaceStr)
                    ? namespaceStr + "." + baseType + "`" + namedType.TypeArguments.Length
                    : baseType + "`" + namedType.TypeArguments.Length;
            }
            else
            {
                fullQualifiedName = baseType + "`" + namedType.TypeArguments.Length;
            }

            if (fullQualifiedName.StartsWith("global::"))
                fullQualifiedName = fullQualifiedName.Substring(8);

            var configKey = fullQualifiedName;
            var mappedBase = _typeMappings.HasTypeMapping(configKey)
                ? _typeMappings.MapType(configKey)
                : fullQualifiedName;

            if (mappedBase == fullQualifiedName)
            {
                configKey = baseType + "`" + namedType.TypeArguments.Length;
                mappedBase = _typeMappings.HasTypeMapping(configKey)
                    ? _typeMappings.MapType(configKey)
                    : fullQualifiedName;
            }

            if (mappedBase != fullQualifiedName)
            {
                baseType = MapSimpleTypeName(mappedBase);
                AddImportsForType(configKey);
            }

            var typeArgs = string.Join(", ", namedType.TypeArguments.Select(t => MapTypeForGeneric(t, referenceContext)));

            // IGrouping<K, V> maps to Map.Entry<K, List<V>> because Collectors.groupingBy()
            // groups element values into List<V>. Without this, the type arg V would not be
            // wrapped and for-each variable types would be Map.Entry<K, V> instead of Map.Entry<K, List<V>>.
            if (fullQualifiedName == "System.Linq.IGrouping`2"
                || configKey == "System.Linq.IGrouping`2")
            {
                var keyArg = MapTypeForGeneric(namedType.TypeArguments[0], referenceContext);
                var valueArg = MapTypeForGeneric(namedType.TypeArguments[1], referenceContext);
                AddImport("java.util.List");
                typeArgs = $"{keyArg}, List<{valueArg}>";
            }

            var tickIndex = baseType.IndexOf('`');
            if (tickIndex > 0)
                baseType = baseType.Substring(0, tickIndex);

            var currentNamespace = _getCurrentNamespace();
            if (!string.IsNullOrEmpty(genericTypeNamespace)
                && genericTypeNamespace.StartsWith("Microsoft.", StringComparison.Ordinal)
                && !string.Equals(currentNamespace, genericTypeNamespace, StringComparison.Ordinal)
                && namedType.ContainingType == null
                && (!string.IsNullOrWhiteSpace(currentNamespace) ? !NamespaceContainsType(currentNamespace, baseType) : true))
            {
                var javaPackage = NamespaceToPackage(genericTypeNamespace);
                AddImport($"{javaPackage}.{baseType}");
            }

            if (baseType == "Object")
                return "Object";

            // C# Func<T, bool> → Java Predicate<T> (not Function<T, Boolean>).
            // Java Stream.filter() requires Predicate, not Function.
            // Similarly, Func<T1, T2, bool> → BiPredicate<T1, T2>.
            if ((fullQualifiedName == "System.Func`2" || configKey == "System.Func`2")
                && namedType.TypeArguments.Length == 2
                && namedType.TypeArguments[1].SpecialType == SpecialType.System_Boolean)
            {
                var tArg = MapTypeForGeneric(namedType.TypeArguments[0], referenceContext);
                AddImport("java.util.function.Predicate");
                return $"Predicate<{tArg}>";
            }
            if ((fullQualifiedName == "System.Func`3" || configKey == "System.Func`3")
                && namedType.TypeArguments.Length == 3
                && namedType.TypeArguments[2].SpecialType == SpecialType.System_Boolean)
            {
                var tArg1 = MapTypeForGeneric(namedType.TypeArguments[0], referenceContext);
                var tArg2 = MapTypeForGeneric(namedType.TypeArguments[1], referenceContext);
                AddImport("java.util.function.BiPredicate");
                return $"BiPredicate<{tArg1}, {tArg2}>";
            }
            if ((fullQualifiedName == "System.Func`1" || configKey == "System.Func`1")
                && namedType.TypeArguments.Length == 1)
            {
                var resultType = MapTypeForGeneric(namedType.TypeArguments[0], referenceContext);
                if (resultType == "CompletableFuture")
                    return "Supplier<CompletableFuture<?>>";
            }

            return $"{baseType}<{typeArgs}>";
        }

        // Dynamic type
        if (typeSymbol is IDynamicTypeSymbol)
        {
            _diagnostics.Warning("Dynamic type converted to Object");
            return "Object";
        }

        // [Flags] enum → int
        if (typeSymbol is INamedTypeSymbol namedEnumCheck && namedEnumCheck.TypeKind == TypeKind.Enum)
        {
            bool hasFlagsAttr = namedEnumCheck.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
            if (hasFlagsAttr)
            {
                var flagsValueType = GetEnumValueJavaType(namedEnumCheck);
                RegisterFlagsEnum(namedEnumCheck.Name, flagsValueType);
                RegisterFlagsEnum(namedEnumCheck.ToDisplayString(), flagsValueType);
                return flagsValueType;
            }

            var displayName = namedEnumCheck.ToDisplayString();
            var fullyQualifiedName = TrimGlobalPrefix(namedEnumCheck.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            if (IsFlagsEnum(displayName))
                return GetFlagsEnumValueType(displayName);
            if (IsFlagsEnum(fullyQualifiedName))
                return GetFlagsEnumValueType(fullyQualifiedName);
        }

        // Non-generic named types — check config mapping
        var name = typeSymbol.Name;
        var assemblyScopedName = typeSymbol is INamedTypeSymbol namedNonGeneric
            ? _getAssemblyScopedTypeName(namedNonGeneric)
            : null;
        if (!string.IsNullOrWhiteSpace(assemblyScopedName))
            name = assemblyScopedName;

        var fullQualifiedNameSimple = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullQualifiedNameSimple.StartsWith("global::"))
            fullQualifiedNameSimple = fullQualifiedNameSimple.Substring(8);

        var mapped = _typeMappings.MapType(fullQualifiedNameSimple);

        var configKeySimple = fullQualifiedNameSimple;
        if (mapped == fullQualifiedNameSimple)
        {
            configKeySimple = name;
            mapped = _typeMappings.MapType(name);
        }

        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        var curNs = _getCurrentNamespace();

        if (mapped != name && mapped != fullQualifiedNameSimple)
        {
            AddImportsForType(configKeySimple);
            var mappedFromExplicitConfig = _typeMappings.HasTypeMapping(configKeySimple);
            var mappedSimple = MapSimpleTypeName(mapped);
            if (!mappedFromExplicitConfig
                && !string.IsNullOrWhiteSpace(curNs)
                && !string.IsNullOrWhiteSpace(ns)
                && !string.Equals(curNs, ns, StringComparison.Ordinal)
                && NamespaceContainsType(curNs, mappedSimple))
            {
                return $"{NamespaceToPackage(ns)}.{mappedSimple}";
            }
            return mappedSimple;
        }

        // When the mapped name equals the C# simple name (e.g. CSharpStack
        // → CSharpStack), the type name doesn't change but the config entry
        // may still carry imports that must be registered.
        AddImportsForType(configKeySimple);
        if (_typeMappings.HasTypeMapping(configKeySimple))
            return MapSimpleTypeName(mapped);

        // Cross-namespace imports for project types
            if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Microsoft."))
            {
                if (!string.Equals(curNs, ns, StringComparison.Ordinal)
                    && typeSymbol.ContainingType == null
                    && (!string.IsNullOrWhiteSpace(curNs) ? !NamespaceContainsType(curNs, name) : true))
            {
                var javaPackage = NamespaceToPackage(ns);
                AddImport($"{javaPackage}.{name}");
            }
        }

        // Same-name type shadowing
        if (!string.IsNullOrWhiteSpace(curNs)
            && !string.IsNullOrWhiteSpace(ns)
            && !string.Equals(curNs, ns, StringComparison.Ordinal)
            && NamespaceContainsType(curNs, name))
        {
            return $"{NamespaceToPackage(ns)}.{MapSimpleTypeName(name)}";
        }
        return QualifyTypeReferenceIfNeeded(typeSymbol, MapSimpleTypeName(name));
    }

    private string QualifyIfCurrentNamespaceHasDifferentType(ITypeSymbol typeSymbol, string javaType)
    {
        var curNs = _getCurrentNamespace();
        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        var simpleName = typeSymbol.Name;

        if (string.IsNullOrWhiteSpace(curNs)
            || string.IsNullOrWhiteSpace(ns)
            || string.Equals(curNs, ns, StringComparison.Ordinal)
            || typeSymbol.ContainingType != null
            || !NamespaceContainsType(curNs, simpleName))
        {
            return javaType;
        }

        var simpleJavaType = MapSimpleTypeName(simpleName);
        var simplePrefix = simpleJavaType + "<";
        if (javaType == simpleJavaType || javaType.StartsWith(simplePrefix, StringComparison.Ordinal))
            return $"{NamespaceToPackage(ns)}.{javaType}";

        return javaType;
    }

    private string? TryMapNestedType(
        ITypeSymbol typeSymbol,
        INamedTypeSymbol outerType,
        string name,
        TypeReferenceContext referenceContext)
    {
        // [Flags] enums are mapped to their underlying value type (int/long),
        // not to a nested class reference. Check before constructing the
        // "OuterType.InnerType" string which would produce invalid Java
        // (e.g. "Uri.Flags" as a parameter type instead of "long").
        // Use the symbol's [Flags] attribute directly rather than the name-based
        // registry (IsFlagsEnum) to avoid false positives when a non-Flags enum
        // shares a name with a previously-registered Flags enum from another file.
        if (typeSymbol is INamedTypeSymbol nestedEnum && nestedEnum.TypeKind == TypeKind.Enum)
        {
            bool hasFlagsAttr = nestedEnum.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
            if (hasFlagsAttr)
            {
                var flagsValueType = GetEnumValueJavaType(nestedEnum);
                // Also register in the name-based registry so downstream code
                // (expression transformers, etc.) can look it up by name.
                RegisterFlagsEnum(name, flagsValueType);
                var qualifiedEnumName = $"{outerType.Name}.{name}";
                RegisterFlagsEnum(qualifiedEnumName, flagsValueType);
                return flagsValueType;
            }
        }

        // Dictionary<K,V>.KeyCollection → Set<K>, Dictionary<K,V>.ValueCollection → Collection<V>
        var outerOriginal = outerType.OriginalDefinition?.ToDisplayString() ?? "";
        if (outerOriginal == "System.Collections.Generic.Dictionary<TKey, TValue>"
            && outerType.TypeArguments.Length == 2)
        {
            if (name == "KeyCollection")
            {
                var keyType = MapTypeForGeneric(outerType.TypeArguments[0], referenceContext);
                AddImport("java.util.Set");
                return $"Set<{keyType}>";
            }
            if (name == "ValueCollection")
            {
                var valueType = MapTypeForGeneric(outerType.TypeArguments[1], referenceContext);
                AddImport("java.util.Collection");
                return $"Collection<{valueType}>";
            }
        }

        // When referencing a nested type from within the same enclosing type
        // OR from within the nested type itself, use the simple name to avoid
        // Java raw type issues (e.g., "LowLevelDictionary.Entry" is a raw type
        // that loses generic parameters, while "Entry" preserves them).
        var currentEnclosing = _getCurrentEnclosingType();

        // Compare using OriginalDefinition to handle the case where a non-generic
        // nested class (like Entry) references itself. The type symbol for the
        // self-reference may have type arguments substituted from the outer class
        // (e.g., Entry<TKey, TValue>), while CurrentEnclosingRoslynType is the
        // declared symbol without type arguments (Entry). Comparing OriginalDefinition
        // strips the type arguments and allows the equality check to succeed.
        var currentEnclosingDef = currentEnclosing?.OriginalDefinition ?? currentEnclosing;
        var typeSymbolDef = (typeSymbol as INamedTypeSymbol)?.OriginalDefinition ?? typeSymbol;

        bool isSameEnclosingType = currentEnclosing != null
            && ((referenceContext != TypeReferenceContext.DeclarationHeader
                    && SymbolEqualityComparer.Default.Equals(currentEnclosing, outerType))
                || SymbolEqualityComparer.Default.Equals(currentEnclosingDef, typeSymbolDef));

        var innerName = MapSimpleTypeName(name);
        var outerName = isSameEnclosingType
            ? null  // No outer class prefix needed when inside the same class
            : QualifyTypeReferenceIfNeeded(outerType, MapSimpleTypeName(outerType.Name));
        var nestedStr = outerName != null ? $"{outerName}.{innerName}" : innerName;

        if (typeSymbol is INamedTypeSymbol namedNested && namedNested.TypeArguments.Length > 0)
        {
            nestedStr += "<" + string.Join(", ", namedNested.TypeArguments.Select(t => MapTypeForGeneric(t, referenceContext))) + ">";
        }
        else if (typeSymbol.TypeKind == TypeKind.Delegate)
        {
            var curOuter = outerType;
            var allTypeArgs = new List<string>();
            while (curOuter != null)
            {
                allTypeArgs.InsertRange(0, curOuter.TypeArguments.Select(t => MapTypeForGeneric(t, referenceContext)));
                curOuter = curOuter.ContainingType;
            }
            if (allTypeArgs.Count > 0)
                nestedStr += "<" + string.Join(", ", allTypeArgs) + ">";
        }
        else if (typeSymbol is INamedTypeSymbol nestedClass
            && nestedClass.TypeKind is TypeKind.Class or TypeKind.Struct
            && outerType.TypeParameters.Length > 0)
        {
            // C# nested classes that use the outer class's type parameters are made
            // static in Java with those type parameters added as their own.
            // When referencing such a nested type, we must include the type parameters.
            var usedParams = GetOuterTypeParamsReferencedByNested(nestedClass, outerType);
            if (usedParams.Count > 0)
            {
                nestedStr += "<" + string.Join(", ", usedParams.Select(p => MapTypeForGeneric(p, referenceContext))) + ">";
            }
        }

        return nestedStr;
    }

    /// <summary>
    /// Gets the outer type's type parameters that are referenced by a nested type.
    /// Used to include type parameters in nested type references when the nested type
    /// is made static in Java with those type parameters added as its own.
    /// </summary>
    private static List<ITypeSymbol> GetOuterTypeParamsReferencedByNested(
        INamedTypeSymbol nestedType,
        INamedTypeSymbol outerType)
    {
        var result = new List<ITypeSymbol>();
        var outerClassParams = outerType.TypeParameters
            .Where(tp => tp.DeclaringMethod == null)
            .ToList();
        if (outerClassParams.Count == 0)
            return result;

        // Collect names of the nested type's own type parameters (to skip shadowed ones)
        var nestedOwnParamNames = new HashSet<string>(
            nestedType.TypeParameters.Select(tp => tp.Name),
            StringComparer.Ordinal);

        foreach (var outerParam in outerClassParams)
        {
            if (nestedOwnParamNames.Contains(outerParam.Name))
                continue; // Shadowed by the nested type's own parameter

            if (NestedTypeReferencesTypeParam(nestedType, outerParam))
                result.Add(outerParam);
        }

        return result;
    }

    private static bool NestedTypeReferencesTypeParam(INamedTypeSymbol nestedSymbol, ITypeParameterSymbol typeParam)
    {
        foreach (var member in nestedSymbol.GetMembers())
        {
            if (member.IsStatic)
                continue;

            foreach (var type in GetMemberTypes(member))
            {
                if (TypeReferencesTypeParam(type, typeParam))
                    return true;
            }
        }

        if (nestedSymbol.BaseType != null && TypeReferencesTypeParam(nestedSymbol.BaseType, typeParam))
            return true;

        foreach (var iface in nestedSymbol.Interfaces)
        {
            if (TypeReferencesTypeParam(iface, typeParam))
                return true;
        }

        return false;
    }

    private static bool TypeReferencesTypeParam(ITypeSymbol type, ITypeParameterSymbol targetParam)
    {
        if (type is ITypeParameterSymbol tp && SymbolEqualityComparer.Default.Equals(tp, targetParam))
            return true;

        if (type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
        {
            foreach (var arg in named.TypeArguments)
            {
                if (TypeReferencesTypeParam(arg, targetParam))
                    return true;
            }
        }

        if (type is IArrayTypeSymbol array)
            return TypeReferencesTypeParam(array.ElementType, targetParam);

        return false;
    }

    private static IEnumerable<ITypeSymbol> GetMemberTypes(ISymbol member)
    {
        switch (member)
        {
            case IFieldSymbol field:
                yield return field.Type;
                break;
            case IPropertySymbol property:
                yield return property.Type;
                break;
            case IEventSymbol eventSymbol:
                yield return eventSymbol.Type;
                break;
            case IMethodSymbol method:
                if (method.AssociatedSymbol != null)
                    yield break;
                yield return method.ReturnType;
                foreach (var param in method.Parameters)
                    yield return param.Type;
                break;
        }
    }

    private string QualifyTypeReferenceIfNeeded(ITypeSymbol typeSymbol, string simpleTypeName)
    {
        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrWhiteSpace(ns) || ns == "<global namespace>")
            return simpleTypeName;

        // When the type has an explicit configured mapping (e.g. System.Decimal → Decimal
        // with import io.github.ningpp.compat.Decimal), the configured import already
        // resolves any ambiguity — skip namespace qualification.
        if (HasConfiguredTypeMapping(typeSymbol))
            return simpleTypeName;

        var curNs = _getCurrentNamespace();
        if (AliasNameCollidesWithDifferentType(typeSymbol)
            || ImportedSimpleNameCollidesWithType(ns, simpleTypeName))
        {
            return $"{NamespaceToPackage(ns)}.{simpleTypeName}";
        }

        if (string.Equals(curNs, ns, StringComparison.Ordinal))
            return simpleTypeName;

        if (TypeNameIsAmbiguousOutsideNamespace(typeSymbol.Name, ns)
            || (!string.IsNullOrWhiteSpace(curNs) && NamespaceContainsType(curNs, typeSymbol.Name)))
        {
            return $"{NamespaceToPackage(ns)}.{simpleTypeName}";
        }

        return simpleTypeName;
    }

    private bool AliasNameCollidesWithDifferentType(ITypeSymbol typeSymbol)
    {
        var aliasTarget = _resolveAlias(typeSymbol.Name);
        return aliasTarget != null && !SameTypeSymbol(aliasTarget, typeSymbol);
    }

    private bool ImportedSimpleNameCollidesWithType(string namespaceName, string simpleTypeName)
    {
        if (string.IsNullOrWhiteSpace(simpleTypeName))
            return false;

        var ownJavaName = $"{NamespaceToPackage(namespaceName)}.{simpleTypeName}";
        foreach (var importedType in _importedTypes)
        {
            if (importedType.EndsWith(".*", StringComparison.Ordinal))
                continue;

            var lastDot = importedType.LastIndexOf('.');
            if (lastDot < 0)
                continue;

            if (importedType[(lastDot + 1)..] == simpleTypeName
                && !string.Equals(importedType, ownJavaName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameTypeSymbol(ITypeSymbol left, ITypeSymbol right)
    {
        if (SymbolEqualityComparer.Default.Equals(left, right))
            return true;

        return left is INamedTypeSymbol leftNamed
            && right is INamedTypeSymbol rightNamed
            && SymbolEqualityComparer.Default.Equals(leftNamed.OriginalDefinition, rightNamed.OriginalDefinition);
    }

    private bool TypeNameIsAmbiguousOutsideNamespace(string typeName, string ownNamespace)
    {
        var globalNs = _getGlobalNamespace();
        if (globalNs == null || string.IsNullOrWhiteSpace(typeName))
            return false;

        return CountTypeNameOccurrences(globalNs, typeName, ownNamespace, 0) > 0;
    }

    private static int CountTypeNameOccurrences(
        INamespaceSymbol namespaceSymbol,
        string typeName,
        string ownNamespace,
        int count)
    {
        foreach (var typeMember in namespaceSymbol.GetTypeMembers(typeName))
        {
            var ns = typeMember.ContainingNamespace?.ToDisplayString();
            if (!string.Equals(ns, ownNamespace, StringComparison.Ordinal))
            {
                count++;
                if (count > 0)
                    return count;
            }
        }

        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            count = CountTypeNameOccurrences(childNamespace, typeName, ownNamespace, count);
            if (count > 0)
                return count;
        }

        return count;
    }

    private bool NamespaceContainsType(string namespaceName, string typeName)
    {
        var globalNs = _getGlobalNamespace();
        if (globalNs == null || string.IsNullOrWhiteSpace(namespaceName) || string.IsNullOrWhiteSpace(typeName))
            return false;

        var ns = ResolveNamespaceSymbol(globalNs, namespaceName);
        return ns?.GetTypeMembers(typeName).Length > 0;
    }

    private static string GetEnumValueJavaType(INamedTypeSymbol enumType)
    {
        return enumType.EnumUnderlyingType?.SpecialType switch
        {
            SpecialType.System_Int64 or SpecialType.System_UInt64 => "long",
            _ => "int"
        };
    }

    private static string TrimGlobalPrefix(string name)
        => name.StartsWith("global::", StringComparison.Ordinal) ? name["global::".Length..] : name;

    private bool HasConfiguredTypeMapping(ITypeSymbol typeSymbol)
    {
        var fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName["global::".Length..];

        if (_typeMappings.HasTypeMapping(fullName))
            return true;

        if (IsFrameworkTypeSymbol(typeSymbol)
            && _typeMappings.FindConfigKeyBySimpleName(typeSymbol.Name) is not null)
            return true;

        if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            var ns = namedType.ContainingNamespace?.ToDisplayString();
            var qualifiedGenericName = !string.IsNullOrWhiteSpace(ns) && ns != "<global namespace>"
                ? $"{ns}.{namedType.Name}`{namedType.TypeArguments.Length}"
                : $"{namedType.Name}`{namedType.TypeArguments.Length}";

            if (_typeMappings.HasTypeMapping(qualifiedGenericName))
                return true;
        }

        return false;
    }

    private static bool IsFrameworkTypeSymbol(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.SpecialType != SpecialType.None)
            return true;

        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrWhiteSpace(ns))
            return false;

        return ns.StartsWith("System.", StringComparison.Ordinal)
            || ns == "System";
    }

    private static INamespaceSymbol? ResolveNamespaceSymbol(INamespaceSymbol root, string namespaceName)
    {
        var current = root;
        foreach (var part in namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current == null)
                return null;
        }
        return current;
    }

    internal string MapSimpleTypeName(string typeName)
    {
        return typeName switch
        {
            "String" => "String",
            "Int32" => "int",
            "Int64" => "long",
            "Int16" => "short",
            "Byte" => "int",
            "SByte" => "byte",
            "UInt32" => "int",
            "UInt64" => "long",
            "UInt16" => "int",
            "Single" => "float",
            "Double" => "double",
            "Boolean" => "boolean",
            "Char" => "char",
            "Object" => "Object",
            "Void" => "void",
            "var" => "var",
            _ => typeName
        };
    }

    private string MapTypeForGeneric(ITypeSymbol typeSymbol, TypeReferenceContext referenceContext = TypeReferenceContext.Default)
    {
        var result = referenceContext == TypeReferenceContext.Default
            ? MapType(typeSymbol)
            : MapTypeInternal(typeSymbol, referenceContext);
        return BoxPrimitive(QualifyIfCurrentNamespaceHasDifferentType(typeSymbol, result));
    }

    /// <summary>
    /// Boxes a Java primitive type name to its wrapper type for use in generic type arguments.
    /// </summary>
    private static string BoxPrimitive(string typeName) => typeName switch
    {
        "int" => "Integer",
        "long" => "Long",
        "short" => "Short",
        "byte" => "Byte",
        "float" => "Float",
        "double" => "Double",
        "boolean" => "Boolean",
        "char" => "Character",
        _ => typeName
    };

    private void AddImportsForType(string csharpType)
    {
        var imports = _typeMappings.GetRequiredImports(csharpType);
        foreach (var import in imports)
            AddImport(import);
    }

    public void AddImportsForTypePublic(string csharpType) => AddImportsForType(csharpType);

    private void AddImport(string typeName)
    {
        if (IsImplicitJavaLangType(typeName)) return;
        _importedTypes.Add(typeName);
    }

    private static bool IsImplicitJavaLangType(string typeName)
    {
        if (!typeName.StartsWith("java.lang.", StringComparison.Ordinal))
            return false;

        var remainder = typeName["java.lang.".Length..];
        return !remainder.Contains('.', StringComparison.Ordinal);
    }

    private string MapTypeFromSyntaxString(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return "Object";

        // [Flags] enum types are mapped to their underlying value type (int/long).
        // When the semantic model cannot resolve the type (e.g. due to compilation errors
        // in the same file), ConvertParameter falls back to this syntax-based path.
        // Without this check, a parameter like "Uri.Flags flags" would keep the class
        // name as the Java type instead of "long", producing invalid Java code.
        if (IsFlagsEnum(typeName))
            return GetFlagsEnumValueType(typeName);

        var keywordMapped = typeName switch
        {
            "bool"    => "boolean",
            "int"     => "int",
            "long"    => "long",
            "short"   => "short",
            "byte"    => "int",
            "sbyte"   => "byte",
            "uint"    => "int",
            "ulong"   => "long",
            "ushort"  => "short",
            "float"   => "float",
            "double"  => "double",
            "decimal" => "Decimal",
            "char"    => "char",
            "void"    => "void",
            "object"  => "Object",
            "string"  => "String",
            _         => (string?)null
        };
        if (keywordMapped != null) return keywordMapped;

        if (typeName.EndsWith("?") && typeName.Length > 1)
            return MapTypeFromSyntaxString(typeName.Substring(0, typeName.Length - 1));

        // C# byte[] stays Java byte[] for API compatibility
        if (typeName == "byte[]") return "byte[]";

        if (typeName.EndsWith("[]"))
        {
            var elemType = MapTypeFromSyntaxString(typeName.Substring(0, typeName.Length - 2));
            return elemType + "[]";
        }

        var openAngle = typeName.IndexOf('<');
        if (openAngle > 0 && typeName.EndsWith(">"))
        {
            var baseTypeName = typeName.Substring(0, openAngle).Trim();
            var innerArgsText = typeName.Substring(openAngle + 1, typeName.Length - openAngle - 2);

            // Recursively map each generic type argument, respecting nested angle brackets
            var innerArgParts = SplitGenericArguments(innerArgsText);
            var mappedInnerArgs = innerArgParts
                .Select(arg => BoxPrimitive(MapTypeFromSyntaxString(arg.Trim())))
                .ToList();
            var mappedArgsString = string.Join(", ", mappedInnerArgs);

            var arity = mappedInnerArgs.Count;
            var configKey = _typeMappings.FindConfigKeyBySimpleName(baseTypeName, arity);
            var mappedBase = configKey != null
                ? _typeMappings.MapType(configKey)
                : _typeMappings.MapTypeBySimpleName(baseTypeName, arity);
            if (configKey != null)
                AddImportsForType(configKey);

            if (configKey != null || mappedBase != baseTypeName)
            {
                mappedBase = MapSimpleTypeName(mappedBase);
            }
            if (mappedBase == "Object") return "Object";

            // IGrouping<K, V> → Map.Entry<K, List<V>> (mirrors semantic path at MapTypeInternal)
            if (mappedBase == "Map.Entry" && mappedInnerArgs.Count == 2)
            {
                AddImport("java.util.List");
                mappedArgsString = $"{mappedInnerArgs[0]}, List<{mappedInnerArgs[1]}>";
            }

            return $"{mappedBase}<{mappedArgsString}>";
        }

        var mapped = _typeMappings.MapType(typeName);
        if (mapped != typeName)
        {
            AddImportsForType(typeName);
            return MapSimpleTypeName(mapped);
        }

        var simpleConfigKey = _typeMappings.FindConfigKeyBySimpleName(typeName);
        if (simpleConfigKey != null)
        {
            AddImportsForType(simpleConfigKey);
            return MapSimpleTypeName(_typeMappings.MapTypeBySimpleName(typeName));
        }

        return MapSimpleTypeName(typeName);
    }

    // ─── Synthesized record name lookup (delegated back to context) ───

    private Func<string, string>? _getSynthesizedRecordName;

    internal void SetSynthesizedRecordNameResolver(Func<string, string> resolver)
    {
        _getSynthesizedRecordName = resolver;
    }

    private string GetSynthesizedRecordName(string key)
    {
        return _getSynthesizedRecordName?.Invoke(key) ?? "Object";
    }

    // ─── Java metadata validation ───

    /// <summary>
    /// Convenience accessor for the Java standard-library metadata index attached to
    /// the underlying <see cref="TypeMapping.TypeMappingRegistry"/>.
    /// </summary>
    public JavaLibraryIndex? JavaLibrary => _typeMappings.JavaLibrary;

    /// <summary>
    /// Validates that the mapped Java type <paramref name="javaTypeName"/> exists in the
    /// Java standard-library metadata.  Emits a <c>CS2J4001</c> diagnostic when the type
    /// is not found (and metadata is available).
    /// </summary>
    public void ValidateMappedJavaType(string javaTypeName, Location? location = null)
    {
        if (_typeMappings.JavaLibrary is null)
            return; // No metadata loaded — skip validation.

        // Skip primitive types and common types not in metadata
        if (IsPrimitiveOrCommon(javaTypeName))
            return;

        if (!_typeMappings.ValidateTypeExists(javaTypeName))
        {
            _diagnostics.Warning(
                $"Mapped Java type '{javaTypeName}' not found in Java standard-library metadata",
                location,
                code: "CS2J4001",
                category: "JavaApiValidation");
        }
    }

    /// <summary>
    /// Validates that the mapped Java method <paramref name="javaMethodName"/> exists on
    /// <paramref name="javaTypeName"/>.  Emits a <c>CS2J4002</c> diagnostic when the method
    /// is not found (and metadata is available).
    /// </summary>
    public void ValidateMappedJavaMethod(string javaTypeName, string javaMethodName, Location? location = null)
    {
        if (_typeMappings.JavaLibrary is null)
            return; // No metadata loaded — skip validation.

        if (IsPrimitiveOrCommon(javaTypeName))
            return;

        if (!_typeMappings.ValidateMethodExists(javaTypeName, javaMethodName))
        {
            _diagnostics.Warning(
                $"Mapped Java method '{javaTypeName}.{javaMethodName}' not found in Java standard-library metadata",
                location,
                code: "CS2J4002",
                category: "JavaApiValidation");
        }
    }

    private static bool IsPrimitiveOrCommon(string javaTypeName)
    {
        return javaTypeName is "int" or "long" or "short" or "byte" or "float" or "double"
            or "boolean" or "char" or "void"
            or "Object" or "String" or "var"
            or "Integer" or "Long" or "Short" or "Byte" or "Float" or "Double" or "Boolean" or "Character";
    }

    /// <summary>
    /// Splits generic type arguments by top-level commas, respecting nested angle brackets.
    /// e.g. "int, List<string>, Dictionary<int, string>" → ["int", "List<string>", "Dictionary<int, string>"]
    /// </summary>
    private static List<string> SplitGenericArguments(string argsText)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < argsText.Length; i++)
        {
            switch (argsText[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    result.Add(argsText.Substring(start, i - start));
                    start = i + 1;
                    break;
            }
        }
        result.Add(argsText.Substring(start));
        return result;
    }
}
