using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping.JavaModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit Java IR rewriter that detects method calls whose target Java methods declare
/// checked exceptions, and wraps the enclosing method body with try-catch to re-throw as
/// RuntimeException. Since C# has no checked exceptions, this matches C# semantics.
///
/// <para>Diagnostics emitted:
/// <list type="bullet">
///   <item><c>CS2J4005</c> — method body wrapped for checked exception handling</item>
/// </list>
/// </para>
/// </summary>
public sealed class JavaExceptionCheckRewriter : JavaSyntaxRewriter
{
    private readonly JavaLibraryIndex? _javaLibrary;
    private readonly DiagnosticCollector _diagnostics;

    /// <summary>
    /// During method traversal, collects checked exceptions that need handling.
    /// </summary>
    private readonly HashSet<string> _pendingExceptions = new(StringComparer.Ordinal);

    /// <summary>
    /// Tracks field types (name → type) for the current type. Populated per type declaration.
    /// </summary>
    private readonly Dictionary<string, string> _fieldTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Tracks local variable types (name → type) for the current method. Cleared per method.
    /// </summary>
    private readonly Dictionary<string, string> _localTypes = new(StringComparer.Ordinal);

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
            return node;

        return base.VisitCompilationUnit(node);
    }

    public override JavaTypeDeclaration VisitTypeDeclaration(JavaTypeDeclaration node)
    {
        if (_javaLibrary is null)
            return node;

        // Collect field types for instance method call target resolution
        _fieldTypes.Clear();
        if (node is JavaClassDeclaration classDecl)
        {
            foreach (var field in classDecl.Fields)
            {
                if (!string.IsNullOrEmpty(field.Name) && !string.IsNullOrEmpty(field.Type))
                {
                    _fieldTypes[field.Name] = StripGenerics(field.Type);
                }
            }
            // Record components act as fields
            foreach (var comp in classDecl.RecordComponents)
            {
                if (!string.IsNullOrEmpty(comp.Name) && !string.IsNullOrEmpty(comp.Type))
                {
                    _fieldTypes[comp.Name] = StripGenerics(comp.Type);
                }
            }
        }
        else if (node is JavaEnumDeclaration enumDecl)
        {
            foreach (var field in enumDecl.Fields)
            {
                if (!string.IsNullOrEmpty(field.Name) && !string.IsNullOrEmpty(field.Type))
                {
                    _fieldTypes[field.Name] = StripGenerics(field.Type);
                }
            }
        }
        else if (node is JavaInterfaceDeclaration ifaceDecl)
        {
            foreach (var field in ifaceDecl.Fields)
            {
                if (!string.IsNullOrEmpty(field.Name) && !string.IsNullOrEmpty(field.Type))
                {
                    _fieldTypes[field.Name] = StripGenerics(field.Type);
                }
            }
        }

        return base.VisitTypeDeclaration(node);
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        if (_javaLibrary is null)
            return node;

        _localTypes.Clear();

        var outerExceptions = new HashSet<string>(_pendingExceptions, StringComparer.Ordinal);
        _pendingExceptions.Clear();

        if (node.StructuredBody != null)
        {
            node.StructuredBody = VisitMethodBody(node.StructuredBody);
        }

        if (_pendingExceptions.Count > 0)
        {
            WrapMethodBody(node);
            _diagnostics.Info(
                $"Wrapped method body with try-catch for checked exceptions in method '{node.Name}'",
                location: null,
                code: "CS2J4005",
                category: "JavaApiValidation");
            _diagnosticCount++;
        }

        _pendingExceptions.Clear();
        foreach (var ex in outerExceptions)
            _pendingExceptions.Add(ex);

        return node;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        if (_javaLibrary is null)
            return node;

        _localTypes.Clear();

        var outerExceptions = new HashSet<string>(_pendingExceptions, StringComparer.Ordinal);
        _pendingExceptions.Clear();

        if (node.StructuredBody != null)
        {
            node.StructuredBody = VisitMethodBody(node.StructuredBody);
        }

        if (_pendingExceptions.Count > 0)
        {
            WrapConstructorBody(node);
            _diagnostics.Info(
                $"Wrapped constructor body with try-catch for checked exceptions in '{node.ClassName}'",
                location: null,
                code: "CS2J4005",
                category: "JavaApiValidation");
            _diagnosticCount++;
        }

        _pendingExceptions.Clear();
        foreach (var ex in outerExceptions)
            _pendingExceptions.Add(ex);

        return node;
    }

    public override JavaVariableDeclarationStatement VisitVariableDeclarationStatement(JavaVariableDeclarationStatement node)
    {
        // Track local variable types for instance method call target resolution
        if (!string.IsNullOrEmpty(node.Name) && !string.IsNullOrEmpty(node.Type))
        {
            _localTypes[node.Name] = StripGenerics(node.Type);
        }

        return base.VisitVariableDeclarationStatement(node);
    }

    /// <summary>
    /// Methods that always throw checked exceptions in Java, regardless of declaring type.
    /// e.g. <c>close()</c> comes from AutoCloseable which declares <c>throws Exception</c>.
    /// </summary>
    private static readonly HashSet<string> AlwaysThrowsMethods = new(StringComparer.Ordinal)
    {
        "close",
    };

    /// <summary>
    /// Java types whose constructors always throw checked exceptions.
    /// e.g. <c>new PrintWriter(String)</c> throws FileNotFoundException.
    /// </summary>
    private static readonly HashSet<string> AlwaysThrowsConstructors = new(StringComparer.Ordinal)
    {
        "PrintWriter",
        "FileInputStream",
        "FileOutputStream",
        "FileReader",
        "FileWriter",
        "RandomAccessFile",
        "Scanner",
        "Formatter",
    };

    public override JavaNewExpression VisitNewExpression(JavaNewExpression node)
    {
        if (!string.IsNullOrEmpty(node.Type))
        {
            var typeName = StripGenerics(node.Type);
            if (AlwaysThrowsConstructors.Contains(typeName))
            {
                _pendingExceptions.Add("Exception");
            }
            else if (_javaLibrary is not null)
            {
                var canonicalName = ResolveCanonical(typeName);
                if (canonicalName is not null)
                {
                    CollectCheckedExceptions(canonicalName, "<init>");
                }
            }
        }

        return base.VisitNewExpression(node);
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        if (_javaLibrary is not null && node.Target is not null)
        {
            // Special case: methods that always throw checked exceptions (e.g. close())
            if (AlwaysThrowsMethods.Contains(node.MethodName))
            {
                _pendingExceptions.Add("Exception");
            }
            else
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
        }

        return base.VisitMethodCallExpression(node);
    }

    private void CollectCheckedExceptions(string typeName, string methodName)
    {
        var methods = _javaLibrary!.FindMethods(typeName, methodName);
        foreach (var method in methods)
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

    private string? InferTargetType(JavaExpression target)
    {
        // Static method calls: class name starts with uppercase (e.g. "System", "ThreadHelper")
        if (target is JavaIdentifierExpression staticId
            && staticId.Name.Length > 0
            && char.IsUpper(staticId.Name[0]))
        {
            return staticId.Name;
        }

        // Instance method calls: look up field type first, then local variable type
        if (target is JavaIdentifierExpression instanceId)
        {
            var varName = instanceId.Name;
            if (_fieldTypes.TryGetValue(varName, out var fieldType))
                return fieldType;
            if (_localTypes.TryGetValue(varName, out var localType))
                return localType;
        }

        // Constructor calls: extract type from JavaNewExpression
        if (target is JavaNewExpression newExpr)
            return StripGenerics(newExpr.Type);

        return null;
    }

    private static string StripGenerics(string type)
    {
        if (!type.Contains('<'))
            return type;
        var idx = type.IndexOf('<');
        return type.Substring(0, idx);
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

    private static readonly Regex TryWithResourcesTypePattern = new(
        @"try\s*\(\s*(\w+)(?:<[^>]*>)?\s+\w+\s*=",
        RegexOptions.Compiled);

    private static readonly Regex CloseCallPattern = new(
        @"\.close\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CheckedConstructorPattern = new(
        @"new\s+(PrintWriter|FileInputStream|FileOutputStream|FileReader|FileWriter|RandomAccessFile|Scanner|Formatter)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CheckedReflectionCallPattern = new(
        @"Class\.forName\s*\(|\.getDeclaredConstructor\s*\(|\.newInstance\s*\(|\.getMethod\s*\(|\.getDeclaredMethod\s*\(|\.getField\s*\(|\.getDeclaredField\s*\(|\.getConstructor\s*\(|\.invoke\s*\(",
        RegexOptions.Compiled);

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        if (_javaLibrary is not null)
        {
            // Detect try-with-resources: close() calls on AutoCloseable types
            if (node.Code.Contains("try ("))
            {
                var matches = TryWithResourcesTypePattern.Matches(node.Code);
                foreach (Match match in matches)
                {
                    var typeName = match.Groups[1].Value;
                    if (typeName == "var")
                    {
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
                        _pendingExceptions.Add("Exception");
                    }
                }
            }

            // Detect standalone close() calls in raw statements.
            // In Java, close() comes from AutoCloseable which declares throws Exception.
            if (CloseCallPattern.IsMatch(node.Code))
            {
                _pendingExceptions.Add("Exception");
            }

            // Detect constructors that throw checked exceptions
            if (CheckedConstructorPattern.IsMatch(node.Code))
            {
                _pendingExceptions.Add("Exception");
            }

            // Detect reflection calls that throw checked exceptions (Class.forName, etc.)
            if (CheckedReflectionCallPattern.IsMatch(node.Code))
            {
                _pendingExceptions.Add("Exception");
            }
        }

        return node;
    }

    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        if (_javaLibrary is not null && !string.IsNullOrEmpty(node.Code))
        {
            if (CheckedReflectionCallPattern.IsMatch(node.Code))
            {
                _pendingExceptions.Add("Exception");
            }

            if (CheckedConstructorPattern.IsMatch(node.Code))
            {
                _pendingExceptions.Add("Exception");
            }

            if (CloseCallPattern.IsMatch(node.Code))
            {
                _pendingExceptions.Add("Exception");
            }
        }

        return node;
    }

    // ─── Body wrapping helpers ──────────────────────────────────────

    private static void WrapMethodBody(JavaMethodDeclaration node)
    {
        if (node.StructuredBody != null)
        {
            node.StructuredBody = WrapStructuredBody(node.StructuredBody);
        }
        else if (!string.IsNullOrWhiteSpace(node.Body))
        {
            node.Body = WrapStringBody(node.Body, node.IsBodyExpression);
            node.IsBodyExpression = false;
        }
    }

    private static void WrapConstructorBody(JavaConstructorDeclaration node)
    {
        if (node.StructuredBody != null)
        {
            node.StructuredBody = WrapStructuredBody(node.StructuredBody);
        }
        else if (!string.IsNullOrWhiteSpace(node.Body))
        {
            node.Body = WrapStringBody(node.Body, false);
        }
        else if (!string.IsNullOrEmpty(node.Initializer))
        {
            node.StructuredBody = new JavaMethodBody();
        }
    }

    private static JavaMethodBody WrapStructuredBody(JavaMethodBody body)
    {
        var tryStmt = new JavaTryCatchStatement();
        tryStmt.TryBody = new JavaBlockStatement();
        foreach (var stmt in body.Statements)
            tryStmt.TryBody.Statements.Add(stmt);

        var catchBody = new JavaBlockStatement();
        catchBody.Statements.Add(new JavaRawStatement("throw new RuntimeException(_e_cs2j);"));
        tryStmt.CatchClauses.Add(new JavaCatchClause
        {
            ExceptionType = "Exception",
            VariableName = "_e_cs2j",
            Body = catchBody,
        });

        return new JavaMethodBody { Statements = { tryStmt } };
    }

    private static string WrapStringBody(string body, bool isExpression)
    {
        var content = isExpression
            ? $"return {body.TrimEnd(';')};"
            : body;

        return $"try {{\n        {content}\n    }} catch (Exception _e_cs2j) {{\n        throw new RuntimeException(_e_cs2j);\n    }}";
    }
}
