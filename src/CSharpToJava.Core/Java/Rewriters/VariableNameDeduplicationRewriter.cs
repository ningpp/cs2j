namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit IR rewriter that detects and fixes duplicate local variable names within
/// the same method scope.  When a variable is declared with a name that shadows or conflicts
/// with another variable in the same or enclosing scope, a numeric suffix is appended
/// (e.g. <c>result</c> → <c>result_1</c>).
///
/// <para>Only operates on structured IR (<see cref="JavaVariableDeclarationStatement"/>).
/// Variables in <see cref="JavaRawStatement"/> blocks are not tracked.</para>
/// </summary>
public sealed class VariableNameDeduplicationRewriter : JavaSyntaxRewriter
{
    private readonly Stack<HashSet<string>> _scopeStack = new();
    private readonly Dictionary<string, string> _renameMap = new();
    private int _rewriteCount;

    /// <summary>Number of renames applied during the last traversal.</summary>
    public int RewriteCount => _rewriteCount;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    // ─── Scope management ───────────────────────────────────────

    private void PushScope() => _scopeStack.Push(new HashSet<string>());

    private void PopScope()
    {
        var scope = _scopeStack.Pop();
        // Remove renames that were scoped to this block
        foreach (var name in scope)
        {
            if (_renameMap.ContainsKey(name) && !IsInAnyScope(name))
                _renameMap.Remove(name);
        }
    }

    private bool IsInAnyScope(string name)
    {
        foreach (var scope in _scopeStack)
        {
            if (scope.Contains(name))
                return true;
        }
        return false;
    }

    private bool IsDeclaredInCurrentOrParentScope(string name)
    {
        foreach (var scope in _scopeStack)
        {
            if (scope.Contains(name))
                return true;
        }
        return false;
    }

    private void DeclareVariable(string name)
    {
        if (_scopeStack.Count > 0)
            _scopeStack.Peek().Add(name);
    }

    private string AllocateUniqueName(string name)
    {
        int suffix = 1;
        string candidate;
        do
        {
            candidate = $"{name}_{suffix}";
            suffix++;
        } while (IsDeclaredInCurrentOrParentScope(candidate));
        return candidate;
    }

    // ─── Traversal overrides ────────────────────────────────────

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        PushScope();
        _renameMap.Clear();

        foreach (var param in node.Parameters)
            DeclareVariable(param.Name);

        var result = base.VisitMethodDeclaration(node);
        PopScope();
        return result;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        PushScope();
        _renameMap.Clear();

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
        // Visit initializer first (may reference renamed variables)
        var result = base.VisitVariableDeclarationStatement(node);

        if (IsDeclaredInCurrentOrParentScope(result.Name))
        {
            var newName = AllocateUniqueName(result.Name);
            _renameMap[result.Name] = newName;
            result.Name = newName;
            _rewriteCount++;
        }

        DeclareVariable(result.Name);
        return result;
    }

    public override JavaForEachStatement VisitForEachStatement(JavaForEachStatement node)
    {
        node.Collection = VisitExpression(node.Collection);

        PushScope();

        if (IsDeclaredInCurrentOrParentScope(node.VariableName))
        {
            var newName = AllocateUniqueName(node.VariableName);
            _renameMap[node.VariableName] = newName;
            node.VariableName = newName;
            _rewriteCount++;
        }
        DeclareVariable(node.VariableName);

        node.Body = VisitStatement(node.Body);
        PopScope();
        return node;
    }

    public override JavaIdentifierExpression VisitIdentifierExpression(JavaIdentifierExpression node)
    {
        if (_renameMap.TryGetValue(node.Name, out var renamed))
        {
            return new JavaIdentifierExpression { Name = renamed };
        }
        return node;
    }
}
