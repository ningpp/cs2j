using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>One remaining [ThreadStatic] attribute found in the rewritten tree, reconciled against plans.</summary>
public sealed class ThreadStaticResidualLocation
{
    public required Location Location { get; init; }

    /// <summary>Display name of the field carrying the residual attribute.</summary>
    public required string FieldDisplayName { get; init; }

    /// <summary>Id of the skipped plan owning this residual (CS2J1201/1202/1203/1204/1205/1290); null when unowned.</summary>
    public string? OwnerPlanId { get; init; }

    /// <summary>Fail code of the owning skipped plan; null for unowned residuals (gate CS2J1220).</summary>
    public string? OwnerCode { get; init; }
}

/// <summary>
/// Structured residual gate result (design §4.2 step 5 / §7.4): every residual
/// attribute that corresponds to a Skipped plan is "owned" and harmless; residual
/// attributes without a matching skipped plan mean the rewrite dropped a field and
/// are reported CS2J1220.
/// </summary>
public sealed class ThreadStaticResidualReport
{
    public required IReadOnlyList<ThreadStaticResidualLocation> Owned { get; init; }
    public required IReadOnlyList<ThreadStaticResidualLocation> Unowned { get; init; }

    public int Total => Owned.Count + Unowned.Count;
    public bool HasUnowned => Unowned.Count > 0;
}

/// <summary>
/// Post-rewrite residual gate. Scans the rewritten compilation for
/// ThreadStaticAttribute instances left on field symbols and reconciles them
/// against the inactive plan set. Matching is by field display name (plans and
/// the rewritten compilation are different symbol worlds, but names survive).
/// </summary>
public static class ThreadStaticResidualScanner
{
    public const string ThreadStaticAttributeMetadataName = "System.ThreadStaticAttribute";

    public static ThreadStaticResidualReport Scan(
        Compilation rewrittenCompilation,
        IReadOnlyCollection<ThreadStaticFieldPlan> plans)
    {
        var skippedByFieldId = plans
            .Where(p => !p.Active)
            .ToDictionary(p => p.Id, p => p.FailCode, StringComparer.Ordinal);

        var attributeSymbol = rewrittenCompilation.GetTypeByMetadataName(ThreadStaticAttributeMetadataName);
        if (attributeSymbol == null)
        {
            return Empty();
        }

        // One attribute syntax may decorate a multi-variable declaration; dedupe
        // locations but let any declarator's skipped plan claim ownership.
        var seenLocations = new HashSet<Location>();
        var owned = new List<ThreadStaticResidualLocation>();
        var unowned = new List<ThreadStaticResidualLocation>();

        foreach (var tree in rewrittenCompilation.SyntaxTrees)
        {
            var model = rewrittenCompilation.GetSemanticModel(tree);
            foreach (var field in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                foreach (var declarator in field.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol fieldSymbol) continue;
                    foreach (var data in fieldSymbol.GetAttributes())
                    {
                        if (data.AttributeClass == null
                            || !SymbolEqualityComparer.Default.Equals(data.AttributeClass, attributeSymbol))
                        {
                            continue;
                        }

                        var location = data.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                            ?? declarator.Identifier.GetLocation();
                        var displayName = fieldSymbol.ToDisplayString();
                        skippedByFieldId.TryGetValue(displayName, out var ownerCode);

                        if (seenLocations.Add(location) == false)
                        {
                            continue;
                        }

                        var residual = new ThreadStaticResidualLocation
                        {
                            Location = location,
                            FieldDisplayName = displayName,
                            OwnerPlanId = ownerCode != null ? displayName : null,
                            OwnerCode = ownerCode,
                        };
                        (ownerCode != null ? owned : unowned).Add(residual);
                    }
                }
            }
        }

        owned.Sort(Compare);
        unowned.Sort(Compare);
        return new ThreadStaticResidualReport { Owned = owned, Unowned = unowned };
    }

    private static int Compare(ThreadStaticResidualLocation a, ThreadStaticResidualLocation b)
    {
        var byFile = string.CompareOrdinal(
            a.Location.SourceTree?.FilePath, b.Location.SourceTree?.FilePath);
        return byFile != 0 ? byFile : a.Location.SourceSpan.Start.CompareTo(b.Location.SourceSpan.Start);
    }

    private static ThreadStaticResidualReport Empty()
        => new()
        {
            Owned = Array.Empty<ThreadStaticResidualLocation>(),
            Unowned = Array.Empty<ThreadStaticResidualLocation>(),
        };
}
