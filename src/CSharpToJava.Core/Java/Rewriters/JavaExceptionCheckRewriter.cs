using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping.JavaModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Populated between pass 1 and pass 2 of <see cref="VisitTypeDeclaration"/>.
    /// Maps each method name in the current type to the checked exceptions it declares
    /// (after pass 1 has run). Pass 2 uses this to propagate throws to callers of
    /// sibling methods within the same class.
    /// </summary>
    private Dictionary<string, HashSet<string>> _siblingThrows = new(StringComparer.Ordinal);

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

    // ─── Two-pass sibling-throws propagation ───────────────────────────────

    /// <summary>
    /// Runs two traversals over each type: the first collects JDK-based checked
    /// exceptions; the second propagates those throws to any sibling callers in the
    /// same class. This handles cases like a default-parameter overload that delegates
    /// to a full overload which carries <c>throws Exception</c>.
    /// </summary>
    public override JavaTypeDeclaration VisitTypeDeclaration(JavaTypeDeclaration node)
    {
        if (_javaLibrary is null)
            return node;

        // Save enclosing type's sibling map (supports nested types)
        var outerSiblingThrows = _siblingThrows;
        _siblingThrows = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Pass 1: standard JDK-based throws propagation
        node = base.VisitTypeDeclaration(node);

        // Build sibling throws map from pass-1 results
        bool hasSiblingThrows = false;
        foreach (var method in EnumerateMethods(node))
        {
            if (method.ThrownExceptions.Count > 0)
            {
                if (!_siblingThrows.TryGetValue(method.Name, out var set))
                    _siblingThrows[method.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ex in method.ThrownExceptions)
                {
                    if (set.Add(ex))
                        hasSiblingThrows = true;
                }
            }
        }

        // Pass 2: propagate sibling throws to callers (only when needed)
        if (hasSiblingThrows)
            node = base.VisitTypeDeclaration(node);

        _siblingThrows = outerSiblingThrows;
        return node;
    }

    private static IEnumerable<JavaMethodDeclaration> EnumerateMethods(JavaTypeDeclaration node) =>
        node switch
        {
            JavaClassDeclaration c => c.Methods,
            JavaInterfaceDeclaration i => i.Methods,
            JavaEnumDeclaration e => e.Methods,
            _ => Enumerable.Empty<JavaMethodDeclaration>(),
        };

    // ─── Method/constructor traversal ────────────────────────────────────────

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

        // Unqualified (no-target) call — may be a sibling method in the same class.
        if (node.Target is null && _siblingThrows.TryGetValue(node.MethodName, out var siblings))
        {
            foreach (var ex in siblings)
                if (!IsUnchecked(ex))
                    _pendingExceptions.Add(ex);
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

    // ─── Try-with-resources detection in raw statements ─────────

    // Matches the resource type in: try (Type varName = ...) or try (Type<Gen> varName = ...)
    private static readonly Regex TryWithResourcesTypePattern = new(
        @"try\s*\(\s*(\w+)(?:<[^>]*>)?\s+\w+\s*=",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans raw statements for try-with-resources patterns.
    /// The <c>using</c>-statement transformer emits raw strings, so the structured
    /// <see cref="JavaTryCatchStatement"/> path does not cover them.
    /// When a resource type is found, its <c>close()</c> declared exceptions are collected.
    /// </summary>
    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (_javaLibrary is not null && node.Code.Contains("try ("))
        {
            var matches = TryWithResourcesTypePattern.Matches(node.Code);
            foreach (Match match in matches)
            {
                var typeName = match.Groups[1].Value;
                if (typeName == "var")
                {
                    // Cannot determine resource type — conservatively add Exception
                    _pendingExceptions.Add("Exception");
                    continue;
                }

                var canonical = ResolveCanonical(typeName);
                if (canonical is not null)
                {
                    CollectCheckedExceptions(canonical, "close");
                }
                else
                {
                    // Unknown / converted type — AutoCloseable.close() declares throws Exception
                    _pendingExceptions.Add("Exception");
                }
            }
        }

        // Propagate throws from sibling method calls found in raw statement text.
        // Raw statements are emitted as strings (e.g. default-param delegating overloads),
        // so VisitMethodCallExpression is not invoked for them.
        if (_siblingThrows.Count > 0)
        {
            foreach (var (methodName, throws) in _siblingThrows)
            {
                if (ContainsSiblingCall(node.Code, methodName))
                {
                    foreach (var ex in throws)
                        if (!IsUnchecked(ex))
                            _pendingExceptions.Add(ex);
                }
            }
        }

        return node;
    }

    /// <summary>
    /// Returns true when <paramref name="code"/> contains a syntactically unqualified call
    /// to <paramref name="methodName"/> (i.e. the character immediately before the name is
    /// not an identifier character, ruling out longer identifiers that end with the same name).
    /// </summary>
    private static bool ContainsSiblingCall(string code, string methodName)
    {
        var pattern = methodName + "(";
        var idx = code.IndexOf(pattern, StringComparison.Ordinal);
        while (idx >= 0)
        {
            if (idx == 0 || !IsIdentifierChar(code[idx - 1]))
                return true;
            idx = code.IndexOf(pattern, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';
}

