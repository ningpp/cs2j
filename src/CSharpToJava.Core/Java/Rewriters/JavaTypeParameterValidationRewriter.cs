using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit IR validation rewriter that checks generic type parameter usage consistency.
///
/// <para>Detected problems:
/// <list type="bullet">
///   <item><c>CS2J5003</c> — generic type used without required type arguments
///   (e.g. <c>new Inner()</c> instead of <c>new Inner&lt;T&gt;()</c>)</item>
/// </list>
/// </para>
///
/// <para>This rewriter is purely diagnostic and does not modify the IR tree.</para>
/// </summary>
public sealed class JavaTypeParameterValidationRewriter : JavaSyntaxRewriter
{
    private readonly DiagnosticCollector _diagnostics;
    private int _diagnosticCount;

    // Tracks types that have type parameters, keyed by simple name
    private readonly Dictionary<string, int> _genericTypes = new();
    private string? _currentClassName;

    /// <summary>Number of diagnostics emitted during the last traversal.</summary>
    public int DiagnosticCount => _diagnosticCount;

    public JavaTypeParameterValidationRewriter(DiagnosticCollector diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _diagnosticCount = 0;
        _genericTypes.Clear();

        // First pass: collect type parameter info from all type declarations
        foreach (var typeDecl in node.TypeDeclarations)
            CollectTypeParamInfo(typeDecl);

        // Second pass: validate usage
        return base.VisitCompilationUnit(node);
    }

    private void CollectTypeParamInfo(JavaTypeDeclaration type)
    {
        if (type.TypeParameters.Count > 0)
            _genericTypes[type.Name] = type.TypeParameters.Count;

        if (type is JavaClassDeclaration classDecl)
        {
            foreach (var nested in classDecl.NestedTypes)
                CollectTypeParamInfo(nested);
        }
        else if (type is JavaInterfaceDeclaration ifaceDecl)
        {
            foreach (var nested in ifaceDecl.NestedTypes)
                CollectTypeParamInfo(nested);
        }
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var previousClassName = _currentClassName;
        _currentClassName = node.Name;

        // Validate extends clause
        if (node.ExtendedType != null)
            ValidateTypeArguments(node.ExtendedType, "extends clause");

        // Validate implements clauses
        foreach (var impl in node.ImplementedTypes)
            ValidateTypeArguments(impl, "implements clause");

        var result = base.VisitClassDeclaration(node);
        _currentClassName = previousClassName;
        return result;
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        if (node is JavaNewExpression newExpr)
        {
            ValidateTypeArguments(newExpr.Type, "new expression");
        }

        return base.VisitExpression(node);
    }

    public override JavaVariableDeclarationStatement VisitVariableDeclarationStatement(JavaVariableDeclarationStatement node)
    {
        ValidateTypeArguments(node.Type, $"variable '{node.Name}'");
        return base.VisitVariableDeclarationStatement(node);
    }

    /// <summary>
    /// Check if a type reference is missing required type arguments.
    /// E.g., if "Inner" is a generic type with 1 type parameter, writing "new Inner()"
    /// instead of "new Inner&lt;T&gt;()" is likely a bug.
    /// </summary>
    private void ValidateTypeArguments(string javaType, string context)
    {
        if (string.IsNullOrEmpty(javaType))
            return;

        // Extract the base type name (before any < or [ )
        var baseName = ExtractBaseTypeName(javaType);

        // Skip if the type already has type arguments (contains '<')
        if (javaType.Contains('<'))
            return;

        // Skip diamond operator patterns — they're common in Java
        if (javaType.Contains("<>"))
            return;

        // Skip if this type is not in our known generic types
        if (!_genericTypes.TryGetValue(baseName, out var expectedCount))
            return;

        // Skip self-references in the same class (Java allows raw type in own constructors)
        if (baseName == _currentClassName)
            return;

        _diagnosticCount++;
        _diagnostics.Warning(
            $"Type '{baseName}' requires {expectedCount} type argument(s) but none were provided in {context}",
            code: "CS2J5003",
            category: "ir-validation");
    }

    private static string ExtractBaseTypeName(string type)
    {
        var idx = type.IndexOf('<');
        if (idx > 0) return type[..idx].Trim();

        idx = type.IndexOf('[');
        if (idx > 0) return type[..idx].Trim();

        return type.Trim();
    }
}
