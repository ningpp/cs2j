namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes <c>AbstractMap.SimpleEntry</c> usages in iteration contexts
/// to use <c>Map.Entry</c> instead.
///
/// <para>Addresses error pattern 14: <c>Map.entrySet()</c> returns
/// <c>Set&lt;Map.Entry&lt;K,V&gt;&gt;</c>, not <c>Set&lt;AbstractMap.SimpleEntry&lt;K,V&gt;&gt;</c>.
/// When iterating over a map's entries with <c>for (var entry : map.entrySet())</c>,
/// the entry type must be <c>Map.Entry&lt;K,V&gt;</c>.</para>
///
/// <para>The rewriter detects <c>AbstractMap.SimpleEntry</c> type references in:
/// <list type="bullet">
///   <item>Variable declarations</item>
///   <item>For-each loop variable types</item>
///   <item>Cast expressions</item>
///   <item>instanceof checks</item>
/// </list>
/// and replaces them with <c>Map.Entry</c> when the context indicates iteration
/// over map entries.</para>
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

    public override JavaForEachStatement VisitForEachStatement(JavaForEachStatement node)
    {
        // Fix: for (AbstractMap.SimpleEntry<K,V> entry : map.entrySet())
        //  →   for (Map.Entry<K,V> entry : map.entrySet())
        if (IsEntrySetIteration(node.Collection) && ContainsSimpleEntry(node.VariableType))
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

    // ─── helpers ─────────────────────────────────────────────

    private static bool ContainsSimpleEntry(string type)
        => type.Contains(SimpleEntryFull, StringComparison.Ordinal);

    private static string ReplaceSimpleEntry(string type)
        => type.Replace(SimpleEntryFull, MapEntryFull, StringComparison.Ordinal);

    private static bool IsEntrySetIteration(JavaExpression collection)
    {
        // collection is map.entrySet()
        return collection is JavaMethodCallExpression { MethodName: "entrySet" };
    }
}
