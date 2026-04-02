namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes delegate invocation calls. C# delegates are invoked via
/// <c>delegate(args)</c> or <c>delegate.Invoke(args)</c>, but Java functional interfaces
/// require specific method names.
///
/// <para>Addresses error pattern 16: The converter generates <c>.Invoke()</c> or
/// <c>.invoke()</c> which don't exist on Java functional interfaces. The correct methods
/// depend on the functional interface type:
/// <list type="bullet">
///   <item><c>Consumer&lt;T&gt;</c> → <c>.accept(t)</c></item>
///   <item><c>BiConsumer&lt;T,U&gt;</c> → <c>.accept(t, u)</c></item>
///   <item><c>Function&lt;T,R&gt;</c> → <c>.apply(t)</c></item>
///   <item><c>BiFunction&lt;T,U,R&gt;</c> → <c>.apply(t, u)</c></item>
///   <item><c>Supplier&lt;T&gt;</c> → <c>.get()</c></item>
///   <item><c>Predicate&lt;T&gt;</c> → <c>.test(t)</c></item>
///   <item><c>Runnable</c> → <c>.run()</c></item>
///   <item><c>Callable&lt;T&gt;</c> → <c>.call()</c></item>
///   <item><c>Comparator&lt;T&gt;</c> → <c>.compare(t1, t2)</c></item>
/// </list>
/// </para>
///
/// <para>Since we don't have type information at the IR level, we use argument count
/// heuristics to determine the most likely functional interface method name.</para>
/// </summary>
public sealed class DelegateInvocationRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        // Pattern: target.Invoke(args) or target.invoke(args)
        if (node.MethodName is "Invoke" or "invoke" && node.Target != null)
        {
            // Determine the correct functional interface method based on context/argument count.
            // Without full type info, use argument-count heuristics:
            //  0 args: Supplier.get() or Runnable.run() or Callable.call()
            //  1 arg:  Consumer.accept() or Function.apply() or Predicate.test()
            //  2 args: BiConsumer.accept() or BiFunction.apply() or Comparator.compare()
            //
            // Since we can't distinguish return-type usage at IR level, we default to
            // the most common: apply() for 1-2 args (works for Function/BiFunction),
            // run() for 0 args (works for Runnable).
            // More specific rewrites should be handled at the transformer level where
            // semantic info is available.

            // For now: rename Invoke/invoke to the most likely candidate.
            // This is a best-effort fix; the transformer should ideally provide the correct name.
            var argCount = node.Arguments.Count;
            var newMethodName = argCount switch
            {
                0 => "run", // Runnable or Supplier — run() is safe for void
                1 => "apply", // Function<T,R>.apply(t)
                2 => "apply", // BiFunction<T,U,R>.apply(t,u)
                _ => "apply", // Default fallback
            };

            node.MethodName = newMethodName;
            _rewriteCount++;
        }

        return node;
    }
}
