using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Statement;

/// <summary>
/// 语句转换器
/// </summary>
public class StatementTransformer : IStatementTransformer
{
    public JavaSyntaxNode Transform(StatementSyntax node, ConversionContext context)
    {
        return node.Kind() switch
        {
            SyntaxKind.Block => new JavaStatementNode(TransformBlock(node as BlockSyntax, context)),
            SyntaxKind.ExpressionStatement => TransformExpressionStatement(node as ExpressionStatementSyntax, context),
            SyntaxKind.ReturnStatement => TransformReturnStatement(node as ReturnStatementSyntax, context),
            SyntaxKind.ThrowStatement => TransformThrowStatement(node as ThrowStatementSyntax, context),
            SyntaxKind.IfStatement => TransformIfStatement(node as IfStatementSyntax, context),
            SyntaxKind.WhileStatement => TransformWhileStatement(node as WhileStatementSyntax, context),
            SyntaxKind.ForStatement => TransformForStatement(node as ForStatementSyntax, context),
            SyntaxKind.ForEachStatement => TransformForEachStatement(node as ForEachStatementSyntax, context),
            SyntaxKind.DoStatement => TransformDoStatement(node as DoStatementSyntax, context),
            SyntaxKind.SwitchStatement => TransformSwitchStatement(node as SwitchStatementSyntax, context),
            SyntaxKind.TryStatement => TransformTryStatement(node as TryStatementSyntax, context),
            SyntaxKind.UsingStatement => TransformUsingStatement(node as UsingStatementSyntax, context),
            SyntaxKind.BreakStatement => new JavaStatementNode("break;"),
            SyntaxKind.ContinueStatement => new JavaStatementNode("continue;"),
            SyntaxKind.YieldReturnStatement => TransformYieldReturn(node as YieldStatementSyntax, context),
            SyntaxKind.YieldBreakStatement => TransformYieldBreak(node as YieldStatementSyntax, context),
            SyntaxKind.LockStatement => TransformLockStatement(node as LockStatementSyntax, context),
            SyntaxKind.FixedStatement => TransformFixedStatement(node as FixedStatementSyntax, context),
            SyntaxKind.UnsafeStatement => TransformUnsafeStatement(node as UnsafeStatementSyntax, context),
            SyntaxKind.EmptyStatement => new JavaStatementNode(""),
            SyntaxKind.LocalDeclarationStatement => TransformLocalDeclaration(node as LocalDeclarationStatementSyntax, context),
            _ => new JavaStatementNode($"/* TODO: {node.Kind()} - {node} */")
        };
    }

    public string TransformBlock(BlockSyntax block, ConversionContext context)
    {
        if (block == null) return "{}";

        var statements = TransformStatements(block.Statements, context);
        return string.Join("\n        ", statements);
    }

    public List<string> TransformStatements(SyntaxList<StatementSyntax> statements, ConversionContext context)
    {
        var results = new List<string>();

        foreach (var statement in statements)
        {
            var result = Transform(statement, context);
            if (result is JavaMemberCollection collection)
            {
                foreach (var member in collection.Members)
                {
                    var memberText = member.ToString("");
                    if (!string.IsNullOrWhiteSpace(memberText) &&
                        !memberText.TrimStart().StartsWith("#") &&
                        !memberText.TrimStart().StartsWith("/* TODO: UncheckedStatement"))
                    {
                        results.Add(memberText);
                    }
                }
            }
            else if (result is JavaStatementNode stmt)
            {
                var stmtText = stmt.ToString("");
                // 过滤掉空语句和 C# 预处理器指令残留
                if (!string.IsNullOrWhiteSpace(stmtText) &&
                    !stmtText.TrimStart().StartsWith("#") &&
                    !stmtText.TrimStart().StartsWith("/* TODO: UncheckedStatement"))
                {
                    results.Add(stmtText);
                }
            }
        }

        return results;
    }

    private JavaSyntaxNode TransformExpressionStatement(ExpressionStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // ConditionalAccessExpression (obj?.Method()) as a statement cannot be a ternary expression in Java.
        // Java only allows method calls, assignments, and new as expression statements.
        // Convert to: if (obj != null) { obj.Method(); }
        if (stmt.Expression is ConditionalAccessExpressionSyntax condAccess)
        {
            var objExpr = exprTransformer.Transform(condAccess.Expression, context);
            string innerCall;
            switch (condAccess.WhenNotNull)
            {
                case MemberBindingExpressionSyntax binding:
                    innerCall = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(binding.Name.Identifier.Text)};";
                    break;
                case InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax invokeBinding:
                    var args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => exprTransformer.Transform(a.Expression, context)));
                    innerCall = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(invokeBinding.Name.Identifier.Text)}({args});";
                    break;
                default:
                    // General case: ?.a.b(...) — recursively substitute the member binding with objExpr
                    innerCall = exprTransformer.TransformWhenNotNull(condAccess.WhenNotNull, objExpr, context) + ";";
                    break;
            }
            return new JavaStatementNode($"if ({objExpr} != null) {{ {innerCall} }}");
        }

        // Special case: dict.TryGetValue(key, out var v) as a standalone statement → v = dict.get(key);
        if (stmt.Expression is InvocationExpressionSyntax tvInvoc &&
            tvInvoc.Expression is MemberAccessExpressionSyntax tvMa &&
            tvMa.Name.Identifier.Text == "TryGetValue" &&
            tvInvoc.ArgumentList.Arguments.Count == 2)
        {
            var tvTarget = exprTransformer.Transform(tvMa.Expression, context);
            var tvKey = exprTransformer.Transform(tvInvoc.ArgumentList.Arguments[0].Expression, context);
            var tvArg2 = tvInvoc.ArgumentList.Arguments[1];
            if (tvArg2.Expression is DeclarationExpressionSyntax tvDecl2)
            {
                var declType = context.SemanticModel?.GetTypeInfo(tvDecl2.Type);
                var javaType = declType.HasValue && declType.Value.Type != null ? context.MapType(declType.Value.Type) : "var";
                var varName = tvDecl2.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : "_outVar";
                return new JavaStatementNode($"{javaType} {varName} = {tvTarget}.get({tvKey});");
            }
            else
            {
                var tvOut2 = exprTransformer.Transform(tvArg2.Expression, context);
                return new JavaStatementNode($"{tvOut2} = {tvTarget}.get({tvKey});");
            }
        }

        var expr = exprTransformer.Transform(stmt.Expression, context);

        // Drain any pre-statements emitted by the expression transformer
        // (e.g., chain property assignment: list.Add(obj.Prop = local = expr) splits into pre-stmts)
        if (context.HasPendingPreStatements)
        {
            var preStmts = context.DrainPreStatements();
            var preStmtLines = string.Join("\n", preStmts.Select(s => s + ";"));
            // If the main expression is just a simple variable or an out-param holder field access
            // (from chain-assignment hoisting), skip it as a statement — e.g. "anchors.value;" is
            // not valid Java. Valid Java statements must be method calls, assignments, etc.
            bool isNonStatement = expr.All(c => char.IsLetterOrDigit(c) || c == '_')
                || expr.EndsWith(".value");  // out-param XHolder field access (e.g. anchors.value)
            if (isNonStatement)
                return new JavaStatementNode(preStmtLines);
            return new JavaStatementNode(preStmtLines + "\n" + expr + ";");
        }

        return new JavaStatementNode(expr + ";");
    }

    private JavaSyntaxNode TransformThrowStatement(ThrowStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            // Java does not support bare 'throw;' — rethrow the enclosing catch variable
            var catchVar = stmt.Ancestors()
                .OfType<CatchClauseSyntax>()
                .FirstOrDefault();
            var rethrowName = catchVar?.Declaration?.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(rethrowName)) rethrowName = "_ex";
            return new JavaStatementNode($"throw {ConversionContext.EscapeJavaKeyword(rethrowName.Trim())};");
        }
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);
        return new JavaStatementNode($"throw {expr};");
    }

    private JavaSyntaxNode TransformReturnStatement(ReturnStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            return new JavaStatementNode("return;");
        }

        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);

        // Detect when a C# array element (from jagged array) is returned where a List<T> is expected.
        // e.g., return outEdges[vertex]; where the method returns IList<TEdge> → List<TEdge> in Java
        // and outEdges is TEdge[][] — element is TEdge[] which doesn't implement List<TEdge> in Java.
        if (stmt.Expression != null && context.SemanticModel != null)
        {
            var exprType = context.SemanticModel.GetTypeInfo(stmt.Expression).Type;
            if (exprType is IArrayTypeSymbol { Rank: 1 } arrayType)
            {
                // Check if the enclosing method's return type is IList<T>, ICollection<T>, or IEnumerable<T>
                var enclosingMethod = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetSym = null;
                if (enclosingMethod != null)
                    enclosingRetSym = context.SemanticModel.GetTypeInfo(enclosingMethod.ReturnType).Type;
                // Also check property accessor (return in a get { } block)
                if (enclosingRetSym == null)
                {
                    var propAccessor = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (propAccessor != null)
                    {
                        var propDecl = propAccessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl != null)
                            enclosingRetSym = context.SemanticModel.GetTypeInfo(propDecl.Type).Type;
                    }
                }
                if (enclosingRetSym is INamedTypeSymbol retNamed &&
                    retNamed.Name is "IList" or "ICollection" or "List" or "Collection"
                        or "IEnumerable" or "Iterable")
                {
                    expr = $"Arrays.asList({expr})";
                    context.AddImport("java.util.Arrays");
                }
            }

            // Detect when a Stream expression is returned from a method that declares Iterable/IEnumerable.
            // C# LINQ expressions become Java Streams but IEnumerable<T> maps to Iterable<T>.
            // Stream<T> does not implement Iterable<T>, so we need .collect(Collectors.toList()).
            var retExprType = context.SemanticModel?.GetTypeInfo(stmt.Expression).Type;
            bool isStreamReturn = retExprType is INamedTypeSymbol retNamed2 &&
                (retNamed2.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable") &&
                retNamed2.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
            if (isStreamReturn && (expr.Contains(".map(") || expr.Contains(".filter(") ||
                expr.Contains(".flatMap(") || expr.Contains(".select(") ||
                expr.Contains("stream(") || expr.Contains("Stream.concat") ||
                expr.Contains(".distinct(") || expr.Contains(".sorted(")))
            {
                // Check if enclosing method returns Iterable
                var enclosing = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetType = enclosing != null
                    ? context.SemanticModel?.GetTypeInfo(enclosing.ReturnType).Type
                    : null;
                // Also check property accessor (return inside a get { } block)
                if (enclosingRetType == null)
                {
                    var accessorDecl = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (accessorDecl != null)
                    {
                        var propDecl2 = accessorDecl.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl2 != null)
                            enclosingRetType = context.SemanticModel?.GetTypeInfo(propDecl2.Type).Type;
                    }
                }
                bool enclosingReturnsIterable = enclosingRetType is INamedTypeSymbol mret &&
                    mret.Name is "IEnumerable" or "ICollection" or "IList";
                // Don't double-collect: if the expression already ends with .toList() or ArrayList::new it's already a List
                bool alreadyCollected = expr.EndsWith(".toList())")
                    || expr.EndsWith("toList()))")
                    || expr.EndsWith("ArrayList::new))")
                    || expr.EndsWith("ArrayList::new)")
                    || expr.EndsWith(".toArray())")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)\)$")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)$");
                if (enclosingReturnsIterable && !alreadyCollected)
                {
                    context.AddImport("java.util.stream.Collectors");
                    expr = $"{expr}.collect(Collectors.toList())";
                }
            }
        }

        return new JavaStatementNode($"return {expr};");
    }

    private JavaSyntaxNode TransformIfStatement(IfStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var stmtTransformer = new StatementTransformer();

        var thenBlock = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{\n        {stmtTransformer.Transform(stmt.Statement, context).ToString("")}\n    }}";

        var result = new System.Text.StringBuilder();
        result.Append($"if ({condition}) {thenBlock}");

        if (stmt.Else != null)
        {
            var elseBlock = stmt.Else.Statement is BlockSyntax elseBlockSyntax
                ? $"{{\n        {TransformBlock(elseBlockSyntax, context)}\n    }}"
                : $"{{\n        {stmtTransformer.Transform(stmt.Else.Statement, context).ToString("")}\n    }}";
            result.Append($" else {elseBlock}");
        }

        return new JavaStatementNode(result.ToString());
    }

    private JavaSyntaxNode TransformWhileStatement(WhileStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"while ({condition}) {body}");
    }

    private JavaSyntaxNode TransformForStatement(ForStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // 初始值
        var initializers = "";
        if (stmt.Declaration != null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";
            var vars = stmt.Declaration.Variables.Select(v => {
                var init = v.Initializer != null ? $" = {exprTransformer.Transform(v.Initializer.Value, context)}" : "";
                return $"{v.Identifier}{init}";
            });
            initializers = $"{javaType} {string.Join(", ", vars)}";
        }
        else if (stmt.Initializers.Any())
        {
            initializers = string.Join(", ", stmt.Initializers.Select(i => exprTransformer.Transform(i, context)));
        }

        // 条件
        var condition = stmt.Condition != null
            ? exprTransformer.Transform(stmt.Condition, context)
            : "true";

        // 增量
        var incrementors = string.Join(", ", stmt.Incrementors.Select(i =>
            exprTransformer.Transform(i, context)));

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"for ({initializers}; {condition}; {incrementors}) {body}");
    }

    private JavaSyntaxNode TransformForEachStatement(ForEachStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Special case: foreach over a LINQ query with anonymous type select
        // e.g.: foreach (var pair in from A a in src from B b in tgt select new { aV = a, bV = b })
        // Transform as nested for loops, replacing pair.getaV() → a, pair.getbV() → b
        if (stmt.Expression is QueryExpressionSyntax queryExpr &&
            queryExpr.Body.SelectOrGroup is SelectClauseSyntax selectClause &&
            selectClause.Expression is AnonymousObjectCreationExpressionSyntax anonCreate)
        {
            return TransformForEachWithAnonymousQuery(stmt, queryExpr, anonCreate, context);
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Type);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";
        var identifier = ConversionContext.EscapeJavaKeyword(stmt.Identifier.Text);
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        // Detect: iterating over a Dictionary/Map → need .entrySet() in Java
        var exprTypeInfo = context.SemanticModel?.GetTypeInfo(stmt.Expression).Type;
        if (exprTypeInfo is INamedTypeSymbol exprNamed &&
            (exprNamed.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or
             "HashMap" or "TreeMap" or "LinkedHashMap" ||
             exprNamed.AllInterfaces.Any(i => i.Name is "IDictionary")))
        {
            // Map iteration: use entrySet()
            expression = $"{expression}.entrySet()";
            // javaType likely contains "AbstractMap.SimpleEntry" or "KeyValuePair" — normalize to "Map.Entry"
            if (javaType.Contains("SimpleEntry") || javaType.Contains("KeyValuePair") || javaType == "var")
            {
                // Try to get the proper Map.Entry type from the dictionary's type arguments
                if (exprNamed.IsGenericType && exprNamed.TypeArguments.Length >= 2)
                {
                    // Must use boxed types for Map.Entry generic args (primitives not allowed in generics)
                    static string BoxJavaType(string t) => t switch
                    {
                        "int" => "Integer", "long" => "Long", "double" => "Double",
                        "float" => "Float", "boolean" => "Boolean", "short" => "Short",
                        "byte" => "Byte", "char" => "Character", _ => t
                    };
                    var keyType = BoxJavaType(context.MapType(exprNamed.TypeArguments[0]));
                    var valType = BoxJavaType(context.MapType(exprNamed.TypeArguments[1]));
                    javaType = $"Map.Entry<{keyType}, {valType}>";
                    context.AddImport("java.util.Map");
                }
                else
                {
                    javaType = "Map.Entry<?, ?>";
                    context.AddImport("java.util.Map");
                }
            }
        }

        // Pre-process: StreamSupport.stream(...).toArray() used in foreach can't be iterated (Object[]).
        // Convert to .collect(Collectors.toList()) so the list is Iterable<T> and foreach works.
        {
            var trimExpr = expression.TrimEnd();
            if (trimExpr.EndsWith(".toArray()") && trimExpr.Contains("StreamSupport.stream("))
            {
                expression = trimExpr.Substring(0, trimExpr.Length - ".toArray()".Length)
                                 + ".collect(Collectors.toCollection(ArrayList::new))";
                context.AddImport("java.util.ArrayList");
            }
        }

        // Stream.concat(), StreamSupport.stream(), Arrays.stream(), etc.
        // Collect to List to allow break/continue/return in the loop body.
        // Use EndsWith check to avoid double-collecting an already-collected stream:
        // the expression may contain inner .collect() calls (e.g. spliterator wrapping)
        // but we only skip if the OUTERMOST call is already .collect(Collectors.toList()).
        bool isStream = !expression.TrimEnd().EndsWith(".collect(Collectors.toList())")
            && !expression.TrimEnd().EndsWith(".collect(Collectors.toCollection(ArrayList::new))")
            && !expression.TrimEnd().EndsWith(".toArray()")
            && !System.Text.RegularExpressions.Regex.IsMatch(expression.TrimEnd(), @"\.toArray\([^)]+\)$")
            && (
            expression.Contains("Stream.concat(") || expression.Contains("StreamSupport.stream(") ||
            expression.Contains("Arrays.stream(") || expression.Contains(".stream()") ||
            expression.Contains(".map(") ||
            expression.Contains(".filter(") ||
            expression.Contains(".flatMap(") ||
            expression.Contains(".sorted(") ||
            expression.Contains(".distinct(") ||
            expression.Contains(".limit(") ||
            expression.Contains(".skip(") ||
            expression.Contains(".peek(") ||
            expression.Contains(".mapToInt(") ||
            expression.Contains(".mapToLong(") ||
            expression.Contains(".mapToDouble(") ||
            expression.Contains(".mapToObj("));

        // Also detect by semantic type: if the C# expression type is IOrderedEnumerable or IQueryable
        // (both are always yielded as Java Streams by the LINQ translator), force-collect.
        if (!isStream && exprTypeInfo is INamedTypeSymbol csForeachType)
        {
            bool isLinqResult = csForeachType.Name is "IOrderedEnumerable" or "IOrderedQueryable" or "IQueryable"
                || (csForeachType.ContainingNamespace?.ToDisplayString().StartsWith("System.Linq") == true
                    && csForeachType.Name != "IEnumerable" && csForeachType.Name != "ICollection");
            if (isLinqResult)
                isStream = true;
        }

        // Fallback: if the foreach expression is a locally-declared variable that was registered
        // as stream-typed in TransformLocalDeclaration, treat it as a stream here too.
        // This handles cases where the SemanticModel is unavailable or the type is not in System.Linq.
        if (!isStream && context.StreamLocalVariables.Contains(expression.Trim()))
            isStream = true;

        if (isStream)
        {
            // Collect stream to list so that break/return/continue work in loop body
            context.AddImport("java.util.ArrayList");
            context.AddImport("java.util.stream.Collectors");
            expression = $"{expression}.collect(Collectors.toCollection(ArrayList::new))";
        }

        // Iterating a raw (non-generic) IEnumerable with a typed loop variable: in Java, iterating
        // a raw Iterable yields Object, which can't be assigned to a typed variable (compile error).
        // Cast the iterable to Iterable<ElementType> to make it compile (generates unchecked warning).
        if (!isStream && javaType != "Object" && javaType != "var"
            && exprTypeInfo is INamedTypeSymbol rawEnum
            && !rawEnum.IsGenericType
            && rawEnum.Name is "IEnumerable" or "ICollection")
        {
            expression = $"(Iterable<{javaType}>) ({expression})";
        }

        // Fix L: Downcast in foreach — C# allows implicitly downcasting the element type in foreach
        // (e.g., foreach (NetworkEdge e in IEnumerable<PolyIntEdge>)) but Java does not.
        // Detect via Roslyn's ForEachStatementInfo.ElementConversion.IsExplicit.
        if (!isStream && javaType != "var" && javaType != "Object" && context.SemanticModel != null)
        {
            try
            {
                var forEachInfo = context.SemanticModel.GetForEachStatementInfo(stmt);
                if (forEachInfo.ElementConversion.IsExplicit && !forEachInfo.ElementConversion.IsUserDefined
                    && forEachInfo.ElementType != null)
                {
                    // Wrap iterable with a double cast to suppress the type mismatch.
                    // At runtime the elements are already the correct subtype.
                    expression = $"(Iterable<{javaType}>)(Iterable<?>)({expression})";
                }
            }
            catch { /* SemanticModel may fail for some edge cases — skip silently */ }
        }

        return new JavaStatementNode($"for ({javaType} {identifier} : {expression}) {body}");
    }

    /// <summary>
    /// Handles: foreach (var pair in from A a in src from B b in tgt ... select new { aField = a, bField = b })
    /// by expanding to nested for-each loops, substituting the anonymous field accesses with the actual variables.
    /// </summary>
    private JavaSyntaxNode TransformForEachWithAnonymousQuery(
        ForEachStatementSyntax stmt,
        QueryExpressionSyntax queryExpr,
        AnonymousObjectCreationExpressionSyntax anonCreate,
        ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Collect all from-clauses (outer first, inner last)
        var froms = new List<(string varName, string javaType, string sourceExpr)>();

        // Outer from
        var outerFrom = queryExpr.FromClause;
        var outerSrc = exprTransformer.Transform(outerFrom.Expression, context);
        var outerSrcType = context.SemanticModel?.GetTypeInfo(outerFrom.Expression).Type;
        string outerIterType;
        if (outerFrom.Type is PredefinedTypeSyntax || outerFrom.Type?.IsKind(SyntaxKind.IdentifierName) == true)
        {
            var ti = context.SemanticModel?.GetTypeInfo(outerFrom.Type);
            outerIterType = ti.HasValue && ti.Value.Type != null ? context.MapType(ti.Value.Type) : "var";
        }
        else
        {
            outerIterType = "var";
        }
        froms.Add((outerFrom.Identifier.ValueText, outerIterType, outerSrc));

        // Inner from-clauses
        foreach (var clause in queryExpr.Body.Clauses)
        {
            if (clause is FromClauseSyntax innerFrom)
            {
                var innerSrc = exprTransformer.Transform(innerFrom.Expression, context);
                string innerIterType;
                if (innerFrom.Type is PredefinedTypeSyntax || innerFrom.Type?.IsKind(SyntaxKind.IdentifierName) == true)
                {
                    var ti2 = context.SemanticModel?.GetTypeInfo(innerFrom.Type);
                    innerIterType = ti2.HasValue && ti2.Value.Type != null ? context.MapType(ti2.Value.Type) : "var";
                }
                else
                {
                    innerIterType = "var";
                }
                froms.Add((innerFrom.Identifier.ValueText, innerIterType, innerSrc));
            }
            // Ignore orderby (TODO: sorting)
        }

        // Build a map from anonymous field name → actual variable name from select clause
        // e.g. new { sourceV = source, targetV = target } → sourceV→source, targetV→target
        var fieldToVar = new Dictionary<string, string>();
        foreach (var initializer in anonCreate.Initializers)
        {
            var fieldName = initializer.NameEquals?.Name.Identifier.ValueText
                            ?? (initializer.Expression is IdentifierNameSyntax id ? id.Identifier.ValueText : null);
            string? exprVarName = initializer.Expression is IdentifierNameSyntax id2 ? id2.Identifier.ValueText : null;
            if (fieldName != null && exprVarName != null)
                fieldToVar[fieldName] = exprVarName;
        }

        // The foreach identifier (e.g. "pair") is what downstream code accesses as pair.getXxx().
        // We need to transform the body and replace "pair.getSourceV()", "pair.getTargetV()" etc.
        // We do this by translating the body normally (which will emit pair.getFieldName())
        // and then doing string replacement of those accessor patterns.
        var forEachIdent = stmt.Identifier.Text;

        var stmtTransformer = new StatementTransformer();
        string body;
        if (stmt.Statement is BlockSyntax block)
        {
            body = $"{{\n        {TransformBlock(block, context)}\n    }}";
        }
        else
        {
            body = $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";
        }

        // Replace pair.getFieldName() with the actual variable name
        foreach (var (fieldName, varName) in fieldToVar)
        {
            // The getter follows JavaBean convention: capitalize first letter (e.g., sourceV -> getSourceV)
            var getterName = "get" + ToPascalCase(fieldName);
            body = body.Replace($"{forEachIdent}.{getterName}()", varName);
        }

        // Build nested for loops from outermost to innermost
        var sb = new System.Text.StringBuilder();
        var indent = "";
        for (int i = 0; i < froms.Count; i++)
        {
            var (varName, javaType, srcExpr) = froms[i];
            // Wrap stream expressions in collect for the outer loops (inner loops can stay as-is if Iterable)
            bool needsCollect = srcExpr.Contains("StreamSupport.stream(") || srcExpr.Contains("Arrays.stream(") ||
                (srcExpr.Contains(".map(") && !srcExpr.Contains(".collect(")) ||
                (srcExpr.Contains(".filter(") && !srcExpr.Contains(".collect("));
            if (needsCollect)
            {
                context.AddImport("java.util.stream.Collectors");
                srcExpr = $"{srcExpr}.collect(Collectors.toList())";
            }
            if (i == froms.Count - 1)
            {
                // Innermost: attach the body
                sb.Append($"{indent}for ({javaType} {varName} : {srcExpr}) {body}");
            }
            else
            {
                sb.AppendLine($"{indent}for ({javaType} {varName} : {srcExpr}) {{");
                indent += "    ";
            }
        }
        // Close outer loops
        for (int i = froms.Count - 2; i >= 0; i--)
        {
            indent = indent.Length >= 4 ? indent.Substring(4) : "";
            sb.AppendLine();
            sb.Append($"{indent}}}");
        }

        return new JavaStatementNode(sb.ToString());
    }

    private JavaSyntaxNode TransformDoStatement(DoStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"do {body} while ({condition});");
    }

    private JavaSyntaxNode TransformSwitchStatement(SwitchStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var sections = new List<string>();

        foreach (var section in stmt.Sections)
        {
            var labels = new List<string>();

            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CaseSwitchLabelSyntax caseLabel:
                        var transformedLabel = exprTransformer.Transform(caseLabel.Value, context);
                        if (context.SemanticModel?.GetSymbolInfo(caseLabel.Value).Symbol is IFieldSymbol fieldSymbol &&
                            fieldSymbol.ContainingType?.TypeKind == TypeKind.Enum)
                        {
                            // For [Flags] enums (now Java classes with static int fields), keep the qualified name
                            // so Java switch-on-int can use the compile-time constant (e.g. case Direction.North:)
                            if (context.IsFlagsEnum(fieldSymbol.ContainingType.Name))
                                transformedLabel = $"{fieldSymbol.ContainingType.Name}.{fieldSymbol.Name}";
                            else
                                transformedLabel = fieldSymbol.Name; // regular enum: unqualified name in switch
                        }
                        else if (caseLabel.Value is MemberAccessExpressionSyntax memberAccess)
                        {
                            // Check if the containing type is a flags enum - if so keep fully qualified
                            var maSymbol = context.SemanticModel?.GetSymbolInfo(memberAccess).Symbol;
                            if (maSymbol is IFieldSymbol maField && maField.ContainingType?.TypeKind == TypeKind.Enum
                                && context.IsFlagsEnum(maField.ContainingType.Name))
                                transformedLabel = $"{maField.ContainingType.Name}.{maField.Name}";
                            else
                                transformedLabel = memberAccess.Name.Identifier.Text;
                        }
                        else if (transformedLabel.Contains(".get"))
                        {
                            var parts = transformedLabel.Split(new[] { ".get" }, StringSplitOptions.None);
                            transformedLabel = parts.Last().Replace("()", "");
                        }
                        labels.Add($"case {transformedLabel}");
                        break;
                    case DefaultSwitchLabelSyntax:
                        labels.Add("default");
                        break;
                }
            }

            var stmtTransformer = new StatementTransformer();
            var statements = section.Statements.Select(s =>
                stmtTransformer.Transform(s, context).ToString("")).ToList();

            // Build case section header: combine multiple labels as "case X, Y:" or "default:"
            string sectionStr;
            var caseValues = labels.Where(l => l.StartsWith("case ")).Select(l => l.Substring(5)).ToList();
            var hasDefault = labels.Contains("default");
            if (hasDefault && caseValues.Count == 0)
            {
                sectionStr = "default:\n            " + string.Join("\n            ", statements);
            }
            else if (hasDefault)
            {
                sectionStr = "case " + string.Join(", ", caseValues) + ":\n            default:\n            " +
                             string.Join("\n            ", statements);
            }
            else
            {
                sectionStr = "case " + string.Join(", ", caseValues) + ":\n            " +
                             string.Join("\n            ", statements);
            }

            // 检查是否有 break
            var lastStmt = section.Statements.LastOrDefault();
            var hasBreak = lastStmt?.Kind() == SyntaxKind.BreakStatement ||
                          lastStmt?.Kind() == SyntaxKind.ReturnStatement;

            if (!hasBreak && statements.Count > 0)
            {
                sectionStr += "\n            break;"; // 添加 break 以防止 fall-through
            }

            sections.Add(sectionStr);
        }

        var bodyStr = string.Join("\n\n        ", sections);

        return new JavaStatementNode($"switch ({expression}) {{\n        {bodyStr}\n    }}");
    }

    private JavaSyntaxNode TransformTryStatement(TryStatementSyntax stmt, ConversionContext context)
    {
        var stmtTransformer = new StatementTransformer();
        var sb = new System.Text.StringBuilder();

        sb.Append("try ");
        sb.Append(stmt.Block is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : "{ }");

        // catch 块
        foreach (var catchClause in stmt.Catches)
        {
            string javaType = "Exception";
            string varName = "_ex";
            if (catchClause.Declaration != null)
            {
                var typeInfo = context.SemanticModel?.GetTypeInfo(catchClause.Declaration.Type);
                javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Exception";
                var rawVarName = catchClause.Declaration.Identifier.ValueText;
                if (!string.IsNullOrWhiteSpace(rawVarName))
                    varName = ConversionContext.EscapeJavaKeyword(rawVarName);
            }

            sb.Append($" catch ({javaType} {varName}) ");

            if (catchClause.Filter != null)
            {
                // Java 不支持 catch 过滤器，需要转换为内部 if
                var exprTransformer = ExpressionTransformerFacade.Instance;
                var filter = exprTransformer.Transform(catchClause.Filter.FilterExpression, context);
                sb.Append($"{{\n        if ({filter}) {{\n            {TransformBlock(catchClause.Block, context)}\n        }}\n    }}");
            }
            else
            {
                sb.Append(catchClause.Block is BlockSyntax catchBlock
                    ? $"{{\n        {TransformBlock(catchBlock, context)}\n    }}"
                    : "{ }");
            }
        }

        // finally 块
        if (stmt.Finally != null)
        {
            sb.Append(" finally ");
            sb.Append(stmt.Finally.Block is BlockSyntax finallyBlock
                ? $"{{\n        {TransformBlock(finallyBlock, context)}\n    }}"
                : "{ }");
        }

        return new JavaStatementNode(sb.ToString());
    }

    private JavaSyntaxNode TransformUsingStatement(UsingStatementSyntax stmt, ConversionContext context)
    {
        // Java 使用 try-with-resources
        var stmtTransformer = new StatementTransformer();
        var exprTransformer = ExpressionTransformerFacade.Instance;

        var resources = new List<string>();

        if (stmt.Declaration != null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "AutoCloseable";

            foreach (var variable in stmt.Declaration.Variables)
            {
                var init = variable.Initializer != null
                    ? $" = {exprTransformer.Transform(variable.Initializer.Value, context)}"
                    : "";
                resources.Add($"{javaType} {variable.Identifier}{init}");
            }
        }

        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"try ({string.Join("; ", resources)}) {body}");
    }

    private JavaSyntaxNode TransformLockStatement(LockStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"synchronized ({expression}) {body}");
    }

    private JavaSyntaxNode TransformFixedStatement(FixedStatementSyntax stmt, ConversionContext context)
    {
        context.Diagnostics.Error(
            "Java doesn't support fixed buffers. Manual conversion required.",
            stmt.GetLocation()
        );
        return new JavaStatementNode("/* TODO: Fixed statement - manual conversion required */");
    }

    private JavaSyntaxNode TransformUnsafeStatement(UnsafeStatementSyntax stmt, ConversionContext context)
    {
        context.Diagnostics.Error(
            "Java doesn't support unsafe code. Manual conversion required.",
            stmt.GetLocation()
        );
        return new JavaStatementNode("/* TODO: Unsafe statement - manual conversion required */");
    }

    private JavaSyntaxNode TransformLocalDeclaration(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
        string javaType;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            var resolvedType = typeInfo.Value.Type;
            // C# enumerator structs (e.g. Dictionary<K,V>.Enumerator, List<T>.Enumerator) have no Java equivalent.
            // The .iterator() call returns an Iterator<T>, so let Java infer the type with var.
            bool isEnumeratorStruct = resolvedType is INamedTypeSymbol nes
                && nes.Name == "Enumerator"
                && nes.ContainingType != null;
            // Also handle IEnumerator<T> mapped via GetEnumerator — use var so Java infers Iterator<T>
            bool isIEnumerator = resolvedType is INamedTypeSymbol ien
                && (ien.Name is "IEnumerator" or "IEnumerator`1");
            javaType = (isEnumeratorStruct || isIEnumerator) ? "var" : context.MapType(resolvedType);
            // Fallback: if MapType returns empty (e.g. unresolved error type), use var to let Java infer
            if (string.IsNullOrWhiteSpace(javaType))
                javaType = "var";
        }
        else
        {
            javaType = "var";
        }

        // When mapping C# IEnumerable<T>/ICollection<T> to Iterable<T> for a local variable,
        // use 'var' so Java infers the concrete return type (e.g. List<T>) from the initializer.
        // This prevents Collection<T> vs Iterable<T> compatibility issues (e.g. ArrayList.addAll).
        if (javaType == "Iterable" || javaType.StartsWith("Iterable<"))
            javaType = "var";

        // If the C# declaration used 'var' (implicit type) and had NO initializer, Java cannot infer the type.
        // We need to add a type + default initializer. Track whether the original C# type was implicit.
        bool wasImplicitVar = false;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            wasImplicitVar = stmt.Declaration.Type.IsVar;
        }
        else
        {
            wasImplicitVar = true;
        }

        // If we ended up with "var" and there is NO initializer, Java cannot infer the type.
        // Try to resolve the type from the local variable symbol instead.
        bool hasNoInitializer = stmt.Declaration.Variables.All(v => v.Initializer == null);
        bool wasConvertedFromVar = false;
        if (javaType == "var" && hasNoInitializer && context.SemanticModel != null)
        {
            foreach (var variable in stmt.Declaration.Variables)
            {
                var localSym = context.SemanticModel.GetDeclaredSymbol(variable) as ILocalSymbol;
                if (localSym?.Type != null && localSym.Type is not IErrorTypeSymbol)
                {
                    javaType = context.MapType(localSym.Type);
                    wasConvertedFromVar = true;
                    break;
                }
            }
        }

        // If this is a primitive-typed variable, check if it's used as an out-argument to TryGetValue.
        // In that case, Java can't compare the result of map.get() (Integer) to null when assigned to
        // a primitive (int). Use the boxed type (Integer, Long, etc.) instead.
        // This applies whether or not the variable has an initializer (e.g. "int x = 0" also needs boxing
        // when used as out-param to TryGetValue).
        {
            string? boxedType = javaType switch
            {
                "int" => "Integer", "long" => "Long", "double" => "Double",
                "float" => "Float", "boolean" => "Boolean", "short" => "Short",
                "byte" => "Byte", "char" => "Character", _ => null
            };
            if (boxedType != null && stmt.Parent is BlockSyntax parentBlock)
            {
                foreach (var variable in stmt.Declaration.Variables)
                {
                    var varName = variable.Identifier.Text;
                    bool usedAsTryGetValueOut = parentBlock.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma2
                            && ma2.Name.Identifier.Text is "TryGetValue" or "TryGetComponent"
                            && inv.ArgumentList.Arguments.Any(a =>
                                a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                                && a.Expression is IdentifierNameSyntax id
                                && id.Identifier.Text == varName));
                    if (usedAsTryGetValueOut)
                    {
                        javaType = boxedType;
                        break;
                    }
                }
            }
        }

        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Special case: var x = target.Property = value
        // Property setters return void in Java — split into two statements: "Type x = value; target.setProperty(x);"
        if (stmt.Declaration.Variables.Count == 1)
        {
            var sv = stmt.Declaration.Variables[0];
            if (sv.Initializer?.Value is AssignmentExpressionSyntax assignInit
                && assignInit.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
            {
                // Check if the LHS of the initializer-assignment is a property setter
                bool lhsIsProp = false;
                if (assignInit.Left is MemberAccessExpressionSyntax maLhsCheck)
                {
                    var symCheck = context.SemanticModel?.GetSymbolInfo(maLhsCheck).Symbol;
                    lhsIsProp = symCheck is IPropertySymbol || (symCheck == null && char.IsUpper(maLhsCheck.Name.Identifier.Text[0]));
                }
                else if (assignInit.Left is IdentifierNameSyntax idLhsCheck)
                {
                    var symCheck = context.SemanticModel?.GetSymbolInfo(idLhsCheck).Symbol;
                    lhsIsProp = symCheck is IPropertySymbol;
                }
                if (lhsIsProp)
                {
                    var varName = ConversionContext.EscapeJavaKeyword(sv.Identifier.Text);
                    var rhsExpr = exprTransformer.Transform(assignInit.Right, context);
                    // Build setter call using varName as the value argument
                    string setterCode;
                    if (assignInit.Left is MemberAccessExpressionSyntax maLhsSet)
                    {
                        var maTarget = exprTransformer.Transform(maLhsSet.Expression, context);
                        var maPropName = maLhsSet.Name.Identifier.Text;
                        setterCode = $"{maTarget}.set{maPropName}({varName})";
                    }
                    else if (assignInit.Left is IdentifierNameSyntax idLhsSet)
                    {
                        setterCode = $"set{idLhsSet.Identifier.Text}({varName})";
                    }
                    else
                    {
                        setterCode = exprTransformer.Transform(assignInit, context);
                    }
                    var preStmts = context.DrainPreStatements();
                    var preCode = preStmts.Count > 0 ? string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")) + "\n" : "";
                    return new JavaMemberCollection(
                        new JavaStatementNode($"{preCode}{javaType} {varName} = {rhsExpr};"),
                        new JavaStatementNode($"{setterCode};"));
                }
            }
        }

        var declarations = string.Join(", ", stmt.Declaration.Variables.Select(v =>
        {
            string init;
            if (v.Initializer != null)
            {
                var initExpr = exprTransformer.Transform(v.Initializer.Value, context);
                // When the declared type is a primitive array (e.g., int[]) and the initializer is
                // a generic method whose original return type is T[] (type-parameter array),
                // Java generics substitute T with the boxed type (Integer[]) — we must unbox it.
                if (javaType is "int[]" or "long[]" or "double[]" or "float[]" or "boolean[]"
                    && v.Initializer.Value is InvocationExpressionSyntax unboxInvExpr
                    && context.SemanticModel != null)
                {
                    var invSym2 = context.SemanticModel.GetSymbolInfo(unboxInvExpr).Symbol as IMethodSymbol;
                    if (invSym2?.OriginalDefinition.ReturnType is IArrayTypeSymbol origRetArr2
                        && origRetArr2.ElementType is ITypeParameterSymbol
                        // Skip if already converted by TransformLinqToArray (contains mapToDouble/mapToInt etc.)
                        && !initExpr.Contains(".mapToDouble(") && !initExpr.Contains(".mapToInt(") && !initExpr.Contains(".mapToLong("))
                    {
                        context.AddImport("java.util.Arrays");
                        initExpr = javaType switch
                        {
                            "int[]" => $"Arrays.stream({initExpr}).mapToInt(Integer::intValue).toArray()",
                            "long[]" => $"Arrays.stream({initExpr}).mapToLong(Long::longValue).toArray()",
                            "double[]" => $"Arrays.stream({initExpr}).mapToDouble(Double::doubleValue).toArray()",
                            _ => initExpr
                        };
                    }
                }
                // ArrayList<T> cannot be directly assigned from Stream.collect(Collectors.toList()) which returns List<T>.
                // Wrap with new ArrayList<>(...) to produce a concrete ArrayList type.
                // (Not needed when already using Collectors.toCollection(ArrayList::new) - that already returns ArrayList<T>.)
                if (javaType.StartsWith("ArrayList<") && initExpr.Contains(".collect(Collectors.toList())"))
                    initExpr = $"new ArrayList<>({initExpr})";

                // Fix K3: When the C# declared type is IEnumerable<T>/ICollection<T>/IList<T> (→ Java Iterable<T>)
                // but the initializer ends with .toArray(T[]::new), the assignment would fail because
                // T[] is NOT Iterable<T> in Java. Replace .toArray(T[]::new) with .collect(Collectors.toList()).
                if ((javaType.StartsWith("Iterable<") || javaType.StartsWith("List<") || javaType.StartsWith("Collection<"))
                    && System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]+::new\)$"))
                {
                    initExpr = System.Text.RegularExpressions.Regex.Replace(
                        initExpr.TrimEnd(), @"\.toArray\([^)]+::new\)$", ".collect(Collectors.toList())");
                    context.AddImport("java.util.stream.Collectors");
                }

                // Track stream-typed local variables for subsequent for-each statements.
                // When the initializer is a Java stream expression (not already collected), register
                // the variable name so TransformForEachStatement can detect it.
                {
                    bool initLooksLikeStream = !initExpr.Contains(".collect(Collectors.toList())")
                        && !initExpr.Contains(".collect(Collectors.toCollection(ArrayList::new))")
                        // If it ends with .toArray(...), the stream was already terminated to an array —
                        // the variable is T[], not a stream, so do NOT register it as a stream variable.
                        && !initExpr.TrimEnd().EndsWith(".toArray()")
                        && !System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]*\)$")
                        && (initExpr.Contains(".sorted(") || initExpr.Contains(".filter(") ||
                            initExpr.Contains(".map(") || initExpr.Contains(".flatMap(") ||
                            initExpr.Contains("StreamSupport.stream(") || initExpr.Contains("Arrays.stream(") ||
                            initExpr.Contains(".stream()") || initExpr.Contains("Stream.concat(") ||
                            initExpr.Contains(".distinct(") || initExpr.Contains(".limit(") ||
                            initExpr.Contains(".skip(") || initExpr.Contains(".peek("));
                    if (initLooksLikeStream)
                        context.StreamLocalVariables.Add(v.Identifier.Text);
                }
                init = $" = {initExpr}";
                // Java cannot auto-box int to Double/Float (only int→Integer is supported).
                // When a boxed Double/Float local is initialized with an int literal, widen it.
                if (javaType == "Double" && IsIntegerLiteralString(initExpr))
                    init = $" = {initExpr}.0";
                else if (javaType == "Float" && IsIntegerLiteralString(initExpr))
                    init = $" = {initExpr}f";
            }
            else if (wasConvertedFromVar && javaType != "var" && javaType != "Object")
            {
                // The original C# used 'var' without initializer — Java needs a type with default.
                // Use type-appropriate defaults: primitives get their zero value, reference types get null.
                init = javaType switch
                {
                    "int" or "short" or "byte" or "long" or "char" => " = 0",
                    "double" or "float" => " = 0.0",
                    "boolean" => " = false",
                    _ => " = null"  // reference type
                };
            }
            else
            {
                init = "";
            }
            return $"{ConversionContext.EscapeJavaKeyword(v.Identifier.Text)}{init}";
        }));

        return new JavaStatementNode($"{javaType} {declarations};");
    }

    private JavaSyntaxNode TransformYieldReturn(YieldStatementSyntax? stmt, ConversionContext context)
    {
        if (stmt?.Expression == null)
            return new JavaStatementNode("// yield return (empty)");
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);
        return new JavaStatementNode($"_yieldResult.add({expr});");
    }

    private JavaSyntaxNode TransformYieldBreak(YieldStatementSyntax? stmt, ConversionContext context)
    {
        return new JavaStatementNode("return _yieldResult;");
    }

    /// <summary>
    /// Returns true when <paramref name="s"/> is a bare integer literal string (possibly negative),
    /// e.g. "0", "1", "-1", "42". Used to detect int literals that need widening to Double/Float.
    /// </summary>
    private static bool IsIntegerLiteralString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("-") || s.StartsWith("+")) s = s.Substring(1).Trim();
        return s.Length > 0 && s.All(char.IsDigit);
    }

    /// <summary>
    /// Converts a string to PascalCase by capitalizing the first letter.
    /// Used for generating JavaBean-compliant getter method names.
    /// </summary>
    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0]) + name.Substring(1);
    }
}

/// <summary>
/// Java 语句节点
/// </summary>
internal class JavaStatementNode : JavaSyntaxNode
{
    private readonly string _statement;

    public JavaStatementNode(string statement)
    {
        _statement = statement;
    }

    public override string ToString(string indentation)
    {
        return _statement;
    }
}



