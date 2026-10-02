namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 initializer section statistics (design §5-3). The common file counters
/// and <see cref="TempsLifted"/> mirror the family object for per-rule
/// aggregation; the family entry owns the authoritative file accounting.
/// </summary>
public sealed class InitializerStatistics
{
    // ---- Common file fields + family temps (mirrors CollectionExprStatistics) ----

    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;
    public int TempsLifted;

    // ---- Sites ----

    /// <summary>Object-initializer sites (object initializer expression present).</summary>
    public int ObjectInitializerSites;

    /// <summary>Collection-initializer sites (collection initializer expression present).</summary>
    public int CollectionInitializerSites;

    /// <summary>Sites carrying at least one dictionary index item (<c>[k] = v</c>).</summary>
    public int DictionaryIndexInitializerSites;

    /// <summary>Target-typed <c>new()</c> sites (bare + with initializer).</summary>
    public int TargetTypedNewSites;

    /// <summary>Bare target-typed <c>new()</c> sites (no initializer).</summary>
    public int TargetTypedNewBareSites;

    /// <summary>Target-typed <c>new() { … }</c> sites (with initializer).</summary>
    public int TargetTypedNewWithInitializerSites;

    // ---- Member assignments ----

    /// <summary>Total member-assignment items (identifier member + indexer).</summary>
    public int MemberAssignments;

    /// <summary>Property-assignment items (semantic split; M1+).</summary>
    public int MemberPropertyAssignments;

    /// <summary>Field-assignment items (semantic split; M1+).</summary>
    public int MemberFieldAssignments;

    /// <summary>Indexer-assignment items (<c>[k] = v</c>).</summary>
    public int MemberIndexerAssignments;

    // ---- Add call buckets ----

    /// <summary>Total collection-element Add calls.</summary>
    public int AddCalls;

    public int AddSingle;
    public int AddMulti;
    public int AddExtension;
    public int AddParams;
    public int AddFluent;
    public int EnumerableFallback;

    // ---- Nesting ----

    /// <summary>Nested object-creation sites (<c>P = new T { … }</c>).</summary>
    public int NestedCreateSites;

    /// <summary>Nested member-initializer sites (<c>P = { … }</c>).</summary>
    public int NestedMemberInitSites;

    // ---- Context lowering (bucket by context name) ----

    /// <summary>Statement-lowered sites bucketed by <see cref="InitializerContextKind"/> name.</summary>
    public readonly Dictionary<string, int> ContextLowered = new();

    /// <summary>Total context-lowered sites.</summary>
    public int ContextLoweredSites;

    /// <summary>Sites skipped/kept by diagnostic code (code -&gt; count).</summary>
    public readonly Dictionary<string, int> SkippedByReason = new();

    /// <summary>Initializer nodes left in the output without a paired skip diagnostic.</summary>
    public int ResidualInitializerCount;

    public void AddSkip(string code)
    {
        SkippedByReason.TryGetValue(code, out var count);
        SkippedByReason[code] = count + 1;
    }

    public void AddContextLowered(InitializerContextKind kind)
    {
        var name = kind.ToString();
        ContextLowered.TryGetValue(name, out var count);
        ContextLowered[name] = count + 1;
        ContextLoweredSites++;
    }

    public void MergeFrom(InitializerStatistics other)
    {
        FilesScanned += other.FilesScanned;
        FilesTransformed += other.FilesTransformed;
        FilesSkippedClean += other.FilesSkippedClean;
        FilesDirtyNoRewrite += other.FilesDirtyNoRewrite;
        ResidualCount += other.ResidualCount;
        TempsLifted += other.TempsLifted;
        ObjectInitializerSites += other.ObjectInitializerSites;
        CollectionInitializerSites += other.CollectionInitializerSites;
        DictionaryIndexInitializerSites += other.DictionaryIndexInitializerSites;
        TargetTypedNewSites += other.TargetTypedNewSites;
        TargetTypedNewBareSites += other.TargetTypedNewBareSites;
        TargetTypedNewWithInitializerSites += other.TargetTypedNewWithInitializerSites;
        MemberAssignments += other.MemberAssignments;
        MemberPropertyAssignments += other.MemberPropertyAssignments;
        MemberFieldAssignments += other.MemberFieldAssignments;
        MemberIndexerAssignments += other.MemberIndexerAssignments;
        AddCalls += other.AddCalls;
        AddSingle += other.AddSingle;
        AddMulti += other.AddMulti;
        AddExtension += other.AddExtension;
        AddParams += other.AddParams;
        AddFluent += other.AddFluent;
        EnumerableFallback += other.EnumerableFallback;
        NestedCreateSites += other.NestedCreateSites;
        NestedMemberInitSites += other.NestedMemberInitSites;
        ContextLoweredSites += other.ContextLoweredSites;
        ResidualInitializerCount += other.ResidualInitializerCount;
        MergeBuckets(ContextLowered, other.ContextLowered);
        MergeBuckets(SkippedByReason, other.SkippedByReason);
    }

    private static void MergeBuckets(
        Dictionary<string, int> target, Dictionary<string, int> source)
    {
        foreach (var pair in source)
        {
            target.TryGetValue(pair.Key, out var count);
            target[pair.Key] = count + pair.Value;
        }
    }
}