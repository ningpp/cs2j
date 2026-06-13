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

    public void AddPreStatementAllowDuplicate(string statement)
    {
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

    /// <summary>
    /// Remove consecutive duplicate entries from pending post-statements.
    /// Called after processing ternary branches to eliminate duplicate read-backs
    /// when the same out variable appears in both branches.
    /// </summary>
    public void DeduplicatePostStatements()
    {
        if (_pendingPostStatements.Count <= 1) return;
        var seen = new HashSet<string>();
        for (int i = _pendingPostStatements.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(_pendingPostStatements[i]))
                _pendingPostStatements.RemoveAt(i);
        }
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

    /// <summary>
    /// Snapshot the current out-holder allocation counts so they can be restored
    /// before processing the second branch of a ternary expression, ensuring both
    /// branches reuse the same holder names for the same out variables.
    /// </summary>
    public Dictionary<string, int> SnapshotOutHolderCounts()
    {
        return new Dictionary<string, int>(_outHolderAllocCounts);
    }

    /// <summary>
    /// Restore out-holder allocation counts to a previous snapshot.
    /// </summary>
    public void RestoreOutHolderCounts(Dictionary<string, int> snapshot)
    {
        _outHolderAllocCounts.Clear();
        foreach (var kvp in snapshot)
            _outHolderAllocCounts[kvp.Key] = kvp.Value;
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

    // ─── Short-Circuit Context ──────────────────────────────────────

    /// <summary>
    /// Depth counter for short-circuit evaluation contexts (|| or &amp;&amp; right operands).
    /// When &gt; 0, side effects from expressions like *ptr++ must be deferred to execute
    /// only when the short-circuit path is actually taken, not unconditionally before the if.
    /// </summary>
    private int _shortCircuitDepth;

    /// <summary>Whether currently inside a short-circuit operand (|| or &amp;&amp; right side).</summary>
    public bool IsInShortCircuitOperand => _shortCircuitDepth > 0;

    /// <summary>Enter a short-circuit operand context. Call before transforming ||/&amp;&amp; right operand.</summary>
    public void EnterShortCircuitOperand() => _shortCircuitDepth++;

    /// <summary>Exit a short-circuit operand context. Call after transforming ||/&amp;&amp; right operand.</summary>
    public void ExitShortCircuitOperand() => _shortCircuitDepth--;

    // ─── Scope Tracking ────────────────────────────────────────────

    private int _scopeDepth;
    private readonly Dictionary<int, HashSet<string>> _streamVarsByScope = new();

    /// <summary>
    /// Push a new block scope. Call when entering a BlockSyntax.
    /// </summary>
    public void PushScope()
    {
        _scopeDepth++;
    }

    /// <summary>
    /// Pop the current block scope and remove all stream variable names registered at that depth.
    /// Call when leaving a BlockSyntax.
    /// </summary>
    public void PopScope()
    {
        if (_streamVarsByScope.TryGetValue(_scopeDepth, out var vars))
        {
            foreach (var v in vars)
                _flatStreamLocalVariables.Remove(v);
            _streamVarsByScope.Remove(_scopeDepth);
        }
        _scopeDepth--;
    }

    /// <summary>Current block scope depth (0 = method body level).</summary>
    public int ScopeDepth => _scopeDepth;

    // ─── Stream and LINQ State ──────────────────────────────────────

    private readonly HashSet<string> _flatStreamLocalVariables = new();

    /// <summary>
    /// Names of local variables whose initializer is a Java Stream expression.
    /// Scope-aware: variables are automatically removed when their declaring scope is popped.
    /// </summary>
    public HashSet<string> StreamLocalVariables => _flatStreamLocalVariables;

    /// <summary>
    /// Register a stream-typed local variable name at the current scope depth.
    /// The name will be automatically removed when <see cref="PopScope"/> exits this scope.
    /// </summary>
    public void AddStreamVariable(string name)
    {
        _flatStreamLocalVariables.Add(name);
        if (!_streamVarsByScope.TryGetValue(_scopeDepth, out var set))
        {
            set = new HashSet<string>();
            _streamVarsByScope[_scopeDepth] = set;
        }
        set.Add(name);
    }

    /// <summary>
    /// Maps LINQ query 'let' variable names to their inlined Java expressions.
    /// </summary>
    public Dictionary<string, string> QueryLetAliases { get; set; } = new();

    // ─── Runtime Class Parameters For Type-Parameter Arrays ────────

    private readonly Dictionary<string, string> _runtimeClassParametersByTypeParameter = new(StringComparer.Ordinal);

    public void RegisterRuntimeClassParameter(string typeParameterName, string parameterName)
    {
        _runtimeClassParametersByTypeParameter[typeParameterName] = parameterName;
    }

    public bool TryGetRuntimeClassParameter(string typeParameterName, out string parameterName)
        => _runtimeClassParametersByTypeParameter.TryGetValue(typeParameterName, out parameterName!);

    // ─── Lambda Capture Registry ────────────────────────────────────

    /// <summary>
    /// Information about a variable captured by a lambda expression.
    /// </summary>
    public record CaptureInfo(string VarName, string JavaType, bool IsMutable);

    private readonly Dictionary<string, List<CaptureInfo>> _lambdaCaptureRegistry = new();

    /// <summary>
    /// Register captured variables for a lambda identified by <paramref name="lambdaKey"/>.
    /// </summary>
    public void RegisterLambdaCaptures(string lambdaKey, List<CaptureInfo> captures)
    {
        _lambdaCaptureRegistry[lambdaKey] = captures;
    }

    /// <summary>
    /// Try to retrieve previously registered capture info for a lambda.
    /// </summary>
    public bool TryGetLambdaCaptures(string lambdaKey, out List<CaptureInfo> captures)
    {
        return _lambdaCaptureRegistry.TryGetValue(lambdaKey, out captures!);
    }

    // ─── Lambda Capture Holder (Effectively Final) ────────────────

    /// <summary>
    /// Whether the lambda capture pre-scan has been performed for the current method.
    /// Set to true after <c>PreScanLambdaCaptures</c> runs to avoid redundant scanning.
    /// </summary>
    public bool LambdaCapturePreScanDone { get; set; }

    // ─── Label Registry ────────────────────────────────────────────

    public LabelRegistry Labels { get; } = new();

    /// <summary>
    /// Goto analyzer for detecting cross-scope goto patterns that require
    /// state machine transformation. Set during method body pre-scan.
    /// </summary>
    public GotoAnalyzer? GotoAnalyzer { get; set; }

    /// <summary>
    /// Pending holders: registered during pre-scan for variables that are captured by lambdas
    /// and externally reassigned. The holder declaration will be emitted right after the
    /// variable's own declaration, then the mapping is promoted to active.
    /// Key: variable name, Value: (JavaType, HolderName).
    /// </summary>
    private readonly Dictionary<string, (string JavaType, string HolderName)> _pendingLambdaCaptureHolders = new(StringComparer.Ordinal);

    /// <summary>
    /// Active holders: after the holder declaration is emitted, the mapping is promoted here.
    /// <see cref="IdentifierExpressionTransformer"/> checks this to replace variable references
    /// with holder element access (<c>_varName[0]</c>).
    /// Key: variable name, Value: holder name (e.g. "_x").
    /// </summary>
    private readonly Dictionary<string, string> _activeLambdaCaptureHolders = new(StringComparer.Ordinal);

    /// <summary>
    /// Register a variable that needs a lambda capture holder due to external reassignment.
    /// Called during the pre-scan phase (before statement processing).
    /// </summary>
    public void RegisterPendingLambdaCaptureHolder(string varName, string javaType)
    {
        var holderName = $"_{varName}";
        _pendingLambdaCaptureHolders[varName] = (javaType, holderName);
    }

    /// <summary>
    /// Check whether a variable has a pending (not yet declared) lambda capture holder.
    /// </summary>
    public bool HasPendingLambdaCaptureHolder(string varName)
        => _pendingLambdaCaptureHolders.ContainsKey(varName);

    /// <summary>
    /// Try to get the pending holder info for a variable.
    /// </summary>
    public bool TryGetPendingLambdaCaptureHolder(string varName, out string javaType, out string holderName)
    {
        if (_pendingLambdaCaptureHolders.TryGetValue(varName, out var info))
        {
            javaType = info.JavaType;
            holderName = info.HolderName;
            return true;
        }
        javaType = string.Empty;
        holderName = string.Empty;
        return false;
    }

    /// <summary>
    /// Activate a pending holder: move it from pending to active.
    /// Called after the holder declaration is emitted (right after the variable declaration).
    /// Once active, <see cref="TryGetActiveLambdaCaptureHolder"/> returns true and
    /// <see cref="IdentifierExpressionTransformer"/> will replace references.
    /// </summary>
    public void ActivateLambdaCaptureHolder(string varName)
    {
        if (_pendingLambdaCaptureHolders.TryGetValue(varName, out var info))
        {
            _activeLambdaCaptureHolders[varName] = info.HolderName;
            _pendingLambdaCaptureHolders.Remove(varName);
        }
    }

    /// <summary>
    /// Check whether a variable has an active (declared) lambda capture holder.
    /// </summary>
    public bool TryGetActiveLambdaCaptureHolder(string varName, out string holderName)
        => _activeLambdaCaptureHolders.TryGetValue(varName, out holderName!);

    /// <summary>
    /// Directly register an active lambda capture holder without requiring a pending entry.
    /// Used by <see cref="LambdaTransformer"/> when <c>GetMutatedCaptures</c> creates an
    /// array holder for a variable mutated inside the lambda — the holder pre-statement is
    /// emitted before the lambda, and all subsequent references to the variable (after the lambda)
    /// must be replaced with <c>_varName[0]</c> by <see cref="IdentifierExpressionTransformer"/>.
    /// </summary>
    public void RegisterActiveLambdaCaptureHolder(string varName, string holderName)
    {
        _activeLambdaCaptureHolders[varName] = holderName;
    }

    /// <summary>
    /// Check whether a variable has an active (declared) lambda capture holder.
    /// Used by <see cref="LambdaTransformer"/> to skip regex replacement for variables
    /// whose identifiers are already replaced by <see cref="IdentifierExpressionTransformer"/>.
    /// Only active holders guarantee the holder declaration has been emitted.
    /// </summary>
    public bool HasActiveLambdaCaptureHolder(string varName)
        => _activeLambdaCaptureHolders.ContainsKey(varName);

    /// <summary>
    /// Check whether a variable has either a pending or active lambda capture holder.
    /// </summary>
    public bool HasLambdaCaptureHolder(string varName)
        => _pendingLambdaCaptureHolders.ContainsKey(varName) || _activeLambdaCaptureHolders.ContainsKey(varName);

    // ─── Lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Resets all method-level state. Called on entering each new method.
    /// </summary>
    public void Reset(IEnumerable<string>? readOnlyRefStructParamNames = null)
    {
        _flatStreamLocalVariables.Clear();
        _streamVarsByScope.Clear();
        _scopeDepth = 0;
        QueryLetAliases.Clear();
        _activeRefHolders.Clear();
        _refHolderAllocCounts.Clear();
        _pendingPreStatements.Clear();
        _pendingPostStatements.Clear();
        _outHolderAllocCounts.Clear();
        _readOnlyRefStructParams.Clear();
        _shortCircuitDepth = 0;
        _lambdaCaptureRegistry.Clear();
        _pendingLambdaCaptureHolders.Clear();
        _activeLambdaCaptureHolders.Clear();
        _runtimeClassParametersByTypeParameter.Clear();
        LambdaCapturePreScanDone = false;
        Labels.Clear();
        GotoAnalyzer = null;

        if (readOnlyRefStructParamNames != null)
        {
            foreach (var name in readOnlyRefStructParamNames)
            {
                _readOnlyRefStructParams.Add(name);
            }
        }
    }
}
