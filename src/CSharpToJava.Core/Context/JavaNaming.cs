using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Kind of type-erasure conflict between two method overloads.
/// </summary>
public enum ErasureConflictKind
{
    None,
    DifferentTypeParamCount,
    SameTypeParamCount
}

/// <summary>
/// Static utility methods for Java naming conventions (keyword escaping, type erasure conflict detection).
/// Extracted from ConversionContext.
/// </summary>
public static class JavaNaming
{
    /// <summary>
    /// Computes the Java getter method name for a C# property, avoiding the clash
    /// with Object.getClass() (which is final) by renaming the getter of a property
    /// literally named "Class" to getClassValue(). Setter/setClass is unaffected.
    /// </summary>
    public static string GetPropertyGetterName(string propertyName)
    {
        string baseName = "get" + char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        return baseName == "getClass" ? "getClassValue" : baseName;
    }

    /// <summary>
    /// TypeMappings 的方法重命名表同时承载两类属性映射：属性→Java 方法（bean 风格
    /// getX/setX/isX，如 ThreadLocal.IsValueCreated→isValueCreated）与属性→字段
    /// （如 Array.Length→length、ICollection.Count→size，后者由 lower 阶段以无括号
    /// 成员访问消费）。只有 bean 风格名才能在属性读写点直接当作方法调用。
    /// </summary>
    public static bool IsBeanAccessorStyleName(string? mappedName)
    {
        if (string.IsNullOrEmpty(mappedName)) return false;
        foreach (var prefix in new[] { "get", "set", "is" })
        {
            if (mappedName.Length > prefix.Length
                && mappedName.StartsWith(prefix, StringComparison.Ordinal)
                && char.IsUpper(mappedName[prefix.Length]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the property hides a base-class property with a Java-incompatible
    /// return type (C# `new`, no covariant override possible). Java forbids two
    /// same-name methods with unrelated return types in a hierarchy, so the derived
    /// getter/setter must be emitted under a disambiguated name and annotated with
    /// <c>@CompatPropertyName</c> to keep reflection/serialization metadata intact.
    /// </summary>
    public static bool IsIncompatibleTypeHidingProperty(IPropertySymbol prop)
    {
        if (prop.IsOverride || prop.IsStatic)
            return false;

        var baseType = prop.ContainingType?.BaseType;
        while (baseType != null)
        {
            foreach (var member in baseType.GetMembers(prop.Name))
            {
                if (member is IPropertySymbol baseProp
                    && member.DeclaredAccessibility != Accessibility.Private
                    && !SymbolEqualityComparer.Default.Equals(prop.Type, baseProp.Type)
                    && !IsCsSubtypeOf(prop.Type, baseProp.Type))
                {
                    return true;
                }
            }
            baseType = baseType.BaseType;
        }
        return false;
    }

    private static bool IsCsSubtypeOf(ITypeSymbol derived, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(derived, baseType))
            return true;
        if (derived is ITypeParameterSymbol)
            return false;

        var current = derived.BaseType;
        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)
                || SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType.OriginalDefinition))
            {
                return true;
            }
            current = current.BaseType;
        }

        if (derived is INamedTypeSymbol namedDerived)
        {
            foreach (var iface in namedDerived.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, baseType)
                    || SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, baseType.OriginalDefinition))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Computes the synthesized backing-field name for an auto-property, escaping
    /// Java keywords and disambiguating when a sibling property maps to the same
    /// camelCased name (e.g. `age` vs `Age` both yield "age").
    /// </summary>
    public static string GetAutoPropertyBackingFieldName(IPropertySymbol prop)
    {
        var camelCase = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
        var fieldName = EscapeJavaKeyword(camelCase);

        var containingType = prop.ContainingType;
        if (containingType == null)
            return fieldName;

        // The FIRST-declared property keeps the plain camelCased name; later siblings
        // with the same camelCase — or any explicit field with that name — get a Value
        // suffix (consistent at declaration and assignment sites).
        var thisStart = prop.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue;
        bool collides = containingType.GetMembers().Any(m =>
        {
            if (SymbolEqualityComparer.Default.Equals(m, prop))
                return false;
            if (m is IPropertySymbol siblingProp
                && string.Equals(ToCamelCase(siblingProp.Name), ToCamelCase(prop.Name), StringComparison.Ordinal)
                && (siblingProp.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue) < thisStart)
            {
                return true;
            }
            return m is IFieldSymbol siblingField
                && !siblingField.IsImplicitlyDeclared
                && string.Equals(siblingField.Name, ToCamelCase(prop.Name), StringComparison.Ordinal);
        });
        return collides ? fieldName + "Value" : fieldName;
    }

    private static string ToCamelCase(string name)
        => name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;

    public static bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or "case" or "catch" or
            "char" or "class" or "const" or "continue" or "default" or "do" or "double" or
            "else" or "enum" or "extends" or "final" or "finally" or "float" or "for" or
            "goto" or "if" or "implements" or "import" or "instanceof" or "int" or
            "interface" or "long" or "native" or "new" or "package" or "private" or
            "protected" or "public" or "return" or "short" or "static" or "strictfp" or
            "super" or "switch" or "synchronized" or "this" or "throw" or "throws" or
              "transient" or "try" or "void" or "volatile" or "while"
              or "true" or "false" or "null"
              or "_"
              // Restricted identifiers that cannot be used as method names in
              // statement-expression positions (e.g. yield(...) — ambiguous with
              // the yield statement).
              or "yield" => true,
              _ => false
        };
    }

    public static string EscapeJavaKeyword(string word)
    {
        // Strip C# verbatim identifier prefix '@' (e.g., @event → event, @object → object).
        // In C#, '@' allows reserved keywords to be used as identifiers.
        // In Java, '@' is an annotation marker and cannot appear in identifiers.
        // After stripping, the bare name may become a Java keyword, so we still
        // need to escape it below.
        if (word.StartsWith("@", StringComparison.Ordinal))
            word = word[1..];

        return IsJavaKeyword(word) ? word + "Value" : word;
    }

    /// <summary>
    /// Determines whether a C# method has a type-erasure conflict with another overload,
    /// and returns the kind of conflict.
    /// </summary>
    public static (bool HasConflict, ErasureConflictKind Kind) GetErasureConflictKind(IMethodSymbol method)
    {
        if (method.ContainingType == null) return (false, ErasureConflictKind.None);
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return (false, ErasureConflictKind.None);

        foreach (var sibling in method.ContainingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method)) continue;
            if (sibling.Name != method.Name) continue;

            // When parameter counts differ, the generic overload may still
            // collide after the converter injects leading Class<T> _cs2j_
            // parameters (one per method-level type parameter that requires
            // a runtime Class token, e.g. typeof(T) in the body). Check if
            // the adjusted parameter counts would match.
            if (sibling.Parameters.Length != method.Parameters.Length)
            {
                // Only consider cross-arity conflicts when one method is
                // generic and the other is not (DifferentTypeParamCount).
                if (method.TypeParameters.Length == sibling.TypeParameters.Length)
                    continue;

                // The generic method gains one leading Class<T> parameter
                // per method-level type parameter that needs a runtime token.
                // Use the simple heuristic: a generic method with 0 C# params
                // and 1+ type params will have 1+ Java params (at least the
                // typeof(T) sentinel guarantees this via EnsurePendingParamsInBaseAndImpl).
                var genericMethod = method.TypeParameters.Length > sibling.TypeParameters.Length
                    ? method : sibling;
                var nonGenericMethod = method.TypeParameters.Length > sibling.TypeParameters.Length
                    ? sibling : method;

                // Heuristic: a generic method with N C# params and M type params
                // will have N + (0 or M) Java params. The simplest case is
                // 0 C# params → 0 or M Java params. If the non-generic has the
                // same number of params as the generic's type params, they
                // would collide when the generic gets Class<T> params injected.
                // This is a conservative check — false positives would just
                // add an unnecessary suffix that the call site must also match.
                var adjustedGenericParamCount = genericMethod.Parameters.Length + genericMethod.TypeParameters.Length;
                if (adjustedGenericParamCount != nonGenericMethod.Parameters.Length)
                    continue;

                // Check erased parameters of the non-generic method against
                // what the generic's Java params would look like: the injected
                // Class<T> params all erase to Class, and the original C# params
                // erase normally. Only verify that the non-generic's first
                // param erases to something Class-compatible (i.e. it's a Type
                // or Class parameter — both erase to Class in Java).
                if (!IsClassErasedParameter(nonGenericMethod.Parameters[0]))
                    continue;

            // Only the method with FEWER type parameters gets renamed
            // (mirrors the same-count path: first one in source order wins).
            if (method.TypeParameters.Length < sibling.TypeParameters.Length)
                return (true, ErasureConflictKind.DifferentTypeParamCount);
            continue;
            }
            if (!HaveSameErasedParameters(method, sibling)) continue;

            var kind = sibling.TypeParameters.Length == method.TypeParameters.Length
                ? ErasureConflictKind.SameTypeParamCount
                : ErasureConflictKind.DifferentTypeParamCount;

            if (kind == ErasureConflictKind.DifferentTypeParamCount)
            {
                if (method.TypeParameters.Length < sibling.TypeParameters.Length)
                    return (true, kind);
            }
            else
            {
                // Both methods are in conflict. Determine which one should be renamed
                // by source order: the first one keeps original name.
                var myLoc = method.Locations.FirstOrDefault(l => l.SourceTree != null);
                var sibLoc = sibling.Locations.FirstOrDefault(l => l.SourceTree != null);
                if (myLoc != null && sibLoc != null)
                {
                    var myLine = myLoc.GetLineSpan().StartLinePosition.Line;
                    var sibLine = sibLoc.GetLineSpan().StartLinePosition.Line;
                    if (myLine > sibLine) return (true, kind);
                }
            }
        }

        return (false, ErasureConflictKind.None);
    }

    /// <summary>
    /// Returns true if the parameter's type would erase to Class in Java
    /// (System.Type, System.Reflection.FieldInfo, etc. all map to Class or
    /// compatible reflection types that erase to Class).
    /// </summary>
    private static bool IsClassErasedParameter(IParameterSymbol param)
    {
        var typeName = param.Type.ToDisplayString();
        return typeName == "System.Type" || typeName == "Type";
    }

    /// <summary>
    /// For SameTypeParamCount conflicts, computes a stable 1-based index within the
    /// conflict group (sorted by source line). Returns 1 for the first method (keeps
    /// original name), 2+ for subsequent methods that need _erasure_N suffix.
    /// </summary>
    public static int GetErasureConflictIndex(IMethodSymbol method)
    {
        if (method.ContainingType == null) return 1;

        var conflictGroup = method.ContainingType.GetMembers().OfType<IMethodSymbol>()
            .Where(s => s.Name == method.Name
                && s.Parameters.Length == method.Parameters.Length
                && s.TypeParameters.Length == method.TypeParameters.Length
                && HaveSameErasedParameters(method, s))
            .Select(s => new
            {
                Symbol = s,
                Line = s.Locations.FirstOrDefault(l => l.SourceTree != null)
                    ?.GetLineSpan().StartLinePosition.Line ?? 0
            })
            .OrderBy(x => x.Line)
            .ToList();

        if (conflictGroup.Count <= 1) return 1;

        for (int i = 0; i < conflictGroup.Count; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(conflictGroup[i].Symbol, method))
                return i + 1;
        }

        return 1;
    }

    /// <summary>
    /// Returns the suffix to append to a method name when it has an erasure conflict,
    /// or an empty string if no conflict exists.
    /// </summary>
    public static string GetErasureConflictSuffix(IMethodSymbol method)
    {
        var (hasConflict, kind) = GetErasureConflictKind(method);
        if (!hasConflict) return "";

        return kind switch
        {
            ErasureConflictKind.DifferentTypeParamCount => $"_{method.TypeParameters.Length}tp",
            ErasureConflictKind.SameTypeParamCount => $"_erasure_{GetErasureConflictIndex(method)}",
            _ => ""
        };
    }

    /// <summary>
    /// Checks whether a C# method has a type-erasure conflict with another overload in the
    /// same type: same name, same erased parameter types, but different generic type parameter
    /// counts. Returns true for the overload with FEWER type parameters.
    /// </summary>
    public static bool HasTypeErasureConflict(IMethodSymbol method)
    {
        if (method.ContainingType == null) return false;
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        foreach (var sibling in method.ContainingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method)) continue;
            if (sibling.Name != method.Name) continue;
            if (sibling.Parameters.Length != method.Parameters.Length) continue;
            if (sibling.TypeParameters.Length == method.TypeParameters.Length) continue;
            if (HaveSameErasedParameters(method, sibling))
                return method.TypeParameters.Length < sibling.TypeParameters.Length;
        }
        return false;
    }

    public static string GetErasureRenamedSuffix(int typeParameterCount)
        => $"_{typeParameterCount}tp";

    /// <summary>
    /// Returns "_0tp" when the method is a non-generic overload of
    /// System.Text.Json.JsonSerializer (or dotnet.text.json.JsonSerializer)
    /// that takes a System.Type parameter — e.g. DeserializeAsync(Stream,
    /// Type, ...), SerializeAsync(Stream, Object, Type, ...). The generic
    /// overloads lead with a Class&lt;T&gt; token in Java, making bare-null
    /// call sites ambiguous; the _0tp suffix disambiguates the non-generic
    /// overload on both declarations and call sites.
    ///
    /// Also covers SerializationWrapper.SerializeWrapper(object, Type) and
    /// overrides in subclasses of that test helper.
    ///
    /// Returns an empty string when the suffix does not apply.
    /// </summary>
    public static string GetJsonTypeOverloadSuffix(IMethodSymbol? method, string methodName)
    {
        if (method is null) return "";
        // Non-generic JsonSerializer overloads taking System.Type
        if (method is { IsGenericMethod: false } jsonNg
            && jsonNg.ContainingType.ToDisplayString()
                is "System.Text.Json.JsonSerializer" or "dotnet.text.json.JsonSerializer"
            && methodName is "Deserialize" or "DeserializeAsync"
                or "Serialize" or "SerializeAsync" or "SerializeToUtf8Bytes"
            && jsonNg.Parameters.Any(p => p.Type.ToDisplayString() == "System.Type"))
        {
            return "_0tp";
        }

        // Non-generic SerializationWrapper.SerializeWrapper(object, Type)
        if (method is { IsGenericMethod: false } serWrapperNg
            && methodName == "SerializeWrapper"
            && serWrapperNg.Parameters.Any(p => p.Type.ToDisplayString() == "System.Type")
            && IsInSerializationWrapperHierarchy(method))
        {
            return "_0tp";
        }

        return "";
    }

    /// <summary>
    /// True when the method's containing type or any of its base types
    /// is SerializationWrapper (the test helper class whose non-generic
    /// SerializeWrapper(object, Type) overload needs the _0tp suffix to
    /// avoid ambiguity with the generic overload).
    /// </summary>
    private static bool IsInSerializationWrapperHierarchy(IMethodSymbol method)
    {
        var type = method.ContainingType;
        while (type != null)
        {
            if (type.ToDisplayString() == "dotnet.text.json.Serialization.Tests.SerializationWrapper")
                return true;
            type = type.BaseType;
        }
        return false;
    }

    private static bool HaveSameErasedParameters(IMethodSymbol a, IMethodSymbol b)
    {
        for (int i = 0; i < a.Parameters.Length; i++)
        {
            if (GetErasedTypeName(a.Parameters[i]) != GetErasedTypeName(b.Parameters[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Renames a nested delegate when its containing type declares another delegate
    /// with the same simple name but a different type-parameter count. C# allows
    /// same-name nested types with different arity, but Java forbids duplicate
    /// nested type names entirely. The smallest arity keeps the plain name; every
    /// other arity gets a "{name}_{arity}" suffix so the declaration and all
    /// reference sites agree on the Java name. Returns the (possibly unchanged) name.
    /// </summary>
    public static string GetNestedDelegateJavaName(INamedTypeSymbol delegateSymbol, string fallbackName)
    {
        var containingType = delegateSymbol.ContainingType;
        if (containingType == null)
            return fallbackName;

        var arities = containingType.GetTypeMembers(delegateSymbol.Name)
            .Where(t => t.TypeKind == TypeKind.Delegate)
            .Select(t => t.Arity)
            .Distinct()
            .ToList();
        if (arities.Count <= 1)
            return fallbackName;

        return arities.Min() == delegateSymbol.Arity
            ? fallbackName
            : fallbackName + "_" + delegateSymbol.Arity;
    }

    private static string GetErasedTypeName(IParameterSymbol parameter)
    {
        // ref/out parameters are wrapped in a holder class (ObjectHolder<T>, IntHolder,
        // ...) by the converter; Java erases the type argument, so e.g. DateTime and
        // DateTimeOffset out parameters collide to the same ObjectHolder signature.
        if (parameter.RefKind is RefKind.Ref or RefKind.Out)
            return "System.ObjectHolder";
        return GetErasedTypeName(parameter.Type);
    }

    private static string GetErasedTypeName(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => "System.Object",
        IArrayTypeSymbol arr => GetErasedTypeName(arr.ElementType) + "[]",
        INamedTypeSymbol named => GetNamedErasedTypeName(named.OriginalDefinition),
        _ => type.ToDisplayString()
    };

    private static string GetNamedErasedTypeName(INamedTypeSymbol named)
    {
        // Unsigned primitives map onto their same-width SIGNED JAVA counterparts
        // (ulong→long, uint→int, ushort→int, byte→int), so overloads differing only
        // in signedness collide after erasure and must be flagged for the _erasure_N
        // rename — otherwise the later overload is silently dropped and its callers
        // fall back to the signed overload's formatting (ulong.MaxValue wrote "-1"
        // instead of "18446744073709551615"). The widths are the JAVA ones: C# byte
        // and ushort widen to Java int (masked & 0xFF / & 0xFFFF at use), sbyte stays
        // Java byte, short stays Java short — grouping by C#-name width would merge
        // Byte into the SByte group and UInt16 into the Int16 group, silently dropping
        // their declarations (XmlConvertTests.toType33: ushort 4582 formatted "230").
        switch (named.SpecialType)
        {
            case SpecialType.System_UInt64: return "System.Int64";
            case SpecialType.System_UInt32: return "System.Int32";
            case SpecialType.System_UInt16: return "System.Int32";
            case SpecialType.System_Byte: return "System.Int32";
        }
        return named.ContainingNamespace + "." + named.Name;
    }

    /// <summary>
    /// Checks whether a method in a derived class has a cross-inheritance type-erasure conflict.
    /// Returns the name of the base-class method if the method's erased parameter signature
    /// matches a base-class method with different generic type arguments.
    /// </summary>
    public static string? FindCrossInheritanceErasureConflict(IMethodSymbol method)
    {
        if (method.ContainingType?.BaseType == null) return null;

        var erasedSig = GetErasedSignature(method);

        var baseType = method.ContainingType.BaseType;
        while (baseType != null)
        {
            foreach (var baseMember in baseType.GetMembers().OfType<IMethodSymbol>())
            {
                if (baseMember.Name != method.Name) continue;
                if (baseMember.Parameters.Length != method.Parameters.Length) continue;
                if (baseMember.DeclaredAccessibility == Accessibility.Private) continue;

                var baseErasedSig = GetErasedSignature(baseMember);
                if (erasedSig == baseErasedSig && !HaveSameActualParameters(method, baseMember))
                {
                    return $"{baseType.Name}.{baseMember.Name}";
                }
            }
            baseType = baseType.BaseType;
        }

        return null;
    }

    private static string GetErasedSignature(IMethodSymbol method)
        => string.Join(",", method.Parameters.Select(p => GetErasedTypeName(p.Type)));

    private static bool HaveSameActualParameters(IMethodSymbol a, IMethodSymbol b)
    {
        for (int i = 0; i < a.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(a.Parameters[i].Type, b.Parameters[i].Type))
                return false;
        }
        return true;
    }
}
