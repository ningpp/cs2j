namespace CSharpToJava.Core.Preprocessing;

/// <summary>
/// Validated, topologically ordered catalog of preprocess passes. Constructed once
/// at CLI startup from the full descriptor set (implemented passes + stubs).
/// Ordering is derived from <see cref="PassDescriptor.RunAfterIds"/>; ties keep
/// declaration order, so the result is deterministic.
/// </summary>
public sealed class PassRegistry
{
    private readonly Dictionary<string, PassDescriptor> _byId;
    private readonly IReadOnlyList<PassDescriptor> _ordered;

    private PassRegistry(
        Dictionary<string, PassDescriptor> byId,
        IReadOnlyList<PassDescriptor> ordered)
    {
        _byId = byId;
        _ordered = ordered;
    }

    /// <summary>All passes in execution order (implemented and stubs).</summary>
    public IReadOnlyList<PassDescriptor> Ordered => _ordered;

    /// <summary>Enumerate descriptors with the given status.</summary>
    public IEnumerable<PassDescriptor> WhereStatus(PassStatus status)
        => _ordered.Where(p => p.Status == status);

    /// <summary>Look up a descriptor by id; returns false for unknown ids.</summary>
    public bool TryGet(string id, out PassDescriptor descriptor)
        => _byId.TryGetValue(id, out descriptor!);

    /// <summary>Look up a descriptor by id, throwing for unknown ids.</summary>
    public PassDescriptor Require(string id)
        => _byId.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Unknown preprocess pass id: '{id}'.");

    /// <summary>
    /// Validate and order the supplied descriptors.
    /// Throws <see cref="PassRegistryException"/> for duplicate ids, duplicate
    /// intermediate directory names, RunAfter references to unknown ids, or cycles.
    /// </summary>
    public static PassRegistry Create(IEnumerable<PassDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var list = descriptors.ToList();
        var errors = new List<string>();

        var byId = new Dictionary<string, PassDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in list)
        {
            if (!byId.TryAdd(descriptor.Id, descriptor))
            {
                errors.Add($"Duplicate pass id: '{descriptor.Id}'.");
            }
        }

        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var descriptor in list)
        {
            if (!dirs.TryAdd(descriptor.IntermediateDirectoryName, descriptor.Id))
            {
                errors.Add(
                    $"Duplicate intermediate directory '{descriptor.IntermediateDirectoryName}' " +
                    $"used by passes '{dirs[descriptor.IntermediateDirectoryName]}' and '{descriptor.Id}'.");
            }
        }

        foreach (var descriptor in list)
        {
            foreach (var runAfter in descriptor.RunAfterIds)
            {
                if (!byId.ContainsKey(runAfter))
                {
                    errors.Add(
                        $"Pass '{descriptor.Id}' declares RunAfter '{runAfter}', which is not registered.");
                }
            }
        }

        var ordered = TopologicalSort(list, byId, errors);

        if (errors.Count > 0)
        {
            throw new PassRegistryException(
                "Invalid preprocess pass registry:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
        }

        return new PassRegistry(byId, ordered);
    }

    private static IReadOnlyList<PassDescriptor> TopologicalSort(
        List<PassDescriptor> list,
        Dictionary<string, PassDescriptor> byId,
        List<string> errors)
    {
        // In-degree over declared edges; declaration order is the deterministic tiebreak.
        var remaining = new List<PassDescriptor>(list);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PassDescriptor>(list.Count);

        while (remaining.Count > 0)
        {
            PassDescriptor? next = null;
            foreach (var candidate in remaining)
            {
                var dependenciesSatisfied = candidate.RunAfterIds.All(
                    id => byId.ContainsKey(id) && emitted.Contains(id));
                if (dependenciesSatisfied)
                {
                    next = candidate;
                    break;
                }
            }

            if (next == null)
            {
                // Every remaining pass has an unsatisfied RunAfter → cycle.
                var cycle = string.Join(", ", remaining.Select(p => "'" + p.Id + "'"));
                errors.Add($"Cycle detected in pass RunAfter graph among: {cycle}.");
                break;
            }

            remaining.Remove(next);
            emitted.Add(next.Id);
            result.Add(next);
        }

        return result;
    }
}

/// <summary>Raised when a <see cref="PassRegistry"/> fails validation.</summary>
public sealed class PassRegistryException : Exception
{
    public PassRegistryException(string message) : base(message)
    {
    }
}
