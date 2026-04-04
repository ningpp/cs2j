using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    private JavaSyntaxNode TransformWhileStatement(WhileStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(stmt.Condition, context);

        // Fix: out/ref parameters in while condition — Holder declarations go before the loop;
        // value read-backs go at the start of the body (re-read each iteration after the call).
        string whilePreamble = "";
        string whilePostInjection = "";
        string whilePostAfterLoop = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            whilePreamble = string.Join("\n", pre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }
        if (context.HasPendingPostStatements)
        {
            var post = context.DrainPostStatements();
            whilePostInjection = string.Join("\n        ", post.Select(s => s.TrimEnd(';') + ";")) + "\n        ";
            whilePostAfterLoop = "\n" + string.Join("\n", post.Select(s => s.TrimEnd(';') + ";"));
        }

        var stmtTransformer = new StatementTransformer();
        string body;
        if (stmt.Statement is BlockSyntax block)
        {
            var bodyStr = TransformBlock(block, context);
            body = $"{{\n        {whilePostInjection}{bodyStr}\n    }}";
        }
        else
        {
            var bodyStr = stmtTransformer.Transform(stmt.Statement, context).ToString("");
            body = $"{{\n        {whilePostInjection}{bodyStr}\n    }}";
        }

        return new JavaStatementNode($"{whilePreamble}while ({condition}) {body}{whilePostAfterLoop}");
    }

    private JavaSyntaxNode TransformForStatement(ForStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // 初始值
        var initializers = "";
        if (stmt.Declaration != null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
            var declaredType = typeInfo.HasValue ? typeInfo.Value.Type : null;
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";
            var vars = stmt.Declaration.Variables.Select(v => {
                var initExpr = v.Initializer != null
                    ? ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                        v.Initializer.Value,
                        exprTransformer.Transform(v.Initializer.Value, context),
                        declaredType,
                        context)
                    : string.Empty;
                var init = v.Initializer != null ? $" = {initExpr}" : "";
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

        // Fix: drain any Holder pre/post statements produced while transforming
        // the for-loop initializers, condition, or incrementors.
        string forPreamble = "";
        string forPostInjection = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            forPreamble = string.Join("\n", pre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }
        if (context.HasPendingPostStatements)
        {
            var post = context.DrainPostStatements();
            forPostInjection = string.Join("\n        ", post.Select(s => s.TrimEnd(';') + ";")) + "\n        ";
        }

        var stmtTransformer = new StatementTransformer();
        string body;
        if (stmt.Statement is BlockSyntax block)
        {
            var bodyStr = TransformBlock(block, context);
            body = $"{{\n        {forPostInjection}{bodyStr}\n    }}";
        }
        else
        {
            var bodyStr = stmtTransformer.Transform(stmt.Statement, context).ToString("");
            body = $"{{\n        {forPostInjection}{bodyStr}\n    }}";
        }

        return new JavaStatementNode($"{forPreamble}for ({initializers}; {condition}; {incrementors}) {body}");
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
        var identifier = ConversionContext.EscapeJavaKeyword(stmt.Identifier.Text);
        // Transform the collection expression FIRST so that any anonymous-type
        // record synthesis (from `select new { ... }`) registers the record
        // before we resolve the foreach variable type via MapType.
        var expression = exprTransformer.Transform(stmt.Expression, context);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";

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

        // Detect: iterating over an IGrouping<K,V> (Map.Entry<K, List<V>>) in a foreach.
        // C#: IGrouping implements IEnumerable<V>, so foreach (var x in group) works directly.
        // Java: Map.Entry does NOT implement Iterable, so we must use group.getValue().
        if (exprTypeInfo is INamedTypeSymbol groupingExprType &&
            groupingExprType.OriginalDefinition?.ToDisplayString().StartsWith("System.Linq.IGrouping<") == true)
        {
            expression = $"{expression}.getValue()";
        }

        // Pre-process: StreamSupport.stream(...).toArray() used in foreach can't be iterated (Object[]).
        // Convert to .collect(Collectors.toCollection(() -> new ArrayList<>())) so the list is Iterable<T> and foreach works.
        {
            var trimExpr = expression.TrimEnd();
            if (trimExpr.EndsWith(".toArray()") && trimExpr.Contains("StreamSupport.stream("))
            {
                expression = trimExpr.Substring(0, trimExpr.Length - ".toArray()".Length)
                                 + ".collect(Collectors.toCollection(() -> new ArrayList<>()))";
                context.AddImport("java.util.ArrayList");
            }
        }

        // Strip trailing .stream() from expressions like .entrySet().stream() or .values().stream()
        // because the underlying collection is already Iterable and .stream() breaks for-each.
        // After stripping, the result ends with a collection method (.entrySet(), .values(), etc.)
        // which is already Iterable — skip further stream detection.
        bool strippedTrailingStream = false;
        if (expression.TrimEnd().EndsWith(".stream()"))
        {
            expression = expression.TrimEnd()[..^".stream()".Length];
            strippedTrailingStream = true;
        }

        // Stream.concat(), StreamSupport.stream(), Arrays.stream(), etc.
        // Collect to List to allow break/continue/return in the loop body.
        // Use EndsWith check to avoid double-collecting an already-collected stream:
        // the expression may contain inner .collect() calls (e.g. spliterator wrapping)
        // but we only skip if the OUTERMOST call is already .collect(Collectors.toCollection(() -> new ArrayList<>())).
        // Use ContainsStreamMethodAtTopLevel to avoid false positives where stream calls
        // appear only inside nested argument lists (e.g. method(x.stream().toArray(...))).
        bool isStream = !strippedTrailingStream
            && !expression.TrimEnd().EndsWith(".collect(Collectors.toCollection(() -> new ArrayList<>()))")
            && !EndsWithCollectCall(expression.TrimEnd())
            && !expression.TrimEnd().EndsWith(".toArray()")
            && !System.Text.RegularExpressions.Regex.IsMatch(expression.TrimEnd(), @"\.toArray\([^)]+\)$")
            && ExpressionTransformerHelpers.ContainsStreamMethodAtTopLevel(expression);

        // Also detect by semantic type: if the C# expression type is IOrderedEnumerable or IQueryable
        // (both are always yielded as Java Streams by the LINQ translator), force-collect.
        if (!isStream && exprTypeInfo is INamedTypeSymbol csForeachType)
        {
            bool isLinqResult = csForeachType.Name is "IOrderedEnumerable" or "IOrderedQueryable" or "IQueryable"
                || (csForeachType.ContainingNamespace?.ToDisplayString().StartsWith("System.Linq") == true
                    && csForeachType.Name != "IEnumerable" && csForeachType.Name != "ICollection"
                    && csForeachType.Name != "IGrouping");
            if (isLinqResult)
            {
                // Guard: if the expression is a simple variable name that was NOT registered in
                // StreamLocalVariables, its declaration already collected the stream.  Don't double-collect.
                var exprTrimmed = expression.Trim();
                bool isSimpleVar = !exprTrimmed.Contains('.') && !exprTrimmed.Contains('(');
                if (!(isSimpleVar && !context.StreamLocalVariables.Contains(exprTrimmed)))
                    isStream = true;
            }
        }

        // Fallback: if the foreach expression is a locally-declared variable that was registered
        // as stream-typed in TransformLocalDeclaration, treat it as a stream here too.
        // This handles cases where the SemanticModel is unavailable or the type is not in System.Linq.
        //
        // Root cause of issue #10: StreamLocalVariables does not track scope — a variable name added
        // to the set in one block persists for the rest of the method. When the same variable name is
        // reused in a sibling scope with an array type (Point[]), the foreach must NOT collect it,
        // because arrays are directly iterable in Java. Fix: exclude IArrayTypeSymbol from stream treatment.
        if (!isStream && context.StreamLocalVariables.Contains(expression.Trim()))
        {
            bool exprIsCollectionLike =
                // Named collection/iterable interfaces are already collections, not streams
                (exprTypeInfo is INamedTypeSymbol exprNamedType &&
                    exprNamedType.Name is "IEnumerable" or "ICollection" or "IList" or "List" or "Collection" or "Iterable")
                // Arrays are directly iterable in Java (T[] supports for-each without stream wrapping)
                || exprTypeInfo is IArrayTypeSymbol;
            if (!exprIsCollectionLike)
                isStream = true;
        }

        if (isStream)
        {
            // Only add .collect() if the expression doesn't already end with one.
            // The LINQ rewriter may have already materialized the stream to a collection.
            if (ExpressionTransformerHelpers.StripCollect(expression) == expression)
            {
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.stream.Collectors");
                expression = $"{expression}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
            }
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
        // When the explicit from-clause type is a subtype of the collection element type,
        // Java's for-each can't downcast. Wrap with double cast: (Iterable<Sub>)(Iterable<?>)(src)
        outerSrc = WrapIfDowncastNeeded(outerIterType, outerSrcType, outerSrc);
        froms.Add((outerFrom.Identifier.ValueText, outerIterType, outerSrc));

        // Inner from-clauses
        foreach (var clause in queryExpr.Body.Clauses)
        {
            if (clause is FromClauseSyntax innerFrom)
            {
                var innerSrc = exprTransformer.Transform(innerFrom.Expression, context);
                var innerSrcType = context.SemanticModel?.GetTypeInfo(innerFrom.Expression).Type;
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
                innerSrc = WrapIfDowncastNeeded(innerIterType, innerSrcType, innerSrc);
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
            body = body.Replace($"{forEachIdent}.{fieldName}", varName);
            body = body.Replace($"{forEachIdent}.{fieldName}()", varName);
        }

        // If body had helper aliases like "var source = pair.sourceV;" they become
        // "var source = source;" after replacement and should be dropped.
        body = System.Text.RegularExpressions.Regex.Replace(
            body,
            @"\bvar\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\1\s*;\s*",
            string.Empty);
        body = System.Text.RegularExpressions.Regex.Replace(
            body,
            @"\bvar\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\1\s*\(\s*\)\s*;\s*",
            string.Empty);

        // Build nested for loops from outermost to innermost
        var sb = new System.Text.StringBuilder();
        var indent = "";
        for (int i = 0; i < froms.Count; i++)
        {
            var (varName, javaType, srcExpr) = froms[i];
            // Wrap stream expressions in collect for the outer loops (inner loops can stay as-is if Iterable)
            bool needsCollect = srcExpr.Contains("StreamSupport.stream(") || srcExpr.Contains("Arrays.stream(") || srcExpr.Contains("IntStream.range(") ||
                (srcExpr.Contains(".map(") && !srcExpr.Contains(".collect(")) ||
                (srcExpr.Contains(".filter(") && !srcExpr.Contains(".collect("));
            if (needsCollect)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                srcExpr = $"{srcExpr}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
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

    /// <summary>
    /// When the explicit from-clause type differs from the collection element type (downcast),
    /// wraps the source expression with a double cast: (Iterable&lt;Sub&gt;)(Iterable&lt;?&gt;)(src)
    /// so Java's for-each loop compiles despite the type mismatch.
    /// </summary>
    private static string WrapIfDowncastNeeded(string javaType, ITypeSymbol? collectionType, string sourceExpr)
    {
        if (javaType is "var" or "Object" || collectionType == null)
            return sourceExpr;

        // Extract element type from the collection (IEnumerable<T>, List<T>, etc.)
        ITypeSymbol? elemType = null;
        if (collectionType is INamedTypeSymbol named)
        {
            if (named.TypeArguments.Length == 1)
                elemType = named.TypeArguments[0];
            else
                elemType = named.AllInterfaces
                    .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>")
                    ?.TypeArguments[0];
        }
        else if (collectionType is IArrayTypeSymbol arr)
        {
            elemType = arr.ElementType;
        }

        if (elemType == null)
            return sourceExpr;

        // Compare simple names: if the collection element type name differs from
        // the declared loop variable type name, a downcast is needed.
        var elemName = elemType.Name;
        var javaSimpleName = javaType.Contains('<') ? javaType.Substring(0, javaType.IndexOf('<')) : javaType;
        if (elemName != javaSimpleName)
        {
            return $"(Iterable<{javaType}>)(Iterable<?>)({sourceExpr})";
        }

        return sourceExpr;
    }

    private JavaSyntaxNode TransformDoStatement(DoStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        var stmtTransformer = new StatementTransformer();
        var bodyBlock = stmt.Statement is BlockSyntax block
            ? TransformBlock(block, context)
            : stmtTransformer.Transform(stmt.Statement, context).ToString("");

        // Condition is evaluated at end of each iteration. Any Holder declarations it produces
        // must live outside the loop; value read-backs are injected into the body tail.
        // Strategy: transform body first so its own pre/post stmts are already drained,
        // then transform condition and hoist its pre-stmts before the do.
        var condition = exprTransformer.Transform(stmt.Condition, context);

        string doPreamble = "";
        string doBodyTail = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            doPreamble = string.Join("\n", pre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }
        if (context.HasPendingPostStatements)
        {
            // Post-stmts after the condition would only run when the condition is false;
            // inject at the end of the body so they run every iteration before re-checking.
            var post = context.DrainPostStatements();
            doBodyTail = "\n        " + string.Join("\n        ", post.Select(s => s.TrimEnd(';') + ";"));
        }

        var body = $"{{\n        {bodyBlock}{doBodyTail}\n    }}";
        return new JavaStatementNode($"{doPreamble}do {body} while ({condition});");
    }
}
