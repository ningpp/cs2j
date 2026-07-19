namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes .NET exception API usages that don't map to Java equivalents.
///
/// <para>Addresses several exception-related conversion issues:
/// <list type="bullet">
///   <item><c>ex.getInnerException()</c> → <c>ex.getCause()</c>
///         (.NET InnerException property → Java Throwable.getCause())</item>
///   <item><c>new ApplicationException(msg)</c> → <c>new RuntimeException(msg)</c>
///         (ApplicationException doesn't exist in Java)</item>
///   <item><c>new InvalidOperationException(msg)</c> → <c>new IllegalStateException(msg)</c></item>
///   <item><c>new ArgumentNullException(msg)</c> → <c>new NullPointerException(msg)</c></item>
///   <item><c>new ArgumentException(msg)</c> → <c>new IllegalArgumentException(msg)</c></item>
///   <item><c>new ArgumentOutOfRangeException(msg)</c> → <c>new IndexOutOfBoundsException(msg)</c></item>
///   <item><c>new NotImplementedException(msg)</c> → <c>new UnsupportedOperationException(msg)</c></item>
///   <item><c>new NotSupportedException(msg)</c> → <c>new UnsupportedOperationException(msg)</c></item>
///   <item>Catch clause types: same mappings applied to exception types in catch blocks</item>
/// </list>
/// </para>
/// </summary>
public sealed class ExceptionApiRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    /// <summary>
    /// Maps .NET exception type names (PascalCase) to Java equivalents.
    /// </summary>
    private static readonly Dictionary<string, string> ExceptionTypeMap = new(StringComparer.Ordinal)
    {
        ["Exception"] = "RuntimeException",
        ["ApplicationException"] = "RuntimeException",
        ["InvalidOperationException"] = "IllegalStateException",
        ["ArgumentNullException"] = "NullPointerException",
        ["ArgumentException"] = "IllegalArgumentException",
        ["ArgumentOutOfRangeException"] = "IndexOutOfBoundsException",
        ["NotImplementedException"] = "UnsupportedOperationException",
        ["NotSupportedException"] = "UnsupportedOperationException",
        ["ObjectDisposedException"] = "IllegalStateException",
        ["InvalidDataException"] = "IllegalArgumentException",
    };

    /// <summary>
    /// Maps compat exception types to their Java parent exception types.
    /// Used to reorder catch clauses so that more specific types come before
    /// more general ones (required by Java).
    /// </summary>
    private static readonly Dictionary<string, string> ExceptionHierarchy = new(StringComparer.Ordinal)
    {
        // Compat exceptions that extend IllegalArgumentException (Java standard)
        // These are NOT in ExceptionTypeMap, so they stay as-is after type mapping.
        ["FormatException"] = "IllegalArgumentException",
        ["CookieException"] = "IllegalArgumentException",
        ["CultureNotFoundException"] = "IllegalArgumentException",
        ["CustomAttributeFormatException"] = "IllegalArgumentException",
        ["DecoderFallbackException"] = "IllegalArgumentException",
        ["DuplicateWaitObjectException"] = "IllegalArgumentException",
        ["EncoderFallbackException"] = "IllegalArgumentException",
        ["HelpCategoryInvalidException"] = "IllegalArgumentException",
        ["InvalidAsynchronousStateException"] = "IllegalArgumentException",
        ["InvalidEnumArgumentException"] = "IllegalArgumentException",
        ["PSArgumentException"] = "IllegalArgumentException",
        ["UriFormatException"] = "IllegalArgumentException",
        // Compat exceptions that extend IllegalStateException (Java standard)
        ["PSInvalidOperationException"] = "IllegalStateException",
        ["PingException"] = "IllegalStateException",
        ["ProtocolViolationException"] = "IllegalStateException",
        ["WebException"] = "IllegalStateException",
        // Compat exceptions that extend IndexOutOfBoundsException (Java standard)
        ["IndexOutOfRangeException"] = "IndexOutOfBoundsException",
        ["PSArgumentOutOfRangeException"] = "IndexOutOfBoundsException",
        // Compat exceptions that extend ClassCastException (Java standard)
        ["InvalidCastException"] = "ClassCastException",
        ["PSInvalidCastException"] = "ClassCastException",
        // Compat exceptions that extend NullPointerException (Java standard)
        ["PSArgumentNullException"] = "NullPointerException",
        // Compat exceptions that extend UnsupportedOperationException (Java standard)
        ["PlatformNotSupportedException"] = "UnsupportedOperationException",
        ["PSNotImplementedException"] = "UnsupportedOperationException",
        ["PSNotSupportedException"] = "UnsupportedOperationException",
        // Compat exceptions that extend ArithmeticException (Java standard)
        ["NotFiniteNumberException"] = "ArithmeticException",
        ["OverflowException"] = "ArithmeticException",
        // Compat exceptions that extend IOException (Java standard)
        ["DriveNotFoundException"] = "IOException",
        ["FileLoadException"] = "IOException",
        ["PathTooLongException"] = "IOException",
        // Compat exceptions that extend IllegalAccessException (Java standard)
        ["FieldAccessException"] = "IllegalAccessException",
        ["MethodAccessException"] = "IllegalAccessException",
        ["MissingMemberException"] = "IllegalAccessException",
        // Compat exceptions that extend ClassNotFoundException (Java standard)
        ["DllNotFoundException"] = "ClassNotFoundException",
        ["EntryPointNotFoundException"] = "ClassNotFoundException",
        ["TypeAccessException"] = "ClassNotFoundException",
    };

    /// <summary>
    /// Maps .NET exception member names to Java equivalents.
    /// </summary>
    private static readonly Dictionary<string, string> ExceptionMethodMap = new(StringComparer.Ordinal)
    {
        ["getInnerException"] = "getCause",
        ["InnerException"] = "getCause",
    };

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        // Pattern: ex.getInnerException() → ex.getCause()
        if (ExceptionMethodMap.TryGetValue(node.MethodName, out var replacement) &&
            node.Arguments.Count == 0 &&
            node.Target != null)
        {
            node.MethodName = replacement;
            _rewriteCount++;
        }

        return node;
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        var result = base.VisitExpression(node);

        // Pattern: ex.InnerException (member access) → ex.getCause()
        if (result is JavaMemberAccessExpression memberAccess &&
            ExceptionMethodMap.TryGetValue(memberAccess.MemberName, out var methodReplacement))
        {
            _rewriteCount++;
            return new JavaMethodCallExpression
            {
                Target = memberAccess.Target,
                MethodName = methodReplacement,
            };
        }

        return result;
    }

    /// <summary>
    /// Java exception types that do NOT have a (String, Throwable) constructor.
    /// When a .NET exception with an inner exception maps to one of these types,
    /// the cause argument must be stripped (Java doesn't support it).
    /// </summary>
    private static readonly HashSet<string> JavaExceptionsWithoutCauseConstructor = new(StringComparer.Ordinal)
    {
        "NumberFormatException",
        "IllegalArgumentException",
        "IllegalStateException",
        "IndexOutOfBoundsException",
        "UnsupportedOperationException",
        "NullPointerException",
    };

    public override JavaNewExpression VisitNewExpression(JavaNewExpression node)
    {
        node = (JavaNewExpression)base.VisitNewExpression(node);

        // Pattern: new ApplicationException(msg) → new RuntimeException(msg)
        if (ExceptionTypeMap.TryGetValue(node.Type, out var javaType))
        {
            node.Type = javaType;
            _rewriteCount++;
        }

        // Pattern: new NumberFormatException(msg, cause) → new NumberFormatException(msg)
        // Java exceptions like NumberFormatException don't have a (String, Throwable) constructor.
        // Strip the cause argument when the mapped type doesn't support it.
        if (node.Arguments.Count == 2
            && JavaExceptionsWithoutCauseConstructor.Contains(node.Type))
        {
            // Keep only the first argument (the message), discard the cause
            node.Arguments.RemoveAt(1);
            _rewriteCount++;
        }

        return node;
    }

    public override JavaTryCatchStatement VisitTryCatchStatement(JavaTryCatchStatement node)
    {
        node = (JavaTryCatchStatement)base.VisitTryCatchStatement(node);

        // Rewrite catch clause exception types
        foreach (var catchClause in node.CatchClauses)
        {
            if (ExceptionTypeMap.TryGetValue(catchClause.ExceptionType, out var javaCatchType))
            {
                catchClause.ExceptionType = javaCatchType;
                _rewriteCount++;
            }
        }

        // Reorder catch clauses so that more specific exception types come before
        // more general ones. In Java, a catch clause for a subclass must precede
        // the catch clause for its superclass, otherwise it's unreachable.
        // For example: catch (FormatException) must come before catch (IllegalArgumentException)
        // because FormatException extends IllegalArgumentException.
        if (node.CatchClauses.Count > 1)
        {
            ReorderCatchClauses(node);
        }

        return node;
    }

    private void ReorderCatchClauses(JavaTryCatchStatement node)
    {
        // Build a depth map: how many levels up from each catch type to RuntimeException
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cc in node.CatchClauses)
        {
            depths[cc.ExceptionType] = GetExceptionDepth(cc.ExceptionType);
        }

        // Sort: deeper (more specific) types first
        var sorted = node.CatchClauses
            .OrderByDescending(cc => depths.GetValueOrDefault(cc.ExceptionType, 0))
            .ThenBy(cc => cc.ExceptionType, StringComparer.Ordinal)
            .ToList();

        // Check if order changed
        bool changed = false;
        for (int i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(sorted[i], node.CatchClauses[i]))
            {
                changed = true;
                break;
            }
        }

        if (changed)
        {
            node.CatchClauses.Clear();
            foreach (var cc in sorted)
                node.CatchClauses.Add(cc);
            _rewriteCount++;
        }
    }

    /// <summary>
    /// Gets the depth of an exception type in the hierarchy (higher = more specific).
    /// Returns 1 for unknown types so that project-specific exceptions are sorted
    /// before the general <c>RuntimeException</c> catch-all that <c>System.Exception</c>
    /// maps to. Known Java roots (<c>RuntimeException</c>, <c>Exception</c>, <c>Throwable</c>)
    /// are treated as the least specific types.
    /// </summary>
    private static int GetExceptionDepth(string exceptionType)
    {
        if (exceptionType is "RuntimeException" or "Exception" or "Throwable")
        {
            return 0;
        }

        int depth = 1;
        var current = exceptionType;
        while (ExceptionHierarchy.TryGetValue(current, out var parent))
        {
            depth++;
            current = parent;
        }
        return depth;
    }

    public override JavaThrowStatement VisitThrowStatement(JavaThrowStatement node)
    {
        // The base visitor already handles the expression via VisitExpression/VisitNewExpression.
        return (JavaThrowStatement)base.VisitThrowStatement(node);
    }
}
