namespace CSharpToJava.Core.Context;

/// <summary>
/// Method-level mutable state used during conversion.
/// Tracks pre/post statements, ref/out holder allocations, stream local variables,
/// and LINQ let-alias mappings. Cleared on entering each new method.
/// </summary>
public class MethodConversionState
{
    // ─── Pre/Post Statements ─────────────────────────────────────────

    private readonly List<string> _pendingPreStatements = new();

    public void AddPreStatement(string statement)
    {
        // Avoid duplicate pre-statements (e.g. closure variable hoisting for the same variable)
        if (!_pendingPreStatements.Contains(statement))
            _pendingPreStatements.Add(statement);
    }

    public IReadOnlyList<string> DrainPreStatements()
    {
        var result = _pendingPreStatements.ToList();
        _pendingPreStatements.Clear();
        return result;
    }

    public bool HasPendingPreStatements => _pendingPreStatements.Count > 0;

    private readonly List<string> _pendingPostStatements = new();

    public void AddPostStatement(string statement)
    {
        _pendingPostStatements.Add(statement);
    }

    public IReadOnlyList<string> DrainPostStatements()
    {
        var result = _pendingPostStatements.ToList();
        _pendingPostStatements.Clear();
        _activeRefHolders.Clear();
        return result;
    }

    public bool HasPendingPostStatements => _pendingPostStatements.Count > 0;

    // ─── Ref/Out Holder Management ───────────────────────────────────

    private readonly Dictionary<string, string> _activeRefHolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _refHolderAllocCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _outHolderAllocCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readOnlyRefStructParams = new(StringComparer.Ordinal);

    public bool IsReadOnlyRefStructParam(string paramName)
        => _readOnlyRefStructParams.Contains(paramName);

    public string AllocateOutHolderName(string varName)
    {
        var key = $"_{varName}Holder";
        var count = _outHolderAllocCounts.GetValueOrDefault(key, 0) + 1;
        _outHolderAllocCounts[key] = count;
        return $"{key}{count}";
    }

    public bool TryGetActiveRefHolder(string varName, out string holderName)
    {
        return _activeRefHolders.TryGetValue(varName, out holderName!);
    }

    public void SetActiveRefHolder(string varName, string holderName)
    {
        _activeRefHolders[varName] = holderName;
    }

    public string AllocateRefHolderName(string varName)
    {
        var count = _refHolderAllocCounts.GetValueOrDefault(varName, 0);
        _refHolderAllocCounts[varName] = count + 1;
        var holderName = count == 0 ? $"_{varName}Ref" : $"_{varName}Ref{count + 1}";
        _activeRefHolders[varName] = holderName;
        return holderName;
    }

    // ─── Stream and LINQ State ──────────────────────────────────────

    /// <summary>
    /// Names of local variables whose initializer is a Java Stream expression.
    /// </summary>
    public HashSet<string> StreamLocalVariables { get; } = new();

    /// <summary>
    /// Maps LINQ query 'let' variable names to their inlined Java expressions.
    /// </summary>
    public Dictionary<string, string> QueryLetAliases { get; set; } = new();

    // ─── Lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Resets all method-level state. Called on entering each new method.
    /// </summary>
    public void Reset(IEnumerable<string>? readOnlyRefStructParamNames = null)
    {
        StreamLocalVariables.Clear();
        QueryLetAliases.Clear();
        _activeRefHolders.Clear();
        _refHolderAllocCounts.Clear();
        _pendingPreStatements.Clear();
        _pendingPostStatements.Clear();
        _outHolderAllocCounts.Clear();
        _readOnlyRefStructParams.Clear();

        if (readOnlyRefStructParamNames != null)
        {
            foreach (var name in readOnlyRefStructParamNames)
            {
                _readOnlyRefStructParams.Add(name);
            }
        }
    }
}
