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
    private readonly ConversionOptions _options;
    private readonly TypeMapping.TypeMappingRegistry _typeMappings;
    private readonly DiagnosticCollector _diagnostics;
    private readonly HashSet<string> _importedTypes;
    private readonly Func<string> _getCurrentNamespace;
    private readonly Func<INamespaceSymbol?> _getGlobalNamespace;
    private readonly Func<string, bool> _tryGetSynthesizedRecordMatch;

    /// <summary>
    /// Per-file type symbol → Java type cache. Cleared per file via ClearCache().
    /// </summary>
    public Dictionary<ITypeSymbol, string> TypeCache { get; } = new();

    /// <summary>
    /// [Flags] enum names mapped to int in Java. Static to survive across files.
    /// </summary>
    private static readonly HashSet<string> _flagsEnumNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Enum names that have explicit integer values (need getValue()/fromValue() instead of ordinal()/values()[]).
    /// Static to survive across files.
    /// </summary>
    private static readonly HashSet<string> _explicitValueEnumNames = new(StringComparer.Ordinal);

    private readonly Func<string, ITypeSymbol?> _resolveAlias;

    public TypeMappingService(
        ConversionOptions options,
        TypeMapping.TypeMappingRegistry typeMappings,
        DiagnosticCollector diagnostics,
        HashSet<string> importedTypes,
        Func<string> getCurrentNamespace,
        Func<INamespaceSymbol?> getGlobalNamespace,
        Func<string, bool> tryGetSynthesizedRecordMatch,
        Func<string, ITypeSymbol?>? resolveAlias = null)
    {
        _options = options;
        _typeMappings = typeMappings;
        _diagnostics = diagnostics;
        _importedTypes = importedTypes;
        _resolveAlias = resolveAlias ?? (_ => null);
        _getCurrentNamespace = getCurrentNamespace;
        _getGlobalNamespace = getGlobalNamespace;
        _tryGetSynthesizedRecordMatch = tryGetSynthesizedRecordMatch;
    }

    public void RegisterFlagsEnum(string enumName) => _flagsEnumNames.Add(enumName);
    public bool IsFlagsEnum(string enumName) => _flagsEnumNames.Contains(enumName);

    public void RegisterExplicitValueEnum(string enumName) => _explicitValueEnumNames.Add(enumName);
    public bool IsExplicitValueEnum(string enumName) => _explicitValueEnumNames.Contains(enumName);

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
            return cached;

        var result = MapTypeInternal(typeSymbol);
        TypeCache[typeSymbol] = result;
        return result;
    }

    public string MapTypeFromSyntax(TypeSyntax typeSyntax)
    {
        if (typeSyntax == null) return "Object";

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

    private string MapTypeInternal(ITypeSymbol typeSymbol)
    {
        // Check using alias registry first: if the type name matches a registered
        // alias, resolve via the alias target type (handles project-pipeline aliases).
        // Guard: skip if alias target has the same name (prevents infinite recursion).
        if (_resolveAlias(typeSymbol.Name) is ITypeSymbol aliasTarget
            && aliasTarget.Name != typeSymbol.Name)
            return MapType(aliasTarget);

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
                return MapType(candidateType);
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
                        namedError.TypeArguments.Select(t => MapTypeForGeneric(t)));
                    return $"{MapSimpleTypeName(fuzzyMapped)}<{typeArgs}>";
                }
            }

            // Try simple-name fuzzy lookup without arity
            var simpleMapped = _typeMappings.MapTypeBySimpleName(errorName);
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
            var underlyingType = ((INamedTypeSymbol)typeSymbol).TypeArguments[0];
            var javaType = MapType(underlyingType);

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
                    var propJavaType = prop.Type.IsAnonymousType ? "Object" : MapType(prop.Type);
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
            var elementType = MapType(arrayType.ElementType);
            var brackets = string.Concat(Enumerable.Repeat("[]", arrayType.Rank));
            return elementType + brackets;
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
                return MapType(namedType.TypeArguments[0]);
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

            var mappedBase = _typeMappings.MapType(fullQualifiedName);

            var configKey = fullQualifiedName;
            if (mappedBase == fullQualifiedName)
            {
                configKey = baseType + "`" + namedType.TypeArguments.Length;
                mappedBase = _typeMappings.MapType(configKey);
            }

            if (mappedBase != fullQualifiedName)
            {
                baseType = MapSimpleTypeName(mappedBase);
                AddImportsForType(configKey);
            }

            var typeArgs = string.Join(", ", namedType.TypeArguments.Select(t => MapTypeForGeneric(t)));

            // IGrouping<K, V> maps to Map.Entry<K, List<V>> because Collectors.groupingBy()
            // groups element values into List<V>. Without this, the type arg V would not be
            // wrapped and for-each variable types would be Map.Entry<K, V> instead of Map.Entry<K, List<V>>.
            if (fullQualifiedName == "System.Linq.IGrouping`2"
                || configKey == "System.Linq.IGrouping`2")
            {
                var keyArg = MapTypeForGeneric(namedType.TypeArguments[0]);
                var valueArg = MapTypeForGeneric(namedType.TypeArguments[1]);
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
            if (IsFlagsEnum(namedEnumCheck.Name))
                return "int";
            bool hasFlagsAttr = namedEnumCheck.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
            if (hasFlagsAttr)
            {
                RegisterFlagsEnum(namedEnumCheck.Name);
                return "int";
            }
        }

        // Non-generic named types — check config mapping
        var name = typeSymbol.Name;

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
            var mappedSimple = MapSimpleTypeName(mapped);
            if (mappedSimple == "Edge" && ns == "Microsoft.Msagl.Core.Layout"
                && !string.Equals(curNs, ns, StringComparison.Ordinal))
            {
                return "Microsoft.Msagl.Core.Layout.Edge";
            }
            if (!string.IsNullOrWhiteSpace(curNs)
                && !string.IsNullOrWhiteSpace(ns)
                && !string.Equals(curNs, ns, StringComparison.Ordinal)
                && NamespaceContainsType(curNs, mappedSimple))
            {
                return $"{NamespaceToPackage(ns)}.{mappedSimple}";
            }
            return mappedSimple;
        }

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

        // Nested types
        if (typeSymbol is not ITypeParameterSymbol
            && typeSymbol.ContainingType is INamedTypeSymbol outerType
            && outerType.TypeKind != TypeKind.Error)
        {
            // Dictionary<K,V>.KeyCollection → Set<K>, Dictionary<K,V>.ValueCollection → Collection<V>
            var outerOriginal = outerType.OriginalDefinition?.ToDisplayString() ?? "";
            if (outerOriginal == "System.Collections.Generic.Dictionary<TKey, TValue>"
                && outerType.TypeArguments.Length == 2)
            {
                if (name == "KeyCollection")
                {
                    var keyType = MapTypeForGeneric(outerType.TypeArguments[0]);
                    AddImport("java.util.Set");
                    return $"Set<{keyType}>";
                }
                if (name == "ValueCollection")
                {
                    var valueType = MapTypeForGeneric(outerType.TypeArguments[1]);
                    AddImport("java.util.Collection");
                    return $"Collection<{valueType}>";
                }
            }

            var nestedStr = $"{outerType.Name}.{MapSimpleTypeName(name)}";
            if (typeSymbol.TypeKind == TypeKind.Delegate)
            {
                var curOuter = outerType;
                var allTypeArgs = new List<string>();
                while (curOuter != null)
                {
                    allTypeArgs.InsertRange(0, curOuter.TypeArguments.Select(t => MapTypeForGeneric(t)));
                    curOuter = curOuter.ContainingType;
                }
                if (allTypeArgs.Count > 0)
                    nestedStr += "<" + string.Join(", ", allTypeArgs) + ">";
            }
            return nestedStr;
        }

        // Same-name type shadowing
        if (!string.IsNullOrWhiteSpace(curNs)
            && !string.IsNullOrWhiteSpace(ns)
            && !string.Equals(curNs, ns, StringComparison.Ordinal)
            && NamespaceContainsType(curNs, name))
        {
            return $"{NamespaceToPackage(ns)}.{MapSimpleTypeName(name)}";
        }

        if (name == "Edge" && ns == "Microsoft.Msagl.Core.Layout"
            && !string.Equals(curNs, ns, StringComparison.Ordinal))
        {
            return "Microsoft.Msagl.Core.Layout.Edge";
        }

        return MapSimpleTypeName(name);
    }

    private bool NamespaceContainsType(string namespaceName, string typeName)
    {
        var globalNs = _getGlobalNamespace();
        if (globalNs == null || string.IsNullOrWhiteSpace(namespaceName) || string.IsNullOrWhiteSpace(typeName))
            return false;

        var ns = ResolveNamespaceSymbol(globalNs, namespaceName);
        return ns?.GetTypeMembers(typeName).Length > 0;
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
            "Byte" => "byte",
            "SByte" => "byte",
            "UInt32" => "int",
            "UInt64" => "long",
            "UInt16" => "short",
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

    private string MapTypeForGeneric(ITypeSymbol typeSymbol)
    {
        var result = MapType(typeSymbol);
        return BoxPrimitive(result);
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
        if (typeName.StartsWith("java.lang.")) return;
        _importedTypes.Add(typeName);
    }

    private string MapTypeFromSyntaxString(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return "Object";

        var keywordMapped = typeName switch
        {
            "bool"    => "boolean",
            "int"     => "int",
            "long"    => "long",
            "short"   => "short",
            "byte"    => "byte",
            "sbyte"   => "byte",
            "uint"    => "int",
            "ulong"   => "long",
            "ushort"  => "short",
            "float"   => "float",
            "double"  => "double",
            "decimal" => "double",
            "char"    => "char",
            "void"    => "void",
            "object"  => "Object",
            "string"  => "String",
            _         => (string?)null
        };
        if (keywordMapped != null) return keywordMapped;

        if (typeName.EndsWith("?") && typeName.Length > 1)
            return MapTypeFromSyntaxString(typeName.Substring(0, typeName.Length - 1));

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
            var mappedBase = _typeMappings.MapTypeBySimpleName(baseTypeName, arity);
            if (mappedBase != baseTypeName)
            {
                var configKey = _typeMappings.FindConfigKeyBySimpleName(baseTypeName, arity);
                if (configKey != null)
                    AddImportsForType(configKey);
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
