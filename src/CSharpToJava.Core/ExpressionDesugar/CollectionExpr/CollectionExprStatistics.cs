namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// C36 section statistics (design §6 end list). The five common file counters
/// and <see cref="TempsLifted"/> mirror the family object for per-rule
/// aggregation; the family entry owns the authoritative file accounting in M0.
/// </summary>
public sealed class CollectionExprStatistics
{
    // ---- Common five file fields + family temps ----

    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;
    public int TempsLifted;

    // ---- Sites ----

    /// <summary>Total collection expression sites (CS2J0570).</summary>
    public int CollectionSites;

    // ByTargetKind — six buckets.
    public int ByTargetArray;
    public int ByTargetSpan;
    public int ByTargetMutableConcrete;
    public int ByTargetInterface;
    public int ByTargetCollectionBuilder;
    public int ByTargetUnknown;

    // ---- Spread (SpreadElements total + expansion buckets) ----

    public int SpreadElements;
    public int SpreadAddRange;
    public int SpreadSpanForeach;
    public int SpreadEnumerableForeach;

    // ---- Site shapes ----

    public int EmptySites;
    public int NestedSites;
    public int SpanArraySites;
    public int MutableConstructSites;
    public int BuilderSites;

    /// <summary>
    /// M2 CE-4: constructible interface targets lowered to List&lt;T&gt;
    /// (CS2J0574 bucket; ByTargetInterface counts the target kind, this
    /// counts sites that actually received the concrete form).
    /// </summary>
    public int InterfaceConstructSites;

    /// <summary>
    /// M2 CE-10: [CollectionBuilder] sites emitted via a uniquely resolved
    /// factory call (the 0580-retained bucket is tracked in SkippedByReason).
    /// </summary>
    public int BuilderFactoryEmitted;

    public int StatementLiftedSites;

    /// <summary>Sites skipped/kept by diagnostic code (code -> count).</summary>
    public readonly Dictionary<string, int> SkippedByReason = new();

    /// <summary>
    /// M3 CE-12: spread elements carried by skip-paired (preserved)
    /// collection sites. A preserved site with N spreads leaves 1 + N
    /// residual nodes on disk; the CLI whole-tree gate pairs the N spread
    /// nodes through this counter alongside the per-site skip counts.
    /// </summary>
    public int SkippedSpreadElements;

    // ---- CE-GUARD (M3 inventory) ----

    public int GuardStaticAbstractDeclarations;
    public int GuardStaticAbstractCallSites;
    public int GuardGenericAttributeDeclarations;
    public int GuardGenericAttributeApplications;
    public int GuardScopedParameterSites;
    public int GuardScopedLocalSites;

    /// <summary>Collection/spread nodes left in the output without a paired skip diagnostic.</summary>
    public int ResidualCollectionCount;

    public void AddTargetKind(TargetKind kind)
    {
        switch (kind)
        {
            case TargetKind.Array: ByTargetArray++; break;
            case TargetKind.Span: ByTargetSpan++; break;
            case TargetKind.MutableConcrete: ByTargetMutableConcrete++; break;
            case TargetKind.Interface: ByTargetInterface++; break;
            case TargetKind.CollectionBuilder: ByTargetCollectionBuilder++; break;
            default: ByTargetUnknown++; break;
        }
    }

    public void AddSkip(string code)
    {
        SkippedByReason.TryGetValue(code, out var count);
        SkippedByReason[code] = count + 1;
    }

    public void MergeFrom(CollectionExprStatistics other)
    {
        FilesScanned += other.FilesScanned;
        FilesTransformed += other.FilesTransformed;
        FilesSkippedClean += other.FilesSkippedClean;
        FilesDirtyNoRewrite += other.FilesDirtyNoRewrite;
        ResidualCount += other.ResidualCount;
        TempsLifted += other.TempsLifted;
        CollectionSites += other.CollectionSites;
        ByTargetArray += other.ByTargetArray;
        ByTargetSpan += other.ByTargetSpan;
        ByTargetMutableConcrete += other.ByTargetMutableConcrete;
        ByTargetInterface += other.ByTargetInterface;
        ByTargetCollectionBuilder += other.ByTargetCollectionBuilder;
        ByTargetUnknown += other.ByTargetUnknown;
        SpreadElements += other.SpreadElements;
        SpreadAddRange += other.SpreadAddRange;
        SpreadSpanForeach += other.SpreadSpanForeach;
        SpreadEnumerableForeach += other.SpreadEnumerableForeach;
        EmptySites += other.EmptySites;
        NestedSites += other.NestedSites;
        SpanArraySites += other.SpanArraySites;
        MutableConstructSites += other.MutableConstructSites;
        BuilderSites += other.BuilderSites;
        InterfaceConstructSites += other.InterfaceConstructSites;
        BuilderFactoryEmitted += other.BuilderFactoryEmitted;
        StatementLiftedSites += other.StatementLiftedSites;
        SkippedSpreadElements += other.SkippedSpreadElements;
        GuardStaticAbstractDeclarations += other.GuardStaticAbstractDeclarations;
        GuardStaticAbstractCallSites += other.GuardStaticAbstractCallSites;
        GuardGenericAttributeDeclarations += other.GuardGenericAttributeDeclarations;
        GuardGenericAttributeApplications += other.GuardGenericAttributeApplications;
        GuardScopedParameterSites += other.GuardScopedParameterSites;
        GuardScopedLocalSites += other.GuardScopedLocalSites;
        ResidualCollectionCount += other.ResidualCollectionCount;
        foreach (var pair in other.SkippedByReason)
        {
            SkippedByReason.TryGetValue(pair.Key, out var count);
            SkippedByReason[pair.Key] = count + pair.Value;
        }
    }
}
