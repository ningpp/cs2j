using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Whole-compilation closed-type enumerator (design D4 / CM-ENUM rules, M3 scope).
/// Symbol-driven only: member signatures (CM-ENUM-1) from the full compilation symbol
/// table plus explicit <c>typeof</c> sites (CM-ENUM-2; M3 covers typeof only — the
/// default/cast/new site forms are intentionally out of the M3 set, see the M3
/// report, not TODO-commented). It reads only the symbol shapes the bound compilation
/// provides (post generic-deepen chain position, D5) and never guesses a mapping:
/// shapes with no decidable Java target (pointer/function-pointer) raise CS2J1203,
/// and arguments mentioning an unbound type parameter raise CS2J1210 (CM-DYN) and are
/// counted but never registered. Results are deduplicated compilation-wide and
/// ordered by the normalized key (CM-REG).
/// </summary>
public sealed class ClosedMetadataCollector
{
    /// <summary>CM-ENUM-1: closed types in field/property/event/method signatures.</summary>
    public const string DeclaredSitesRule = "CM-ENUM-1";

    /// <summary>CM-ENUM-2: explicit typeof closed-type sites (M3 scope).</summary>
    public const string ExplicitTypeOfRule = "CM-ENUM-2";

    private readonly Compilation _compilation;
    private readonly ClosedMetadataOptions _options;
    private readonly List<ClosedMetadataDiagnostic> _diagnostics = new();
    private readonly Dictionary<ClosedTypeKey, ClosedTypeRecord> _records = new();
    private readonly ClosedMetadataStatistics _statistics = new();
    private int _siteHits;

    public ClosedMetadataCollector(Compilation compilation, ClosedMetadataOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    public ClosedMetadataAnalysis Analyze()
    {
        if (_options.RegisterDeclaredClosedTypes)
        {
            VisitNamespace(_compilation.SourceModule.GlobalNamespace);
        }

        if (_options.RegisterExplicitTypeOfSites)
        {
            CollectExplicitTypeOfSites();
        }

        var records = _records.Values
            .OrderBy(record => record.Key.SortText, StringComparer.Ordinal)
            .ToList();
        _statistics.ClosedTypesEnumerated = _siteHits;
        _statistics.Deduplicated = _siteHits - records.Count;

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
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
            {
                continue;
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

        foreach (var nested in type.GetTypeMembers())
        {
            VisitNamedType(nested);
        }
    }

    private void CollectExplicitTypeOfSites()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var typeofExpression in tree.GetRoot().DescendantNodes().OfType<TypeOfExpressionSyntax>())
            {
                var type = model.GetTypeInfo(typeofExpression.Type).Type;
                VisitSiteType(type, ExplicitTypeOfRule, "typeof site", typeofExpression.GetLocation());
            }
        }
    }

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
            siteLocation?.SourceTree?.FilePath ?? string.Empty,
            rule);
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

    private static bool ContainsGenericConstruction(ITypeSymbol type) => type switch
    {
        INamedTypeSymbol named => named.IsGenericType && !named.IsDefinition,
        IArrayTypeSymbol array => ContainsGenericConstruction(array.ElementType),
        _ => false,
    };

    private static Location? SourceLocation(ISymbol symbol)
        => symbol.Locations.FirstOrDefault(location => location.IsInSource);
}
