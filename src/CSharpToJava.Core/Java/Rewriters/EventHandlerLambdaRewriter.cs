namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes event handler lambda type assignments where a two-parameter
/// lambda (sender, eventArgs) is incorrectly typed as <c>Consumer&lt;T&gt;</c> instead
/// of <c>BiConsumer&lt;Object, T&gt;</c>.
///
/// <para>C# event handlers typically have the signature <c>(object sender, EventArgs e)</c>.
/// When converted to Java functional interfaces, the converter may incorrectly use
/// <c>Consumer&lt;T&gt;</c> (single-argument) instead of <c>BiConsumer&lt;Object, T&gt;</c>
/// (two-argument) when the lambda has two parameters.</para>
///
/// <para>This rewriter detects variable declarations of type <c>Consumer&lt;T&gt;</c>
/// with a lambda initializer that has 2 parameters, and rewrites the type to
/// <c>BiConsumer&lt;Object, T&gt;</c>.</para>
/// </summary>
public sealed class EventHandlerLambdaRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaVariableDeclarationStatement VisitVariableDeclarationStatement(
        JavaVariableDeclarationStatement node)
    {
        node = (JavaVariableDeclarationStatement)base.VisitVariableDeclarationStatement(node);

        // Pattern: Consumer<EventArgs> handler = (sender, e) -> { ... }
        //      →   BiConsumer<Object, EventArgs> handler = (sender, e) -> { ... }
        if (node.Initializer is JavaLambdaExpression lambda &&
            lambda.Parameters.Count == 2 &&
            TryExtractConsumerTypeArg(node.Type, out var typeArg))
        {
            node.Type = $"BiConsumer<Object, {typeArg}>";
            _rewriteCount++;
        }

        return node;
    }

    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        // Fields don't have structured initializers (Initializer is a string),
        // so we check the string for a lambda pattern.
        // For now, this rewriter focuses on variable declarations where we have IR nodes.
        return base.VisitFieldDeclaration(node);
    }

    /// <summary>
    /// Extracts the type argument from a <c>Consumer&lt;T&gt;</c> type string.
    /// Returns <c>true</c> if the type matches <c>Consumer&lt;...&gt;</c>.
    /// </summary>
    private static bool TryExtractConsumerTypeArg(string type, out string typeArg)
    {
        typeArg = string.Empty;

        // Match "Consumer<SomeType>"
        const string prefix = "Consumer<";
        if (!type.StartsWith(prefix, StringComparison.Ordinal) ||
            !type.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        // Don't match BiConsumer<...> — already correct
        if (type.StartsWith("BiConsumer<", StringComparison.Ordinal))
        {
            return false;
        }

        typeArg = type.Substring(prefix.Length, type.Length - prefix.Length - 1);
        return !string.IsNullOrWhiteSpace(typeArg);
    }
}
