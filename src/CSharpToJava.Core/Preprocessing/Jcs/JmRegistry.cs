using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>
/// R14 retirement protocol states for Java compensation branches (JM-IDs).
/// <see cref="Frozen"/> is not a sixth phase: it only annotates a partial
/// capability whose constructs have not been covered yet.
/// </summary>
public enum JmRetirementState
{
    Inventory,
    Shadow,
    Enforce,
    Retire,
    Ratchet,
    Frozen,
}

/// <summary>
/// One Java compensation branch registered in the R14 inventory (upgrade plan
/// 附录 H.3). <paramref name="LocationFile"/> is repository-relative with
/// <c>/</c> separators; <paramref name="Anchors"/> are stable method/member
/// name tokens used for file-level reconciliation (line numbers may drift, the
/// JM-ID may not).
/// </summary>
public sealed record JmEntry(
    string JmId,
    string InitialLocation,
    string LocationFile,
    IReadOnlyList<string> Anchors,
    string RequiredCapability,
    string Rule,
    JmRetirementState RetirementState,
    int Hits);

/// <summary>
/// VS-0 step 5 first cut (upgrade plan R14 / 附录 H.3 / 附录 I.1): the
/// authoritative JM-ID inventory plus a read-only, bidirectional file-level
/// reconciliation gate. This registry is data only: it does not change pass
/// behavior, the pass chain or the manifest schema, and <see cref="Validate()"/>
/// never rewrites sources.
/// </summary>
public static class JmRegistry
{
    private static readonly string CallCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Call);

    private static readonly string EvalCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Eval);

    private static readonly string ValueCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Value);

    private static readonly string GenericCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Generic);

    private static readonly string ConversionCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Conversion);

    private static readonly string SymbolCapability =
        JcsCapabilityIds.Format(JcsCapabilityId.Symbol);

    private const string InvocationTransformer =
        "src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs";

    private const string LoopsTransformer =
        "src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Loops.cs";

    private const string ConditionalsTransformer =
        "src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Conditionals.cs";

    private const string StructCloneHelper =
        "src/CSharpToJava.Core/Transformers/StructCloneHelper.cs";

    private const string JsonDeserializeRewrite =
        "src/CSharpToJava.Core/Transformers/JsonDeserializeRewrite.cs";

    private const string CompatCollectionConformance =
        "src/CSharpToJava.Core/Transformers/Utilities/CompatCollectionConformance.cs";

    private const string ArgumentTransformerCoercion =
        "src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.Coercion.cs";

    private const string IdentifierExpressionTransformer =
        "src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs";

    private static readonly JmEntry[] DefaultEntries =
    {
        new(
            "JM-CALL-DELEGATE-INVOKE",
            "InvocationExpressionTransformer.cs:924+ TryRenderImplicitDelegateInvoke（隐式委托调用 → Java SAM 名）",
            InvocationTransformer,
            new[] { "TryRenderImplicitDelegateInvoke", "DelegateInvoke" },
            CallCapability,
            "JE-09",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-CALL-METHOD-GROUP",
            "InvocationExpressionTransformer.cs:827+ TryRenderMethodGroupReference（bare method-group → Java 方法引用）",
            InvocationTransformer,
            new[] { "TryRenderMethodGroupReference", "MethodGroup" },
            CallCapability,
            "JE-09",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-CALL-RAW-RECEIVER-RENDER",
            "IdentifierExpressionTransformer.cs:273,498-502 StripTrailingGenericArguments（H.3 原记于 InvocationExpressionTransformer.cs，实际实现已移至 IdentifierExpressionTransformer）",
            IdentifierExpressionTransformer,
            new[] { "StripTrailingGenericArguments", "raw receiver" },
            CallCapability,
            "JE-09",
            JmRetirementState.Ratchet,
            0),
        new(
            "JM-COERCION-COLLECTION-BRIDGE",
            "ArgumentTransformer.Coercion.cs 方法族 + IListCoercion.cs/IterableCoercion.cs/SpanCoercion.cs（collection/iterable/span 桥接）",
            ArgumentTransformerCoercion,
            new[] { "CoerceIterableSlotType", "TryBuildIterableBridge", "CoerceArrayToCollection" },
            ConversionCapability,
            "JE-03",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-COLLECTION-CONFORMANCE",
            "Utilities/CompatCollectionConformance.cs 方法族（渲染后集合签桥接）",
            CompatCollectionConformance,
            new[] { "ErasureKey", "NormalizeAdd", "WrapIteratorReturn" },
            ConversionCapability,
            "JE-02",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-EVAL-GETTER-PRE-STATEMENT",
            "StatementTransformer.Loops.cs:19-21,106（循环头读改写预语句排布）+ StatementTransformer.ExpressionAndReturn.cs EmitWithPending getter pre-statement 泄漏位",
            LoopsTransformer,
            new[] { "TransformWhileStatement", "DrainPendingStatementsAsPreamble", "RejectConditionPostStatements" },
            EvalCapability,
            "JE-04",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-EVAL-IF-BYREF",
            "StatementTransformer.Conditionals.cs:373-433（短路/out-var/read-back 分支）",
            ConditionalsTransformer,
            new[] { "BuildShortCircuitCondition", "ContainsByRefArgument", "DrainPendingPostStatements" },
            EvalCapability,
            "JE-08",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-EVAL-WHILE-EFFECT",
            "StatementTransformer.Loops.cs:28-37（while(true) 条件线性化）",
            LoopsTransformer,
            new[] { "TransformWhileStatement", "RejectConditionPostStatements" },
            EvalCapability,
            "JE-04",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-GENERATED-EQUALITY-COMPARER",
            "InvocationExpressionTransformer.cs:790-815 TryRenderEqualityComparerCall（tuple 生成物 EqualityComparer<T> 特判）",
            InvocationTransformer,
            new[] { "TryRenderEqualityComparerCall", "EqualityComparer" },
            SymbolCapability,
            "JE-07",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-JSON-DESERIALIZE-TEXT-REWRITE",
            "JsonDeserializeRewrite.cs 全方法族（渲染后文本手术）",
            JsonDeserializeRewrite,
            new[] { "TryRewriteTypedJsonDeserialize", "JsonDeserializeListElement", "TryWrapGenericJsonDeserializeCall" },
            GenericCapability,
            "JE-01",
            JmRetirementState.Inventory,
            0),
        new(
            "JM-VALUE-COPY-SITE",
            "StructCloneHelper.cs 方法族 + Argument/Assignment/Return/Statement 调用点",
            StructCloneHelper,
            new[] { "CloneStructValueIfNeeded", "BuildCloneExpression", "IsUserDefinedStruct" },
            ValueCapability,
            "JE-10",
            JmRetirementState.Inventory,
            0),
    };

    private static readonly Regex RuleShape = new(@"^JE-\d{2}$", RegexOptions.CultureInvariant);

    private static readonly IReadOnlySet<string> CoverageFileSet = DefaultEntries
        .Select(entry => entry.LocationFile)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(file => file, StringComparer.Ordinal)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>All registered JM entries, ordered by <see cref="JmEntry.JmId"/> (ordinal).</summary>
    public static IReadOnlyList<JmEntry> Default { get; } = DefaultEntries
        .OrderBy(entry => entry.JmId, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Distinct repository-relative files covered by <see cref="Default"/>.</summary>
    public static IReadOnlySet<string> CoverageFiles => CoverageFileSet;

    /// <summary>States counted as retired: <see cref="JmRetirementState.Retire"/> and <see cref="JmRetirementState.Ratchet"/>.</summary>
    public static IReadOnlySet<JmRetirementState> RetiredStates { get; } =
        new HashSet<JmRetirementState>
        {
            JmRetirementState.Retire,
            JmRetirementState.Ratchet,
        };

    public static bool IsRetired(JmRetirementState state) => RetiredStates.Contains(state);

    public static bool IsRetired(JmEntry entry) =>
        entry is not null && IsRetired(entry.RetirementState);

    /// <summary>
    /// Validates the default registry against the current repository checkout.
    /// Returns every inconsistency instead of throwing so CI can print them all.
    /// </summary>
    public static IReadOnlyList<string> Validate()
        => Validate(Default, DiscoverRepositoryRoot(), CoverageFiles);

    /// <summary>
    /// Validates an arbitrary JM entry set. When <paramref name="coverageFiles"/>
    /// is null, the covered set is derived from the entries themselves (used by
    /// focused tests that only exercise per-entry checks).
    /// </summary>
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<JmEntry> entries,
        string repositoryRoot,
        IReadOnlySet<string>? coverageFiles = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(repositoryRoot);

        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var fileTextCache = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.JmId))
            {
                errors.Add("JM entry has an empty JM-ID");
            }
            else if (!seenIds.Add(entry.JmId))
            {
                errors.Add($"{entry.JmId}: duplicate JM-ID");
            }

            var label = string.IsNullOrWhiteSpace(entry.JmId) ? "<empty JM-ID>" : entry.JmId;

            if (!JcsCapabilityIds.TryParse(entry.RequiredCapability, out _))
            {
                errors.Add(
                    $"{label}: RequiredCapability '{entry.RequiredCapability}' is not a JCS.<ID> capability");
            }

            if (entry.Rule is null || !RuleShape.IsMatch(entry.Rule))
            {
                errors.Add($"{label}: Rule '{entry.Rule}' does not match ^JE-\\d{{2}}$");
            }

            if (string.IsNullOrWhiteSpace(entry.LocationFile))
            {
                errors.Add($"{label}: LocationFile must not be empty");
                continue;
            }

            if (entry.Anchors is null || entry.Anchors.Count == 0)
            {
                errors.Add($"{label}: Anchors must not be empty");
            }
            else if (entry.Anchors.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{label}: Anchors must not contain empty tokens");
            }

            var relative = entry.LocationFile.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(Path.GetFullPath(repositoryRoot), relative);
            if (!File.Exists(fullPath))
            {
                errors.Add($"{label}: LocationFile '{entry.LocationFile}' does not exist");
                continue;
            }

            if (entry.Anchors is { Count: > 0 })
            {
                if (!fileTextCache.TryGetValue(fullPath, out var text))
                {
                    text = File.ReadAllText(fullPath);
                    fileTextCache[fullPath] = text;
                }

                if (!entry.Anchors.Any(anchor =>
                        !string.IsNullOrWhiteSpace(anchor)
                        && text.Contains(anchor, StringComparison.Ordinal)))
                {
                    errors.Add(
                        $"{label}: no anchor exists in '{entry.LocationFile}' (checked: "
                        + string.Join(", ", entry.Anchors) + ")");
                }
            }
        }

        var covered = coverageFiles
            ?? entries
                .Select(entry => entry.LocationFile)
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .ToHashSet(StringComparer.Ordinal);

        var entryFiles = entries
            .Select(entry => entry.LocationFile)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var file in entryFiles.OrderBy(file => file, StringComparer.Ordinal))
        {
            if (!covered.Contains(file))
            {
                errors.Add($"CoverageFiles is missing '{file}' referenced by JM entries");
            }
        }

        foreach (var file in covered.OrderBy(file => file, StringComparer.Ordinal))
        {
            if (!entryFiles.Contains(file))
            {
                errors.Add($"CoverageFiles lists '{file}' but no JM entry references it");
            }
        }

        return errors;
    }

    private static string DiscoverRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "CSharpToJavaConverter.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                "cannot locate the repository root (CSharpToJavaConverter.slnx) from "
                + AppContext.BaseDirectory);
        }

        return dir.FullName;
    }
}
