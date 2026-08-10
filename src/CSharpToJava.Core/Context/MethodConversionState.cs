namespace CSharpToJava.Core.Context;

using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

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
        // Promote any remaining branch-scoped pre-statements to the regular list.
        // This handles object initializers at statement level (not inside ternary).
        // Inside ternaries, the ternary handler drains branch-scoped pre-statements
        // first, so there's nothing to promote here.
        if (_branchScopedPreStatements.Count > 0)
        {
            foreach (var s in _branchScopedPreStatements)
            {
                if (!_pendingPreStatements.Contains(s))
                    _pendingPreStatements.Add(s);
            }
            _branchScopedPreStatements.Clear();
        }

        var result = _pendingPreStatements.ToList();
        _pendingPreStatements.Clear();
        return result;
    }

    public bool HasPendingPreStatements => _pendingPreStatements.Count > 0 || _branchScopedPreStatements.Count > 0;

    // ─── Branch-Scoped Pre-Statements ──────────────────────────────────

    /// <summary>
    /// Pre-statements that must be emitted inside a conditional branch (if-else)
    /// rather than hoisted to the enclosing statement level. Used by object
    /// initializer extraction inside ternary expressions to avoid calling
    /// side-effectful code unconditionally.
    /// </summary>
    private readonly List<string> _branchScopedPreStatements = new();

    public void AddBranchScopedPreStatement(string statement)
    {
        _branchScopedPreStatements.Add(statement);
    }

    public IReadOnlyList<string> DrainBranchScopedPreStatements()
    {
        var result = _branchScopedPreStatements.ToList();
        _branchScopedPreStatements.Clear();
        return result;
    }

    public bool HasBranchScopedPreStatements => _branchScopedPreStatements.Count > 0;

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
        var escaped = JavaNaming.EscapeJavaKeyword(varName);
        var key = $"_{escaped}Holder";
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
        var escaped = JavaNaming.EscapeJavaKeyword(varName);
        var count = _refHolderAllocCounts.GetValueOrDefault(escaped, 0);
        _refHolderAllocCounts[escaped] = count + 1;
        var holderName = count == 0 ? $"_{escaped}Ref" : $"_{escaped}Ref{count + 1}";
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

    /// <summary>
    /// Deferred side effects from expressions inside short-circuit operands.
    /// Key: temp variable name (e.g. "_ptrPost3"), Value: side effect statement (e.g. "curPos = curPos.asSlice(2)").
    /// These are consumed by the parent expression (e.g. TransformPointerIndirection) to inline them
    /// into the short-circuit path.
    /// </summary>
    private readonly Dictionary<string, string> _shortCircuitDeferredSideEffects = new(StringComparer.Ordinal);

    /// <summary>Register a deferred side effect for a temp variable in a short-circuit context.</summary>
    public void RegisterShortCircuitDeferredSideEffect(string tempVar, string sideEffect)
        => _shortCircuitDeferredSideEffects[tempVar] = sideEffect;

    /// <summary>Try to consume a deferred side effect for a temp variable.</summary>
    public bool TryConsumeShortCircuitDeferredSideEffect(string tempVar, out string sideEffect)
        => _shortCircuitDeferredSideEffects.Remove(tempVar, out sideEffect!);

    // ─── Scope Tracking ────────────────────────────────────────────

    private int _scopeDepth;
    private readonly Dictionary<int, HashSet<string>> _streamVarsByScope = new();

    /// <summary>
    /// Push a new block scope. Call when entering a BlockSyntax.
    /// </summary>
    public void PushScope()
    {
        _scopeDepth++;
        _activeLambdaCaptureHolderFrames.Add(new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Pop the current block scope and remove all stream variable names and lambda capture
    /// holders registered at that depth. Call when leaving a BlockSyntax.
    /// </summary>
    public void PopScope()
    {
        if (_streamVarsByScope.TryGetValue(_scopeDepth, out var vars))
        {
            foreach (var v in vars)
                _flatStreamLocalVariables.Remove(v);
            _streamVarsByScope.Remove(_scopeDepth);
        }
        if (_activeLambdaCaptureHolderFrames.Count > 0)
            _activeLambdaCaptureHolderFrames.RemoveAt(_activeLambdaCaptureHolderFrames.Count - 1);
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

    // ─── Renamed Locals (Lambda Shadowing) ────────────────────────

    /// <summary>
    /// Locals declared inside a lambda body whose name collides with a variable
    /// visible from the enclosing scope (method parameter/local or enclosing lambda
    /// parameter/local). C# allows shadowing across lambda boundaries, Java forbids
    /// it, so the inner declaration must be renamed and every reference to it
    /// (resolved by symbol) emitted under the new name.
    /// </summary>
    private readonly Dictionary<ISymbol, string> _renamedLocals = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// Register that <paramref name="symbol"/> (a local declared inside a lambda body)
    /// is emitted as <paramref name="javaName"/> instead of its C# name.
    /// </summary>
    public void RegisterLocalRename(ILocalSymbol symbol, string javaName)
    {
        _renamedLocals[symbol] = javaName;
    }

    /// <summary>
    /// Look up the effective Java name for a local that was renamed due to
    /// cross-lambda shadowing. Returns false when the local was not renamed.
    /// </summary>
    public bool TryGetLocalRename(ISymbol? symbol, out string javaName)
    {
        if (symbol != null && _renamedLocals.TryGetValue(symbol, out javaName!))
            return true;
        javaName = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns <paramref name="fallbackName"/> unless the local was renamed due to
    /// cross-lambda shadowing, in which case the registered Java name is returned.
    /// </summary>
    public string GetEffectiveLocalJavaName(ISymbol? symbol, string fallbackName)
        => TryGetLocalRename(symbol, out var renamed) ? renamed : fallbackName;

    // ─── Label Registry ────────────────────────────────────────────

    public LabelRegistry Labels { get; } = new();

    /// <summary>
    /// Goto analyzer for detecting cross-scope goto patterns that require
    /// state machine transformation. Set during method body pre-scan.
    /// </summary>
    public GotoAnalyzer? GotoAnalyzer { get; set; }

    // ─── Goto-to-post-loop labels ───────────────────────────────────

    private readonly Stack<(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax Loop, string JavaLabel)> _postLoopGotoLabels = new();

    public void PushPostLoopGotoLabel(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax loop, string javaLabel)
    {
        _postLoopGotoLabels.Push((loop, javaLabel));
    }

    public void PopPostLoopGotoLabel(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax loop)
    {
        if (_postLoopGotoLabels.Count > 0 && ReferenceEquals(_postLoopGotoLabels.Peek().Loop, loop))
        {
            _postLoopGotoLabels.Pop();
        }
    }

    public bool TryGetPostLoopGotoLabel(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax loop, out string javaLabel)
    {
        foreach (var entry in _postLoopGotoLabels)
        {
            if (ReferenceEquals(entry.Loop, loop))
            {
                javaLabel = entry.JavaLabel;
                return true;
            }
        }

        javaLabel = "";
        return false;
    }

    /// <summary>
    /// Pending holders: registered during pre-scan for variables that are captured by lambdas
    /// and externally reassigned. The holder declaration will be emitted right after the
    /// variable's own declaration, then the mapping is promoted to active.
    /// Stored as a list so that multiple locals with the same name in different scopes can each
    /// have their own holder. Each entry records the declaring syntax so activation can match
    /// the exact local variable and not just the name.
    /// </summary>
    private readonly List<PendingLambdaCaptureHolder> _pendingLambdaCaptureHolders = new();

    public record PendingLambdaCaptureHolder(string VarName, string JavaType, string HolderName, SyntaxNode DeclaringSyntax);

    /// <summary>
    /// Active holders: after the holder declaration is emitted, the mapping is promoted here.
    /// <see cref="IdentifierExpressionTransformer"/> checks this to replace variable references
    /// with holder element access (<c>_varName[0]</c>).
    /// Organized as a stack of scopes so that shadowed locals in nested blocks do not reuse
    /// a holder belonging to an outer scope. The innermost scope is at the end of the list.
    /// </summary>
    private readonly List<Dictionary<string, string>> _activeLambdaCaptureHolderFrames = new();

    /// <summary>
    /// Register a variable that needs a lambda capture holder due to external reassignment.
    /// Called during the pre-scan phase (before statement processing).
    /// </summary>
    public void RegisterPendingLambdaCaptureHolder(string varName, string javaType, SyntaxNode declaringSyntax)
    {
        var escaped = JavaNaming.EscapeJavaKeyword(varName);
        var holderName = $"_{escaped}";
        _pendingLambdaCaptureHolders.Add(new PendingLambdaCaptureHolder(varName, javaType, holderName, declaringSyntax));
    }

    /// <summary>
    /// Check whether a variable has a pending (not yet declared) lambda capture holder.
    /// </summary>
    public bool HasPendingLambdaCaptureHolder(string varName)
        => _pendingLambdaCaptureHolders.Any(h => h.VarName == varName);

    /// <summary>
    /// Get all pending lambda capture holders declared by method parameters. These are
    /// emitted at the start of the method body (there is no parameter declaration
    /// statement inside the body to attach them to).
    /// </summary>
    public IReadOnlyList<PendingLambdaCaptureHolder> GetPendingParameterLambdaCaptureHolders()
        => _pendingLambdaCaptureHolders
            .Where(h => h.DeclaringSyntax is Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax)
            .ToList();

    /// <summary>
    /// Try to get the pending holder info for a specific variable declaration.
    /// </summary>
    public bool TryGetPendingLambdaCaptureHolder(string varName, SyntaxNode declaringSyntax, out string javaType, out string holderName)
    {
        foreach (var holder in _pendingLambdaCaptureHolders)
        {
            if (holder.VarName == varName && holder.DeclaringSyntax == declaringSyntax)
            {
                javaType = holder.JavaType;
                holderName = holder.HolderName;
                return true;
            }
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
    public void ActivateLambdaCaptureHolder(string varName, SyntaxNode declaringSyntax)
    {
        for (int i = 0; i < _pendingLambdaCaptureHolders.Count; i++)
        {
            if (_pendingLambdaCaptureHolders[i].VarName == varName &&
                _pendingLambdaCaptureHolders[i].DeclaringSyntax == declaringSyntax)
            {
                GetCurrentLambdaCaptureHolderFrame()[varName] = _pendingLambdaCaptureHolders[i].HolderName;
                _pendingLambdaCaptureHolders.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>
    /// Check whether a variable has an active (declared) lambda capture holder in the
    /// current scope or any enclosing scope.
    /// </summary>
    public bool TryGetActiveLambdaCaptureHolder(string varName, out string holderName)
    {
        for (int i = _activeLambdaCaptureHolderFrames.Count - 1; i >= 0; i--)
        {
            if (_activeLambdaCaptureHolderFrames[i].TryGetValue(varName, out holderName!))
                return true;
        }

        holderName = null!;
        return false;
    }

    /// <summary>
    /// Directly register an active lambda capture holder without requiring a pending entry.
    /// Used by <see cref="LambdaTransformer"/> when <c>GetMutatedCaptures</c> creates an
    /// array holder for a variable mutated inside the lambda — the holder pre-statement is
    /// emitted before the lambda, and all subsequent references to the variable (after the lambda)
    /// must be replaced with <c>_varName[0]</c> by <see cref="IdentifierExpressionTransformer"/>.
    /// </summary>
    public void RegisterActiveLambdaCaptureHolder(string varName, string holderName)
    {
        GetCurrentLambdaCaptureHolderFrame()[varName] = holderName;
    }

    private Dictionary<string, string> GetCurrentLambdaCaptureHolderFrame()
    {
        if (_activeLambdaCaptureHolderFrames.Count == 0)
            _activeLambdaCaptureHolderFrames.Add(new Dictionary<string, string>(StringComparer.Ordinal));
        return _activeLambdaCaptureHolderFrames[^1];
    }

    /// <summary>
    /// Check whether a variable has an active (declared) lambda capture holder in the
    /// current scope or any enclosing scope.
    /// Used by <see cref="LambdaTransformer"/> to skip regex replacement for variables
    /// whose identifiers are already replaced by <see cref="IdentifierExpressionTransformer"/>.
    /// Only active holders guarantee the holder declaration has been emitted.
    /// </summary>
    public bool HasActiveLambdaCaptureHolder(string varName)
    {
        for (int i = _activeLambdaCaptureHolderFrames.Count - 1; i >= 0; i--)
        {
            if (_activeLambdaCaptureHolderFrames[i].ContainsKey(varName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check whether a variable has either a pending or active lambda capture holder.
    /// </summary>
    public bool HasLambdaCaptureHolder(string varName)
    {
        if (_pendingLambdaCaptureHolders.Any(h => h.VarName == varName))
            return true;

        for (int i = _activeLambdaCaptureHolderFrames.Count - 1; i >= 0; i--)
        {
            if (_activeLambdaCaptureHolderFrames[i].ContainsKey(varName))
                return true;
        }

        return false;
    }

    // ─── Lifecycle ──────────────────────────────────────────────────

    /// <summary>
    // ─── Hoisted Local Functions ────────────────────────────────────

    private readonly List<LocalFunctionStatementSyntax> _hoistedLocalFunctions = new();

    /// <summary>
    /// Registers a C# local function for hoisting to class level (Java has no local functions).
    /// Deduplicated by syntax reference so pre-scan and inline paths agree.
    /// </summary>
    public void RegisterHoistedLocalFunction(LocalFunctionStatementSyntax localFunction)
    {
        if (!_hoistedLocalFunctions.Any(f => ReferenceEquals(f, localFunction)))
            _hoistedLocalFunctions.Add(localFunction);
    }

    /// <summary>
    /// Returns and clears all registered hoisted local functions.
    /// </summary>
    public List<LocalFunctionStatementSyntax> DrainHoistedLocalFunctions()
    {
        var result = _hoistedLocalFunctions.ToList();
        _hoistedLocalFunctions.Clear();
        return result;
    }

    // ─── Non-hoisted (capturing) Local Functions ─────────────────────

    private readonly HashSet<string> _nonHoistedLocalFunctions = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a capturing local function that cannot be hoisted to class level.
    /// Call sites must not emit method calls for it (the declaration becomes a TODO).
    /// </summary>
    public void RegisterNonHoistedLocalFunction(string name)
    {
        _nonHoistedLocalFunctions.Add(name);
    }

    public bool IsNonHoistedLocalFunction(string name)
    {
        return _nonHoistedLocalFunctions.Contains(name);
    }

    /// <summary>Captured variable of a hoisted local function.</summary>
    public sealed record CapturedLocal(ISymbol Symbol, string Name, string JavaType, string ParamName);

    private readonly Dictionary<string, List<CapturedLocal>> _hoistedLocalFunctionCaptures = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the enclosing-scope captures of a hoisted local function so the hoisted
    /// method receives them as parameters and call sites pass them as arguments.
    /// </summary>
    public void RegisterHoistedLocalFunctionCaptures(string name, List<CapturedLocal> captures)
    {
        if (captures.Count > 0)
        {
            _hoistedLocalFunctionCaptures[name] = captures;
        }
    }

    /// <summary>Gets the captures of a hoisted local function by its C# name (call-site key).</summary>
    public List<CapturedLocal>? GetHoistedLocalFunctionCaptures(string name)
    {
        return _hoistedLocalFunctionCaptures.TryGetValue(name, out var captures) ? captures : null;
    }

    private readonly Dictionary<string, List<string?>> _hoistedLocalFunctionDefaults = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the Java literal default values of a hoisted local function's regular
    /// parameters (null entry = no default). Call sites pad omitted trailing arguments
    /// from this list, since hoisted signatures keep no parameter defaults.
    /// </summary>
    public void RegisterHoistedLocalFunctionDefaults(string name, List<string?> defaults)
    {
        _hoistedLocalFunctionDefaults[name] = defaults;
    }

    public List<string?>? GetHoistedLocalFunctionDefaults(string name)
    {
        return _hoistedLocalFunctionDefaults.TryGetValue(name, out var defaults) ? defaults : null;
    }

    private readonly List<(ISymbol Symbol, string ParamName)> _activeCaptureRenames = new();

    /// <summary>Maps a captured enclosing symbol to its hoisted-method parameter during body conversion.</summary>
    public void PushCaptureRename(ISymbol symbol, string paramName)
    {
        _activeCaptureRenames.Add((symbol, paramName));
    }

    public void PopCaptureRename()
    {
        if (_activeCaptureRenames.Count > 0)
            _activeCaptureRenames.RemoveAt(_activeCaptureRenames.Count - 1);
    }

    /// <summary>
    /// When converting the body of a hoisted capturing local function, references to the
    /// captured enclosing symbol must emit the hoisted parameter name instead.
    /// </summary>
    public string? TryGetCaptureRename(ISymbol symbol)
    {
        for (int i = _activeCaptureRenames.Count - 1; i >= 0; i--)
        {
            if (SymbolEqualityComparer.Default.Equals(_activeCaptureRenames[i].Symbol, symbol))
                return _activeCaptureRenames[i].ParamName;
        }
        return null;
    }

    /// <summary>
    /// Name-based capture rename lookup for synthetic call-site identifiers that have no
    /// symbol (e.g. a captured variable passed as an argument inside another hoisted
    /// capturing local function, whose own Java name is its hoisted parameter name).
    /// </summary>
    public string? TryGetCaptureRenameByName(string name)
    {
        for (int i = _activeCaptureRenames.Count - 1; i >= 0; i--)
        {
            if (_activeCaptureRenames[i].Symbol.Name == name)
                return _activeCaptureRenames[i].ParamName;
        }
        return null;
    }

    /// <summary>
    /// Resets all method-level state. Called on entering each new method.
    /// </summary>
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
        _shortCircuitDeferredSideEffects.Clear();
        _lambdaCaptureRegistry.Clear();
        _pendingLambdaCaptureHolders.Clear();
        _activeLambdaCaptureHolderFrames.Clear();
        _renamedLocals.Clear();
        _runtimeClassParametersByTypeParameter.Clear();
        LambdaCapturePreScanDone = false;
        _hoistedLocalFunctions.Clear();
        _nonHoistedLocalFunctions.Clear();
        // Captures/defaults of one method's local functions must not leak into
        // later methods (e.g. a local function "Add" capturing "converters"
        // appended converters to every later Add(...) call site).
        _hoistedLocalFunctionCaptures.Clear();
        _hoistedLocalFunctionDefaults.Clear();
        Labels.Clear();
        GotoAnalyzer = null;
        _postLoopGotoLabels.Clear();

        if (readOnlyRefStructParamNames != null)
        {
            foreach (var name in readOnlyRefStructParamNames)
            {
                _readOnlyRefStructParams.Add(name);
            }
        }
    }
}
