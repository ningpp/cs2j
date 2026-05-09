namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that normalizes <c>AbstractMap.SimpleEntry</c> type references
/// to <c>Map.Entry</c> across all IR positions.
///
/// <para><c>AbstractMap.SimpleEntry</c> is the concrete class used only in
/// <c>new</c> expressions (since <c>Map.Entry</c> is an interface).  All other
/// type-reference positions (declarations, parameters, return types, generics)
/// must use the interface type <c>Map.Entry</c> to avoid Java type-compatibility
/// errors — especially with generic invariance
/// (e.g. <c>ArrayList&lt;SimpleEntry&gt;</c> ≠ <c>ArrayList&lt;Map.Entry&gt;</c>).</para>
/// </summary>
public sealed class MapEntryTypeRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    private const string SimpleEntryFull = "AbstractMap.SimpleEntry";
    private const string MapEntryFull = "Map.Entry";

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    // ─── member declarations ────────────────────────────────

    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        // Fix: AbstractMap.SimpleEntry<K,V> field; → Map.Entry<K,V> field;
        if (ContainsSimpleEntry(node.Type))
        {
            node.Type = ReplaceSimpleEntry(node.Type);
            _rewriteCount++;
        }

        return base.VisitFieldDeclaration(node);
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        // Fix return type: AbstractMap.SimpleEntry<K,V> → Map.Entry<K,V>
        if (ContainsSimpleEntry(node.ReturnType))
        {
            node.ReturnType = ReplaceSimpleEntry(node.ReturnType);
            _rewriteCount++;
        }

        // Fix parameter types
        RewriteParameters(node.Parameters);

        // Fix raw SimpleEntry in method body string (not structured IR)
        if (!string.IsNullOrWhiteSpace(node.Body) && ContainsSimpleEntry(node.Body))
        {
            var before = node.Body;
            node.Body = ReplaceSimpleEntryInRaw(node.Body);
            node.Body = FixRawSimpleEntryDiamond(node.Body);
            if (node.Body != before)
                _rewriteCount++;
        }

        return base.VisitMethodDeclaration(node);
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        RewriteParameters(node.Parameters);

        // Fix raw SimpleEntry in constructor body string
        if (!string.IsNullOrWhiteSpace(node.Body) && ContainsSimpleEntry(node.Body))
        {
            var before = node.Body;
            node.Body = ReplaceSimpleEntryInRaw(node.Body);
            node.Body = FixRawSimpleEntryDiamond(node.Body);
            if (node.Body != before)
                _rewriteCount++;
        }

        return base.VisitConstructorDeclaration(node);
    }

    // ─── statements ─────────────────────────────────────────

    public override JavaForEachStatement VisitForEachStatement(JavaForEachStatement node)
    {
        // Fix: for (AbstractMap.SimpleEntry<K,V> entry : collection)
        //  →   for (Map.Entry<K,V> entry : collection)
        // Applies regardless of collection source (entrySet, list, etc.)
        if (ContainsSimpleEntry(node.VariableType))
        {
            node.VariableType = ReplaceSimpleEntry(node.VariableType);
            _rewriteCount++;
        }

        return base.VisitForEachStatement(node);
    }

    public override JavaVariableDeclarationStatement VisitVariableDeclarationStatement(JavaVariableDeclarationStatement node)
    {
        // Fix: AbstractMap.SimpleEntry<K,V> entry = ...
        //  →   Map.Entry<K,V> entry = ...
        if (ContainsSimpleEntry(node.Type))
        {
            node.Type = ReplaceSimpleEntry(node.Type);
            _rewriteCount++;
        }

        return base.VisitVariableDeclarationStatement(node);
    }

    // ─── expressions ────────────────────────────────────────

    public override JavaCastExpression VisitCastExpression(JavaCastExpression node)
    {
        // Fix: (AbstractMap.SimpleEntry<K,V>) expr → (Map.Entry<K,V>) expr
        if (ContainsSimpleEntry(node.Type))
        {
            node.Type = ReplaceSimpleEntry(node.Type);
            _rewriteCount++;
        }

        return base.VisitCastExpression(node);
    }

    public override JavaInstanceOfExpression VisitInstanceOfExpression(JavaInstanceOfExpression node)
    {
        // Fix: expr instanceof AbstractMap.SimpleEntry → expr instanceof Map.Entry
        if (ContainsSimpleEntry(node.Type))
        {
            node.Type = ReplaceSimpleEntry(node.Type);
            _rewriteCount++;
        }

        return base.VisitInstanceOfExpression(node);
    }

    public override JavaNewExpression VisitNewExpression(JavaNewExpression node)
    {
        // Fix generic type arguments in new expressions:
        //   new ArrayList<AbstractMap.SimpleEntry<K,V>>()
        //     → new ArrayList<Map.Entry<K,V>>()
        // BUT preserve: new AbstractMap.SimpleEntry<>(k, v) — that is the only
        // legal way to instantiate a Map.Entry.
        if (ContainsSimpleEntry(node.Type) && !IsBareSimpleEntry(node.Type))
        {
            node.Type = ReplaceSimpleEntry(node.Type);
            _rewriteCount++;
        }

        return base.VisitNewExpression(node);
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (ContainsSimpleEntry(node.Code))
        {
            var before = node.Code;
            node.Code = ReplaceSimpleEntryInRaw(node.Code);
            node.Code = FixRawSimpleEntryDiamond(node.Code);
            if (node.Code != before)
            {
                _rewriteCount++;
            }
        }

        return node;
    }

    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        if (ContainsSimpleEntry(node.Code))
        {
            var before = node.Code;
            node.Code = ReplaceSimpleEntryInRaw(node.Code);
            node.Code = FixRawSimpleEntryDiamond(node.Code);
            if (node.Code != before)
            {
                _rewriteCount++;
            }
        }

        return node;
    }

    // ─── helpers ─────────────────────────────────────────────

    private void RewriteParameters(List<JavaParameter> parameters)
    {
        foreach (var param in parameters)
        {
            if (ContainsSimpleEntry(param.Type))
            {
                param.Type = ReplaceSimpleEntry(param.Type);
                _rewriteCount++;
            }
        }
    }

    private static bool ContainsSimpleEntry(string type)
        => type.Contains(SimpleEntryFull, StringComparison.Ordinal);

    private static string ReplaceSimpleEntry(string type)
        => type.Replace(SimpleEntryFull, MapEntryFull, StringComparison.Ordinal);

    /// <summary>
    /// Replaces <c>AbstractMap.SimpleEntry</c> with <c>Map.Entry</c> in a raw
    /// code string, but preserves occurrences immediately preceded by <c>new </c>
    /// since those are legitimate concrete instantiations.
    /// </summary>
    private static string ReplaceSimpleEntryInRaw(string code)
    {
        const string newPrefix = "new ";

        var sb = new System.Text.StringBuilder(code.Length);
        int pos = 0;
        while (pos < code.Length)
        {
            int idx = code.IndexOf(SimpleEntryFull, pos, StringComparison.Ordinal);
            if (idx < 0)
            {
                sb.Append(code, pos, code.Length - pos);
                break;
            }

            // Check if preceded by "new "
            bool afterNew = idx >= newPrefix.Length
                && code.Substring(idx - newPrefix.Length, newPrefix.Length) == newPrefix;

            sb.Append(code, pos, idx - pos);
            sb.Append(afterNew ? SimpleEntryFull : MapEntryFull);
            pos = idx + SimpleEntryFull.Length;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns true when the type string is exactly <c>AbstractMap.SimpleEntry</c>
    /// or <c>AbstractMap.SimpleEntry&lt;...&gt;</c> (i.e. the bare type being
    /// instantiated, not nested inside another generic).
    /// </summary>
    /// <summary>
    /// Fixes raw SimpleEntry/Map.Entry types in generated Java code:
    /// 1. Adds diamond operator to <c>new AbstractMap.SimpleEntry(</c>
    /// 2. Adds wildcard to raw <c>(Map.Entry)</c> casts → <c>(Map.Entry&lt;?, ?&gt;)</c>
    /// </summary>
    private static string FixRawSimpleEntryDiamond(string code)
    {
        // Fix 1: new AbstractMap.SimpleEntry( → new AbstractMap.SimpleEntry<>(
        code = System.Text.RegularExpressions.Regex.Replace(
            code,
            @"\bnew AbstractMap\.SimpleEntry\(",
            "new AbstractMap.SimpleEntry<>(");

        // Fix 2: Remove raw (Map.Entry) casts. They erase generics even when
        // the constructor uses diamond inference.
        // (Map.Entry) new SimpleEntry<>(...) → new SimpleEntry<>(...)
        code = System.Text.RegularExpressions.Regex.Replace(
            code,
            @"\(Map\.Entry\)\s*(new\s+AbstractMap\.SimpleEntry(?:<[^>]*>)?\s*\()",
            "$1");

        return code;
    }

    private static bool IsBareSimpleEntry(string type)
    {
        var trimmed = type.AsSpan().Trim();
        if (!trimmed.StartsWith(SimpleEntryFull.AsSpan(), StringComparison.Ordinal))
            return false;
        var rest = trimmed.Slice(SimpleEntryFull.Length);
        // Exact match or followed by generic parameters only
        return rest.IsEmpty || rest[0] == '<';
    }
}
