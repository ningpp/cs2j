using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// Post-emit IR rewriter that detects and fixes duplicate local variable names within
/// the same method scope.  When a variable is declared with a name that shadows or conflicts
/// with another variable in the same or enclosing scope, a numeric suffix is appended
/// (e.g. <c>result</c> → <c>result_1</c>).
///
/// <para>Operates on structured IR (<see cref="JavaVariableDeclarationStatement"/>)
/// and also scans <see cref="JavaRawStatement"/> blocks for variable declarations.</para>
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
        // Remove renames that were scoped to this block.
        // The scope contains the declared name (which may be the renamed name).
        // We need to find the original name in _renameMap that maps to this declared name.
        foreach (var name in scope)
        {
            var originalName = _renameMap.FirstOrDefault(kvp => kvp.Value == name).Key;
            if (originalName != null)
                _renameMap.Remove(originalName);
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

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        for (int i = 1; i < name.Length; i++)
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_') return false;
        return true;
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

        if (IsValidIdentifier(result.Name)
            && IsDeclaredInCurrentOrParentScope(result.Name))
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

    public override JavaTryCatchStatement VisitTryCatchStatement(JavaTryCatchStatement node)
    {
        // Visit try body
        node.TryBody = (JavaBlockStatement)VisitStatement(node.TryBody);

        // Visit catch clauses — handle catch variable deduplication
        foreach (var catchClause in node.CatchClauses)
        {
            PushScope(); // catch clause scope (includes catch variable)

            string? originalCatchVarName = catchClause.VariableName;
            if (!string.IsNullOrEmpty(catchClause.VariableName))
            {
                if (IsDeclaredInCurrentOrParentScope(catchClause.VariableName))
                {
                    var newName = AllocateUniqueName(catchClause.VariableName);
                    _renameMap[catchClause.VariableName] = newName;
                    catchClause.VariableName = newName;
                    _rewriteCount++;
                }
                DeclareVariable(catchClause.VariableName);
            }

            catchClause.Body = (JavaBlockStatement)VisitStatement(catchClause.Body);

            PopScope(); // catch clause scope

            // Clean up rename map for catch variable (it's scoped to the catch block)
            if (!string.IsNullOrEmpty(originalCatchVarName) && _renameMap.ContainsKey(originalCatchVarName))
            {
                _renameMap.Remove(originalCatchVarName);
            }
        }

        // Visit finally body
        if (node.FinallyBody != null)
        {
            node.FinallyBody = (JavaBlockStatement)VisitStatement(node.FinallyBody);
        }

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

    // Regex for variable declarations in raw statements: Type varName = ... or Type varName;
    // Matches: UriFormatException e = ..., MemorySegment str = ..., int x = ..., etc.
    private static readonly Regex RawVarDeclPattern = new(
        @"(?:^|(?<=\s))((?:(?:final|volatile)\s+)?[\w.]+(?:<[^>]+>)?(?:\[\])*)\s+(\w+)\s*([=;])",
        RegexOptions.Compiled);

    // Java keywords that should not be treated as variable names
    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "while", "for", "do", "switch", "case", "default",
        "try", "catch", "finally", "throw", "throws", "return", "break",
        "continue", "new", "class", "interface", "extends", "implements",
        "import", "package", "public", "private", "protected", "static",
        "final", "void", "abstract", "synchronized", "volatile", "transient",
        "native", "strictfp", "assert", "enum", "instanceof", "super", "this",
        "true", "false", "null", "goto", "const"
    };

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        var code = node.Code;

        // First, apply existing renames from _renameMap to variable references in the raw statement
        // Use word boundary matching to avoid renaming within type names or method names
        foreach (var kvp in _renameMap)
        {
            var originalName = kvp.Key;
            if (!IsValidIdentifier(originalName))
                continue;
            var renamedName = kvp.Value;
            // Only rename standalone identifiers (word boundary matching)
            code = Regex.Replace(code, $@"\b{Regex.Escape(originalName)}\b", renamedName);
        }

        // Then, scan for variable declarations and check for conflicts
        var matches = RawVarDeclPattern.Matches(code);
        foreach (Match match in matches)
        {
            var varName = match.Groups[2].Value;

            // Skip Java keywords
            if (JavaKeywords.Contains(varName))
                continue;

            // Skip if already renamed (has a suffix like _1, _2, etc.)
            if (char.IsDigit(varName[^1]) && varName.Contains('_'))
                continue;

            // Skip non-identifier names (numeric literals, etc.)
            if (!IsValidIdentifier(varName))
                continue;

            // Check if the variable name conflicts with an existing declaration
            if (IsDeclaredInCurrentOrParentScope(varName))
            {
                var newName = AllocateUniqueName(varName);
                _renameMap[varName] = newName;

                // Rename the variable in the raw statement text (declaration and all references)
                code = Regex.Replace(code, $@"\b{Regex.Escape(varName)}\b", newName);
                _rewriteCount++;
                DeclareVariable(newName);
            }
            else
            {
                DeclareVariable(varName);
            }
        }

        if (code != node.Code)
        {
            node.Code = code;
        }
        return node;
    }
}
