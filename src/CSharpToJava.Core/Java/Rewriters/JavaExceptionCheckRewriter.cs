using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit Java IR rewriter that detects method calls whose target Java methods declare
/// checked exceptions, and propagates those exceptions to the enclosing method's
/// <see cref="JavaMethodDeclaration.ThrownExceptions"/> list.
///
/// <para>Java checked exceptions (any exception that is not <c>RuntimeException</c> or
/// <c>Error</c>) must be either caught or declared in the method's <c>throws</c> clause.
/// Since C# has no checked exceptions, the converter would otherwise produce Java code
/// that fails to compile.</para>
///
/// <para>Strategy: propagate <c>throws</c> (clean signatures). If the user wants
/// <c>try-catch</c> wrapping instead, that would require a separate rewriter.</para>
///
/// <para>Diagnostics emitted:
/// <list type="bullet">
///   <item><c>CS2J4005</c> — checked exception added to throws clause</item>
/// </list>
/// </para>
/// </summary>
public sealed class JavaExceptionCheckRewriter : JavaSyntaxRewriter
{
    private readonly JavaLibraryIndex? _javaLibrary;
    private readonly DiagnosticCollector _diagnostics;

    /// <summary>
    /// During method traversal, collects checked exceptions that need to be declared.
    /// </summary>
    private readonly HashSet<string> _pendingExceptions = new(StringComparer.Ordinal);

    private int _diagnosticCount;

    /// <summary>Number of diagnostics emitted during the last <c>VisitCompilationUnit</c> call.</summary>
    public int DiagnosticCount => _diagnosticCount;

    // Well-known unchecked exception supertypes in Java
    private static readonly HashSet<string> UncheckedExceptionTypes = new(StringComparer.Ordinal)
    {
        "java.lang.RuntimeException",
        "java.lang.Error",
        "RuntimeException",
        "Error",
    };

    // Well-known unchecked exception concrete types (common ones)
    private static readonly HashSet<string> KnownUncheckedExceptions = new(StringComparer.Ordinal)
    {
        "NullPointerException", "java.lang.NullPointerException",
        "IllegalArgumentException", "java.lang.IllegalArgumentException",
        "IllegalStateException", "java.lang.IllegalStateException",
        "IndexOutOfBoundsException", "java.lang.IndexOutOfBoundsException",
        "ArrayIndexOutOfBoundsException", "java.lang.ArrayIndexOutOfBoundsException",
        "StringIndexOutOfBoundsException", "java.lang.StringIndexOutOfBoundsException",
        "UnsupportedOperationException", "java.lang.UnsupportedOperationException",
        "ClassCastException", "java.lang.ClassCastException",
        "ArithmeticException", "java.lang.ArithmeticException",
        "NumberFormatException", "java.lang.NumberFormatException",
        "ConcurrentModificationException", "java.util.ConcurrentModificationException",
        "NoSuchElementException", "java.util.NoSuchElementException",
        "StackOverflowError", "java.lang.StackOverflowError",
        "OutOfMemoryError", "java.lang.OutOfMemoryError",
        "AssertionError", "java.lang.AssertionError",
    };

    public JavaExceptionCheckRewriter(JavaLibraryIndex? javaLibrary, DiagnosticCollector diagnostics)
    {
        _javaLibrary = javaLibrary;
        _diagnostics = diagnostics;
    }

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _diagnosticCount = 0;

        if (_javaLibrary is null)
            return node; // No metadata loaded — skip

        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        if (_javaLibrary is null)
            return node;

        // Save and reset pending exceptions for this method scope
        var outerExceptions = new HashSet<string>(_pendingExceptions, StringComparer.Ordinal);
        _pendingExceptions.Clear();

        // Visit method body to collect all checked exceptions from method calls
        if (node.StructuredBody != null)
        {
            node.StructuredBody = VisitMethodBody(node.StructuredBody);
        }

        // Add any collected checked exceptions to the method's throws clause
        var existingThrows = new HashSet<string>(node.ThrownExceptions, StringComparer.Ordinal);
        foreach (var exception in _pendingExceptions)
        {
            if (!existingThrows.Contains(exception))
            {
                node.ThrownExceptions.Add(exception);
                _diagnostics.Info(
                    $"Added checked exception '{exception}' to throws clause of method '{node.Name}'",
                    location: null,
                    code: "CS2J4005",
                    category: "JavaApiValidation");
                _diagnosticCount++;
            }
        }

        // Restore outer scope
        _pendingExceptions.Clear();
        foreach (var ex in outerExceptions)
            _pendingExceptions.Add(ex);

        return node;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        if (_javaLibrary is null)
            return node;

        // Similar to method — collect and propagate exceptions
        var outerExceptions = new HashSet<string>(_pendingExceptions, StringComparer.Ordinal);
        _pendingExceptions.Clear();

        if (node.StructuredBody != null)
        {
            node.StructuredBody = VisitMethodBody(node.StructuredBody);
        }

        var existingThrows = new HashSet<string>(node.ThrownExceptions, StringComparer.Ordinal);
        foreach (var exception in _pendingExceptions)
        {
            if (!existingThrows.Contains(exception))
            {
                node.ThrownExceptions.Add(exception);
                _diagnostics.Info(
                    $"Added checked exception '{exception}' to throws clause of constructor '{node.ClassName}'",
                    location: null,
                    code: "CS2J4005",
                    category: "JavaApiValidation");
                _diagnosticCount++;
            }
        }

        _pendingExceptions.Clear();
        foreach (var ex in outerExceptions)
            _pendingExceptions.Add(ex);

        return node;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        if (_javaLibrary is not null && node.Target is not null)
        {
            var targetTypeName = InferTargetType(node.Target);
            if (targetTypeName is not null)
            {
                var canonicalName = ResolveCanonical(targetTypeName);
                if (canonicalName is not null)
                {
                    CollectCheckedExceptions(canonicalName, node.MethodName);
                }
            }
        }

        return base.VisitMethodCallExpression(node);
    }

    // ─── Private helpers ────────────────────────────────────

    private void CollectCheckedExceptions(string typeName, string methodName)
    {
        var methods = _javaLibrary!.FindMethods(typeName, methodName);
        foreach (var method in methods)
        {
            foreach (var exception in method.Exceptions)
            {
                if (!IsUnchecked(exception))
                {
                    // Use simple name (without java.lang. prefix) for cleaner throws clauses
                    var simpleName = exception.Contains('.') ? exception.Substring(exception.LastIndexOf('.') + 1) : exception;
                    _pendingExceptions.Add(simpleName);
                }
            }
        }

        // Also check supertypes
        foreach (var supertype in _javaLibrary.GetSupertypes(typeName))
        {
            var superMethods = _javaLibrary.FindMethods(supertype, methodName);
            foreach (var method in superMethods)
            {
                foreach (var exception in method.Exceptions)
                {
                    if (!IsUnchecked(exception))
                    {
                        var simpleName = exception.Contains('.') ? exception.Substring(exception.LastIndexOf('.') + 1) : exception;
                        _pendingExceptions.Add(simpleName);
                    }
                }
            }
        }
    }

    private bool IsUnchecked(string exceptionTypeName)
    {
        if (UncheckedExceptionTypes.Contains(exceptionTypeName))
            return true;

        if (KnownUncheckedExceptions.Contains(exceptionTypeName))
            return true;

        // If we have metadata, check the inheritance chain
        if (_javaLibrary is not null)
        {
            var canonicalName = exceptionTypeName.Contains('.')
                ? exceptionTypeName
                : "java.lang." + exceptionTypeName;

            if (_javaLibrary.IsAssignableTo(canonicalName, "java.lang.RuntimeException"))
                return true;
            if (_javaLibrary.IsAssignableTo(canonicalName, "java.lang.Error"))
                return true;
        }

        return false;
    }

    private static string? InferTargetType(JavaExpression target)
    {
        return target switch
        {
            JavaNewExpression newExpr => JavaApiValidationRewriter.StripGenerics(newExpr.Type),
            JavaIdentifierExpression id when id.Name.Length > 0 && char.IsUpper(id.Name[0]) => id.Name,
            _ => null,
        };
    }

    private string? ResolveCanonical(string typeName)
    {
        if (_javaLibrary is null)
            return null;

        if (typeName.Contains('.'))
            return typeName;

        if (_javaLibrary.FindType("java.lang." + typeName) is not null)
            return "java.lang." + typeName;

        if (_javaLibrary.FindType("java.util." + typeName) is not null)
            return "java.util." + typeName;

        if (_javaLibrary.FindType("java.io." + typeName) is not null)
            return "java.io." + typeName;

        return null;
    }
}
