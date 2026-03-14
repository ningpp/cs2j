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
            if (result is JavaStatementNode stmt)
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
        var exprTransformer = new ExpressionTransformer();

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
                    innerCall = $"{objExpr}./* TODO: conditionalAccess */{condAccess.WhenNotNull};";
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
            // If the main expression is just a simple variable (from chain-assignment hoisting),
            // skip it as a statement (it's a no-op). Otherwise include it.
            bool isSimpleIdentifier = expr.All(c => char.IsLetterOrDigit(c) || c == '_');
            if (isSimpleIdentifier)
                return new JavaStatementNode(preStmtLines);
            return new JavaStatementNode(preStmtLines + "\n" + expr + ";");
        }

        return new JavaStatementNode(expr + ";");
    }

    private JavaSyntaxNode TransformThrowStatement(ThrowStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            return new JavaStatementNode("throw;");
        }
        var exprTransformer = new ExpressionTransformer();
        var expr = exprTransformer.Transform(stmt.Expression, context);
        return new JavaStatementNode($"throw {expr};");
    }

    private JavaSyntaxNode TransformReturnStatement(ReturnStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            return new JavaStatementNode("return;");
        }

        var exprTransformer = new ExpressionTransformer();
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
                // Don't double-collect: if the expression already ends with .toList() it's already a List
                bool alreadyCollected = expr.EndsWith(".toList())") || expr.EndsWith("toList()))");
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
        var exprTransformer = new ExpressionTransformer();
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
        var exprTransformer = new ExpressionTransformer();
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"while ({condition}) {body}");
    }

    private JavaSyntaxNode TransformForStatement(ForStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = new ExpressionTransformer();

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
        var exprTransformer = new ExpressionTransformer();

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
                             + ".collect(Collectors.toList())";
                context.AddImport("java.util.stream.Collectors");
            }
        }

        // Check if the expression is a Stream (doesn't implement Iterable).
        // Stream.concat(), StreamSupport.stream(), Arrays.stream(), etc.
        // Collect to List to allow break/continue/return in the loop body.
        // Use EndsWith check to avoid double-collecting an already-collected stream:
        // the expression may contain inner .collect() calls (e.g. spliterator wrapping)
        // but we only skip if the OUTERMOST call is already .collect(Collectors.toList()).
        bool isStream = !expression.TrimEnd().EndsWith(".collect(Collectors.toList())")
            && !expression.TrimEnd().EndsWith(".toArray()")
            && !System.Text.RegularExpressions.Regex.IsMatch(expression.TrimEnd(), @"\.toArray\([^)]+\)$")
            && (
            expression.Contains("Stream.concat(") || expression.Contains("StreamSupport.stream(") ||
            expression.Contains("Arrays.stream(") || expression.Contains(".stream()") ||
            expression.Contains(".map(") ||
            expression.Contains(".filter(") ||
            expression.Contains(".flatMap("));

        if (isStream)
        {
            // Collect stream to list so that break/return/continue work in loop body
            context.AddImport("java.util.stream.Collectors");
            expression = $"{expression}.collect(Collectors.toList())";
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
        var exprTransformer = new ExpressionTransformer();

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
            // The getter would be emitted as pair.getFieldName() (camelCase first letter)
            var getterName = "get" + fieldName;
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
        var exprTransformer = new ExpressionTransformer();
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"do {body} while ({condition});");
    }

    private JavaSyntaxNode TransformSwitchStatement(SwitchStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = new ExpressionTransformer();
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
            var typeInfo = context.SemanticModel?.GetTypeInfo(catchClause.Declaration.Type);
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Exception";
            var varName = catchClause.Declaration.Identifier.Text;

            sb.Append($" catch ({javaType} {varName}) ");

            if (catchClause.Filter != null)
            {
                // Java 不支持 catch 过滤器，需要转换为内部 if
                var exprTransformer = new ExpressionTransformer();
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
        var exprTransformer = new ExpressionTransformer();

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
        var exprTransformer = new ExpressionTransformer();
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
        }
        else
        {
            javaType = "var";
        }

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

        // If this is a primitive-typed variable with no initializer, check if it's used as an out-argument
        // to TryGetValue. In that case, Java can't compare the result of map.get() (Integer) to null
        // when assigned to a primitive (int). Use the boxed type (Integer, Long, etc.) instead.
        if (hasNoInitializer)
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

        var exprTransformer = new ExpressionTransformer();
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
                        && origRetArr2.ElementType is ITypeParameterSymbol)
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
                if (javaType.StartsWith("ArrayList<") && initExpr.Contains(".collect(Collectors.toList())"))
                    initExpr = $"new ArrayList<>({initExpr})";
                init = $" = {initExpr}";
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



