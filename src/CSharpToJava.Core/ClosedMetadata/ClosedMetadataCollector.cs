using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Whole-compilation closed-type enumerator (design D4 / CM-ENUM rules).
/// Symbol-driven only, reads the bound compilation shapes at the post-generic-deepen
/// chain position (D5) and never guesses a mapping:
/// - CM-ENUM-1: closed types in field/property/event/member signatures;
/// - CM-ENUM-2: explicit <c>typeof(闭型)</c> sites (default/cast/new forms bind the
///   same type symbols and reach registration transitively through their members);
/// - CM-ENUM-3: concrete type arguments of the JsonSerializer/options entry family;
/// - CM-ENUM-4: concrete generic constructions of base types and implemented
///   interfaces (covers JsonConverter&lt;T&gt; families and override/interface closure
///   signatures — all family members are visited anyway and deduped by key).
/// Shapes with no decidable Java target (pointer/function-pointer) raise CS2J1203,
/// arguments mentioning an unbound type parameter raise CS2J1210 (CM-DYN) and are
/// counted but never registered, and JsonSerializerContext/[JsonSerializable] AOT
/// shapes raise CS2J1204 and are excluded (CM-SRCGEN). Results are deduplicated
/// compilation-wide and ordered by the normalized key (CM-REG).
/// </summary>
public sealed class ClosedMetadataCollector
{
    /// <summary>CM-ENUM-1: closed types in field/property/event/method signatures.</summary>
    public const string DeclaredSitesRule = "CM-ENUM-1";

    /// <summary>CM-ENUM-2: explicit typeof closed-type sites.</summary>
    public const string ExplicitTypeOfRule = "CM-ENUM-2";

    /// <summary>CM-ENUM-3: concrete type arguments at serializer entry call sites.</summary>
    public const string SerializerCallSitesRule = "CM-ENUM-3";

    /// <summary>CM-ENUM-4: closed generic constructions in base/interface families.</summary>
    public const string SignatureSitesRule = "CM-ENUM-4";

    private const string JsonSerializerFullName = "System.Text.Json.JsonSerializer";
    private const string JsonSerializerOptionsFullName = "System.Text.Json.JsonSerializerOptions";
    private const string JsonSerializerContextFullName = "System.Text.Json.Serialization.JsonSerializerContext";
    private const string JsonSerializableAttributeName = "JsonSerializableAttribute";

    private static readonly HashSet<string> SerializerMethodNames = new(StringComparer.Ordinal)
    {
        "Serialize", "SerializeAsync", "Deserialize", "DeserializeAsync",
    };

    private readonly Compilation _compilation;
    private readonly ClosedMetadataOptions _options;
    private readonly List<ClosedMetadataDiagnostic> _diagnostics = new();
    private readonly Dictionary<ClosedTypeKey, ClosedTypeRecord> _records = new();
    private readonly ClosedMetadataStatistics _statistics = new();

    /// <summary>
    /// Types deriving (at any depth) from JsonSerializerContext. Their members are the
    /// AOT source-generation surface (CM-SRCGEN): excluded from registration. Populated
    /// during the CM-ENUM-1 symbol walk and consumed by the CM-ENUM-2 syntax walk.
    /// </summary>
    private readonly HashSet<INamedTypeSymbol> _sourceGenContexts =
        new(SymbolEqualityComparer.Default);

    private int _siteHits;

    public ClosedMetadataCollector(Compilation compilation, ClosedMetadataOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    public ClosedMetadataAnalysis Analyze()
    {
        if (_options.RegisterDeclaredClosedTypes || _options.RegisterSignatureSites)
        {
            VisitNamespace(_compilation.SourceModule.GlobalNamespace);
        }

        // CM-ENUM-3 needs no ENUM-1 walk; run it independently when selected. The
        // context set is empty in that mode, which is fine — it only feeds ENUM-2.
        if (_options.RegisterSerializerCallSites)
        {
            CollectSerializerCallSites();
        }

        if (_options.RegisterExplicitTypeOfSites)
        {
            CollectExplicitTypeOfSites();
        }

        var records = _records.Values
            .OrderBy(record => record.Key.SortText, StringComparer.Ordinal)
            .ToList();
        _statistics.ClosedTypesEnumerated = _siteHits;
        _statistics.Registered = records.Count;
        _statistics.Deduplicated = Math.Max(0, _siteHits - records.Count);

        return new ClosedMetadataAnalysis
        {
            Diagnostics = _diagnostics,
            ClosedTypes = records,
            Statistics = _statistics,
        };
    }

    private void VisitNamespace(INamespaceSymbol namespaceSymbol)
    {
        foreach (var member in namespaceSymbol.GetMembers())
        {
            if (member is INamespaceSymbol nested)
            {
                VisitNamespace(nested);
            }
            else if (member is INamedTypeSymbol type)
            {
                VisitNamedType(type);
            }
        }
    }

    private void VisitNamedType(INamedTypeSymbol type)
    {
        if (IsSourceGenContext(type))
        {
            // CM-SRCGEN: the whole type is the AOT reflection-free surface; enumerate
            // nothing inside it and surface CS2J1204 once per context type.
            _sourceGenContexts.Add(type);
            _diagnostics.Add(new ClosedMetadataDiagnostic(
                ClosedMetadataSeverity.Warning,
                ClosedMetadataRule.SourceGeneration,
                ClosedMetadataCodes.SourceGeneratedContext,
                $"Type '{type.ToDisplayString()}' derives from JsonSerializerContext; the source-generated " +
                "AOT surface is outside the closed-metadata served set (CM-SRCGEN) and its closures are not registered.",
                SourceLocation(type)?.SourceTree?.FilePath,
                SourceLocation(type)?.GetLineSpan().StartLinePosition.Line + 1));
            return;
        }

        if (_options.RegisterDeclaredClosedTypes)
        {
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                {
                    continue;
                }

                // CM-SRCGEN: [JsonSerializable(typeof(...))] attributes are the AOT
                // declaration form — report and never register their typeof payload.
                if (HasJsonSerializableAttribute(member, out var attributeLocation))
                {
                    ReportSourceGenAttribute(member.ToDisplayString(), attributeLocation);
                }

                switch (member)
                {
                    case IFieldSymbol field:
                        VisitSiteType(field.Type, DeclaredSitesRule, member.Name, SourceLocation(member));
                        break;
                    case IPropertySymbol property:
                        VisitSiteType(property.Type, DeclaredSitesRule, member.Name, SourceLocation(member));
                        break;
                    case IEventSymbol @event:
                        VisitSiteType(@event.Type, DeclaredSitesRule, member.Name, SourceLocation(member));
                        break;
                    case IMethodSymbol method when method.AssociatedSymbol is null:
                        // Accessors (get/set/add/remove) are the property/event's own signature
                        // expanded — visiting them would double-count the same site.
                        VisitSiteType(method.ReturnType, DeclaredSitesRule, member.Name, SourceLocation(member));
                        foreach (var parameter in method.Parameters)
                        {
                            VisitSiteType(parameter.Type, DeclaredSitesRule, member.Name, SourceLocation(member));
                        }

                        break;
                }
            }
        }

        if (_options.RegisterSignatureSites)
        {
            // CM-ENUM-4: concrete generic constructions in the family's base/interface
            // list (e.g. sealed `X : JsonConverter<MyType>` — Read/Write consume MyType,
            // and `class C : List<List<short>>` — the element closure is part of the family
            // signature). Object itself carries no closure.
            if (type.BaseType is { } baseType && baseType.SpecialType != SpecialType.System_Object)
            {
                VisitSiteType(baseType, SignatureSitesRule, type.Name + " base", SourceLocation(type));
            }

            foreach (var @interface in type.Interfaces)
            {
                VisitSiteType(@interface, SignatureSitesRule, type.Name + " interface", SourceLocation(type));
            }
        }

        foreach (var nested in type.GetTypeMembers())
        {
            VisitNamedType(nested);
        }
    }

    private void CollectSerializerCallSites()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                if (!IsSerializerEntry(method))
                {
                    continue;
                }

                // Only the generic overloads carry a compile-time closed type. The
                // non-generic (Type runtime-argument) forms are dynamic by construction.
                if (!method.IsGenericMethod)
                {
                    continue;
                }

                foreach (var typeArgument in method.TypeArguments)
                {
                    if (MentionsTypeParameter(typeArgument))
                    {
                        ReportDynamicSite(typeArgument,
                            $"{method.ContainingType.Name}.{method.Name} call site",
                            invocation.GetLocation());
                    }
                    else
                    {
                        VisitSiteType(typeArgument, SerializerCallSitesRule,
                            $"{method.ContainingType.Name}.{method.Name} call site",
                            invocation.GetLocation());
                    }
                }
            }
        }
    }

    private void CollectExplicitTypeOfSites()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var typeofExpression in tree.GetRoot().DescendantNodes().OfType<TypeOfExpressionSyntax>())
            {
                // CM-SRCGEN: typeof payloads inside [JsonSerializable(...)] belong to the
                // AOT declaration form and must never register.
                if (typeofExpression.FirstAncestorOrSelf<AttributeSyntax>() is { } attribute
                    && IsJsonSerializableAttributeName(attribute.Name.ToString()))
                {
                    continue;
                }

                // ENUM-1 walks source symbols; the typeof node's enclosing type may be a
                // source-gen context discovered during that walk. When ENUM-1 is disabled
                // the context set is empty (serializer-only mode) — fall back to a cheap
                // base-chain check on the enclosing named type.
                var enclosing = model.GetEnclosingSymbol(typeofExpression.SpanStart)?.ContainingType;
                if (enclosing is not null && IsOrInsideSourceGenContext(enclosing))
                {
                    continue;
                }

                var type = model.GetTypeInfo(typeofExpression.Type).Type;
                VisitSiteType(type, ExplicitTypeOfRule, "typeof site", typeofExpression.GetLocation());
            }
        }
    }

    /// <summary>
    /// D8 phase-1 consumer surface: the JsonSerializer static entry family plus
    /// JsonSerializerOptions.GetTypeInfo. Shared by CM-ENUM-3 (enumeration) and the
    /// CM-LINK consumer linker (guard insertion) so the two never drift apart.
    /// </summary>
    internal static bool IsSerializerEntry(IMethodSymbol method)
    {
        var containing = method.ContainingType?.ToDisplayString();
        if (containing == JsonSerializerFullName)
        {
            return SerializerMethodNames.Contains(method.Name);
        }

        return containing == JsonSerializerOptionsFullName && method.Name == "GetTypeInfo";
    }

    /// <summary>Whether the type derives (at any depth) from JsonSerializerContext (CM-SRCGEN).</summary>
    internal static bool IsSourceGenContext(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.ToDisplayString() == JsonSerializerContextFullName)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private bool IsOrInsideSourceGenContext(INamedTypeSymbol type)
    {
        var current = type;
        while (current is not null)
        {
            if (_sourceGenContexts.Contains(current))
            {
                return true;
            }

            // ENUM-1-disabled mode: derive the fact directly.
            if (IsSourceGenContext(current))
            {
                return true;
            }

            current = current.ContainingType;
        }

        return false;
    }

    private static bool HasJsonSerializableAttribute(ISymbol symbol, out Location? location)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == JsonSerializableAttributeName)
            {
                location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                return true;
            }
        }

        location = null;
        return false;
    }

    private void ReportSourceGenAttribute(string ownerDisplay, Location? location)
    {
        _diagnostics.Add(new ClosedMetadataDiagnostic(
            ClosedMetadataSeverity.Warning,
            ClosedMetadataRule.SourceGeneration,
            ClosedMetadataCodes.SourceGeneratedContext,
            $"'{ownerDisplay}' carries a [JsonSerializable] AOT declaration; the source-generated surface is " +
            "outside the closed-metadata served set (CM-SRCGEN) and the payload is not registered.",
            location?.SourceTree?.FilePath,
            location?.GetLineSpan().StartLinePosition.Line + 1));
    }

    internal static bool IsJsonSerializableAttributeName(string name)
        => name is "JsonSerializable" or "JsonSerializableAttribute"
            || name.EndsWith(".JsonSerializable", StringComparison.Ordinal)
            || name.EndsWith(".JsonSerializableAttribute", StringComparison.Ordinal);

    private void VisitSiteType(ITypeSymbol? type, string rule, string siteName, Location? siteLocation)
    {
        if (type is null || type is ITypeParameterSymbol)
        {
            return;
        }

        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
        {
            // D5: no Java target is decidable for these shapes — report loudly, never
            // silently skip (design §7 CS2J1203).
            _diagnostics.Add(new ClosedMetadataDiagnostic(
                ClosedMetadataSeverity.Warning,
                ClosedMetadataRule.TokenMapping,
                ClosedMetadataCodes.JavaTargetUndecidable,
                $"Closed type '{type.ToDisplayString()}' at '{siteName}' has a pointer/function-pointer shape " +
                "whose Java target is undecidable in the current symbol shape; it is not registered.",
                siteLocation?.SourceTree?.FilePath,
                siteLocation?.GetLineSpan().StartLinePosition.Line + 1));
            return;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (MentionsTypeParameter(array.ElementType))
            {
                ReportDynamicSite(type, siteName, siteLocation);
                return;
            }

            VisitSiteType(array.ElementType, rule, siteName, siteLocation);
            // Only arrays over a generic construction are closures (e.g. List<short>[]);
            // a plain array (string[]) carries no type-argument erasure problem.
            if (ContainsGenericConstruction(array.ElementType))
            {
                TryAddClosedType(type, rule, siteName, siteLocation);
            }

            return;
        }

        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            if (named.IsDefinition)
            {
                // An open generic definition is not a closure.
                return;
            }

            if (named.TypeArguments.Any(MentionsTypeParameter))
            {
                ReportDynamicSite(type, siteName, siteLocation);
                return;
            }

            _siteHits++;
            TryAddClosedType(type, rule, siteName, siteLocation);
            // Nested closures register independently: an element consumer needs the inner layer too.
            foreach (var argument in named.TypeArguments)
            {
                VisitSiteType(argument, rule, siteName, siteLocation);
            }
        }
    }

    private void TryAddClosedType(ITypeSymbol type, string rule, string siteName, Location? siteLocation)
    {
        var key = ClosedTypeKey.Create(type);
        if (key is null)
        {
            return;
        }

        if (_records.ContainsKey(key))
        {
            return;
        }

        _records[key] = new ClosedTypeRecord(
            key,
            type,
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            OwnershipFilePath(type, siteLocation),
            rule);
    }

    /// <summary>
    /// D6 registration-file ownership prefers the project that declares the closed
    /// construction's own definition: the emitted <c>typeof(X&lt;...&gt;)</c> must compile
    /// there even when the type is internal to that project, and call-site projects
    /// (CM-ENUM-3) reach the registration through their project references. Framework
    /// and other metadata types have no source declaration, so ownership falls back to
    /// the discovery-site file.
    /// </summary>
    private static string OwnershipFilePath(ITypeSymbol type, Location? siteLocation)
    {
        var typeSourcePath = (type as INamedTypeSymbol)?
            .Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath;
        return typeSourcePath ?? siteLocation?.SourceTree?.FilePath ?? string.Empty;
    }

    private void ReportDynamicSite(ITypeSymbol type, string siteName, Location? siteLocation)
    {
        _statistics.DynamicSitesSkipped++;
        _diagnostics.Add(new ClosedMetadataDiagnostic(
            _options.Strict ? ClosedMetadataSeverity.Warning : ClosedMetadataSeverity.Info,
            ClosedMetadataRule.DynamicSite,
            ClosedMetadataCodes.DynamicClosedTypeSite,
            $"Dynamic closed type '{type.ToDisplayString()}' at '{siteName}' has an unbound type-parameter " +
            "argument; A1 declaration-driven registration cannot cover it (CM-DYN, A2 call-site tokens).",
            siteLocation?.SourceTree?.FilePath,
            siteLocation?.GetLineSpan().StartLinePosition.Line + 1));
    }

    private static bool MentionsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => MentionsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(MentionsTypeParameter),
        _ => false,
    };

    /// <summary>True when the type mentions a closed generic construction anywhere (e.g. List&lt;short&gt;[], but not string[]).</summary>
    internal static bool ContainsGenericConstruction(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol named => named.IsGenericType && !named.IsDefinition,
        IArrayTypeSymbol array => ContainsGenericConstruction(array.ElementType),
        _ => false,
    };

    private static Location? SourceLocation(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(location => location.IsInSource);
}
