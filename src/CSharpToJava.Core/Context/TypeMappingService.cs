using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    public TypeMappingService(
        ConversionOptions options,
        TypeMapping.TypeMappingRegistry typeMappings,
        DiagnosticCollector diagnostics,
        HashSet<string> importedTypes,
        Func<string> getCurrentNamespace,
        Func<INamespaceSymbol?> getGlobalNamespace,
        Func<string, bool> tryGetSynthesizedRecordMatch)
    {
        _options = options;
        _typeMappings = typeMappings;
        _diagnostics = diagnostics;
        _importedTypes = importedTypes;
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
        // IErrorTypeSymbol is returned by the semantic model when a type cannot be resolved
        // (e.g. due to missing project references). Return "Object" instead of empty string
        // to avoid generating invalid Java like "ObjectHolder<>".
        if (typeSymbol is IErrorTypeSymbol)
            return "Object";

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
        return result switch
        {
            "int" => "Integer",
            "long" => "Long",
            "short" => "Short",
            "byte" => "Byte",
            "float" => "Float",
            "double" => "Double",
            "boolean" => "Boolean",
            "char" => "Character",
            _ => result
        };
    }

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
            var innerArgs = typeName.Substring(openAngle + 1, typeName.Length - openAngle - 2);
            var mappedBase = _typeMappings.MapType(baseTypeName);
            if (mappedBase != baseTypeName)
            {
                AddImportsForType(baseTypeName);
                mappedBase = MapSimpleTypeName(mappedBase);
            }
            if (mappedBase == "Object") return "Object";
            return $"{mappedBase}<{innerArgs}>";
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
}
