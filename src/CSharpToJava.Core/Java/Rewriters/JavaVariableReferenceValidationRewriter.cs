using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit IR validation rewriter that checks all variable references have a corresponding
/// declaration in scope.  Traverses the IR tree, maintains a scope stack, and reports
/// undeclared variable references as CS2J5001 diagnostics.
///
/// <para>Limitations: Only tracks structured IR declarations (<see cref="JavaVariableDeclarationStatement"/>
/// and <see cref="JavaForEachStatement"/> loop variables).  Variables declared in raw strings
/// (<see cref="JavaRawStatement"/>) are not tracked and will not trigger false positives because
/// raw identifier expressions are also not checked.</para>
/// </summary>
public sealed class JavaVariableReferenceValidationRewriter : JavaSyntaxRewriter
{
    private readonly DiagnosticCollector _diagnostics;
    private readonly Stack<HashSet<string>> _scopeStack = new();
    private int _diagnosticCount;

    /// <summary>Number of diagnostics emitted during the last traversal.</summary>
    public int DiagnosticCount => _diagnosticCount;

    public JavaVariableReferenceValidationRewriter(DiagnosticCollector diagnostics)
    {
        _diagnostics = diagnostics;
    }

    // ─── Scope helpers ──────────────────────────────────────────

    private void PushScope() => _scopeStack.Push(new HashSet<string>());

    private void PopScope() => _scopeStack.Pop();

    private void DeclareVariable(string name)
    {
        if (_scopeStack.Count > 0)
            _scopeStack.Peek().Add(name);
    }

    private bool IsVariableDeclared(string name)
    {
        foreach (var scope in _scopeStack)
        {
            if (scope.Contains(name))
                return true;
        }
        return false;
    }

    // ─── Traversal ──────────────────────────────────────────────

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _diagnosticCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        PushScope();

        // Register fields as available identifiers
        foreach (var field in node.Fields)
            DeclareVariable(field.Name);

        var result = base.VisitClassDeclaration(node);
        PopScope();
        return result;
    }

    public override JavaInterfaceDeclaration VisitInterfaceDeclaration(JavaInterfaceDeclaration node)
    {
        PushScope();
        foreach (var field in node.Fields)
            DeclareVariable(field.Name);
        var result = base.VisitInterfaceDeclaration(node);
        PopScope();
        return result;
    }

    public override JavaEnumDeclaration VisitEnumDeclaration(JavaEnumDeclaration node)
    {
        PushScope();
        foreach (var field in node.Fields)
            DeclareVariable(field.Name);
        foreach (var val in node.Values)
            DeclareVariable(val);
        var result = base.VisitEnumDeclaration(node);
        PopScope();
        return result;
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        PushScope();

        // Register parameters
        foreach (var param in node.Parameters)
            DeclareVariable(param.Name);

        var result = base.VisitMethodDeclaration(node);
        PopScope();
        return result;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        PushScope();
        foreach (var param in node.Parameters)
            DeclareVariable(param.Name);
        var result = base.VisitConstructorDeclaration(node);
        PopScope();
        return result;
    }

    public override JavaBlockStatement VisitBlockStatement(JavaBlockStatement node)
    {
        PushScope();
        var result = base.VisitBlockStatement(node);
        PopScope();
        return result;
    }

    public override JavaVariableDeclarationStatement VisitVariableDeclarationStatement(JavaVariableDeclarationStatement node)
    {
        // Visit initializer before declaring the variable (can't reference itself)
        var result = base.VisitVariableDeclarationStatement(node);
        DeclareVariable(node.Name);
        return result;
    }

    public override JavaForEachStatement VisitForEachStatement(JavaForEachStatement node)
    {
        node.Collection = VisitExpression(node.Collection);
        PushScope();
        DeclareVariable(node.VariableName);
        node.Body = VisitStatement(node.Body);
        PopScope();
        return node;
    }

    public override JavaIdentifierExpression VisitIdentifierExpression(JavaIdentifierExpression node)
    {
        // Skip well-known identifiers that are always available
        if (IsWellKnownIdentifier(node.Name))
            return node;

        if (!IsVariableDeclared(node.Name))
        {
            _diagnosticCount++;
            _diagnostics.Warning(
                $"Variable '{node.Name}' may not be declared in the current scope",
                code: "CS2J5001",
                category: "ir-validation");
        }

        return node;
    }

    private static bool IsWellKnownIdentifier(string name)
    {
        // Java keywords that are valid identifiers in certain contexts,
        // class names, 'this', 'super', 'null', 'true', 'false', System types, etc.
        return name is "this" or "super" or "null" or "true" or "false"
            or "System" or "Math" or "String" or "Object" or "Integer"
            or "Long" or "Double" or "Float" or "Boolean" or "Character"
            or "Byte" or "Short" or "Arrays" or "Collections" or "Collectors"
            or "Optional" or "Stream" or "IntStream" or "LongStream" or "DoubleStream"
            or "CompletableFuture" or "List" or "Map" or "Set" or "HashMap" or "ArrayList"
            or "HashSet" or "TreeMap" or "TreeSet" or "LinkedList" or "Queue"
            or "Comparator" or "Iterator" or "Iterable" or "Class" or "Enum"
            or "RuntimeException" or "Exception" or "Throwable" or "Thread"
            or "Runnable" or "Callable" or "Objects" or "StreamSupport";
    }
}
