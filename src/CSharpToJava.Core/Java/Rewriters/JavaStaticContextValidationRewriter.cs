using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit IR validation rewriter that checks for static/instance context mismatches.
///
/// <para>Detected problems:
/// <list type="bullet">
///   <item><c>CS2J5002</c> — static method references a class-level type parameter
///   (Java does not allow static methods to use type parameters declared on the enclosing class)</item>
/// </list>
/// </para>
///
/// <para>This rewriter is purely diagnostic and does not modify the IR tree.</para>
/// </summary>
public sealed class JavaStaticContextValidationRewriter : JavaSyntaxRewriter
{
    private readonly DiagnosticCollector _diagnostics;
    private int _diagnosticCount;

    // State maintained during traversal
    private readonly HashSet<string> _currentClassTypeParams = new();
    private bool _isInStaticMethod;
    private string? _currentMethodName;
    private string? _currentClassName;

    /// <summary>Number of diagnostics emitted during the last traversal.</summary>
    public int DiagnosticCount => _diagnosticCount;

    public JavaStaticContextValidationRewriter(DiagnosticCollector diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _diagnosticCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var previousClassTypeParams = new HashSet<string>(_currentClassTypeParams);
        var previousClassName = _currentClassName;

        _currentClassName = node.Name;
        _currentClassTypeParams.Clear();
        foreach (var tp in node.TypeParameters)
            _currentClassTypeParams.Add(tp.Name);

        var result = base.VisitClassDeclaration(node);

        _currentClassTypeParams.Clear();
        foreach (var p in previousClassTypeParams)
            _currentClassTypeParams.Add(p);
        _currentClassName = previousClassName;

        return result;
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        var previousStatic = _isInStaticMethod;
        var previousMethodName = _currentMethodName;

        _isInStaticMethod = (node.Modifiers & JavaModifiers.Static) != 0;
        _currentMethodName = node.Name;

        // Collect method's own type params — these are allowed even in static methods
        var methodTypeParamNames = new HashSet<string>();
        foreach (var tp in node.TypeParameters)
            methodTypeParamNames.Add(tp.Name);

        if (_isInStaticMethod && _currentClassTypeParams.Count > 0)
        {
            // Check return type for class-level type parameters
            CheckTypeForClassTypeParam(node.ReturnType, methodTypeParamNames);

            // Check parameter types
            foreach (var param in node.Parameters)
                CheckTypeForClassTypeParam(param.Type, methodTypeParamNames);
        }

        var result = base.VisitMethodDeclaration(node);

        _isInStaticMethod = previousStatic;
        _currentMethodName = previousMethodName;
        return result;
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        if (_isInStaticMethod && _currentClassTypeParams.Count > 0)
        {
            // Check cast expressions for class-level type params
            if (node is JavaCastExpression cast)
                CheckTypeForClassTypeParam(cast.Type, null);

            // Check new expressions
            if (node is JavaNewExpression newExpr)
                CheckTypeForClassTypeParam(newExpr.Type, null);

            // Check instanceof patterns
            if (node is JavaInstanceOfExpression inst)
                CheckTypeForClassTypeParam(inst.Type, null);
        }

        return base.VisitExpression(node);
    }

    public override JavaVariableDeclarationStatement VisitVariableDeclarationStatement(JavaVariableDeclarationStatement node)
    {
        if (_isInStaticMethod && _currentClassTypeParams.Count > 0)
            CheckTypeForClassTypeParam(node.Type, null);

        return base.VisitVariableDeclarationStatement(node);
    }

    /// <summary>
    /// Check if a type string references a class-level type parameter that is
    /// not also declared as a method-level type parameter.
    /// </summary>
    private void CheckTypeForClassTypeParam(string javaType, HashSet<string>? methodTypeParamNames)
    {
        foreach (var classParam in _currentClassTypeParams)
        {
            // Skip if this type param is also declared on the method (shadows class param)
            if (methodTypeParamNames != null && methodTypeParamNames.Contains(classParam))
                continue;

            // Simple containment check: look for the type parameter as a standalone token
            // in the type string (e.g. "T", "List<T>", "Map<String, T>")
            if (ContainsTypeParam(javaType, classParam))
            {
                _diagnosticCount++;
                _diagnostics.Warning(
                    $"Static method '{_currentMethodName}' in class '{_currentClassName}' references " +
                    $"class-level type parameter '{classParam}' — Java does not allow this",
                    code: "CS2J5002",
                    category: "ir-validation");
                // Only report once per method per type parameter
                return;
            }
        }
    }

    private static bool ContainsTypeParam(string type, string param)
    {
        if (string.IsNullOrEmpty(type))
            return false;

        int idx = 0;
        while (idx < type.Length)
        {
            idx = type.IndexOf(param, idx, StringComparison.Ordinal);
            if (idx < 0) return false;

            // Check boundaries: param must be a standalone token
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(type[idx - 1]);
            int endIdx = idx + param.Length;
            bool rightOk = endIdx >= type.Length || !char.IsLetterOrDigit(type[endIdx]);

            if (leftOk && rightOk)
                return true;

            idx = endIdx;
        }

        return false;
    }
}
