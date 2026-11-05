using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ArrayMethodRewriter;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    private JavaSyntaxNode TransformWhileStatement(WhileStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var conditionPre = context.HasPendingPreStatements
            ? context.DrainPreStatements()
            : new List<string>();
        RejectConditionPostStatements(context, "while");

        // 条件中的属性自增降级成「读临时量 → 写回」预语句，必须随每次条件求值
        // 执行。放在 while 之前只会执行一次，条件随后读取冻结的临时量（真实链
        // XsdDateTime.Parser.parseTime 的 `while (++start.Value < _length)` 因此
        // 变成死循环）。按 C# 的求值顺序线性化到循环体顶部：
        //     while (true) { <读改写>; if (!(C')) break; body }
        // continue 重新执行读改写与测试，break 直接离开循环，与原语义一致。
        if (conditionPre.Count > 0)
        {
            var prelude = string.Join(
                "\n        ",
                conditionPre.Select(s => s.TrimEnd(';') + ";")
                    .Append($"if (!({condition})) break;"));
            var loopBody = TransformLoopBody(stmt.Statement, bodyPrelude: prelude, context);
            return new JavaStatementNode($"while (true) {loopBody}");
        }

        var body = TransformLoopBody(stmt.Statement, bodyPrelude: null, context);
        return new JavaStatementNode($"while ({condition}) {body}");
    }

    private JavaSyntaxNode TransformDoStatement(DoStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var bodyBlock = TransformLoopBodyBlockText(stmt.Statement, context);

        var condition = exprTransformer.Transform(stmt.Condition, context);
        var preamble = DrainPendingStatementsAsPreamble(context);
        RejectConditionPostStatements(context, "do-while");

        return new JavaStatementNode($"{preamble}do {{\n        {bodyBlock}\n    }} while ({condition});");
    }

    private JavaSyntaxNode TransformForStatement(ForStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        var initializers = "";
        if (stmt.Declaration != null)
        {
            var firstVar = stmt.Declaration.Variables.FirstOrDefault();
            var javaType = firstVar != null && (context.GetDeclaredSymbol(firstVar) as ILocalSymbol)?.Type is { } declType
                ? context.MapType(declType)
                : context.MapTypeFromSyntax(stmt.Declaration.Type);

            var vars = new List<string>();
            foreach (var variable in stmt.Declaration.Variables)
            {
                var initExpr = variable.Initializer != null
                    ? exprTransformer.Transform(variable.Initializer.Value, context)
                    : null;

                var forVarName = context.MethodState.GetEffectiveLocalJavaName(
                    context.GetDeclaredSymbol(variable), variable.Identifier.ToString());
                vars.Add(initExpr != null ? $"{forVarName} = {initExpr}" : forVarName);
            }

            if (vars.Count > 0)
                initializers = $"{javaType} {string.Join(", ", vars)}";
        }
        else if (stmt.Initializers.Any())
        {
            initializers = string.Join(", ", stmt.Initializers.Select(i => exprTransformer.Transform(i, context)));
        }

        // for 头的 condition/incrementors 转换产生的预语句会被 Drain 到循环外
        // （只执行一次），故在此区间要求属性自增降级输出自包含表达式。
        var previousInlinePropertyIncrement = context.InlinePropertyIncrement;
        string condition;
        string incrementors;
        context.InlinePropertyIncrement = true;
        try
        {
            condition = stmt.Condition != null
                ? exprTransformer.Transform(stmt.Condition, context)
                : "true";
            incrementors = string.Join(", ", stmt.Incrementors.Select(i => exprTransformer.Transform(i, context)));
        }
        finally
        {
            context.InlinePropertyIncrement = previousInlinePropertyIncrement;
        }

        var pendingPre = context.HasPendingPreStatements ? context.DrainPreStatements() : new List<string>();
        RejectConditionPostStatements(context, "for");

        var preamble = string.Concat(pendingPre.Select(s => s.TrimEnd(';') + ";\n"));

        var body = TransformLoopBody(stmt.Statement, bodyPrelude: null, context);
        return new JavaStatementNode($"{preamble}for ({initializers}; {condition}; {incrementors}) {body}");
    }

    /// <summary>
    /// foreach maps directly to Java's enhanced for. Iteration sources must be Java
    /// arrays or Iterables after preprocessing. Strings, dictionaries, LINQ streams,
    /// enumerator adapters and deconstructions must be lowered by a preprocessor.
    /// </summary>
    private JavaSyntaxNode TransformForEachStatement(ForEachStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        var loopVarSymbol = context.GetDeclaredSymbol(stmt) as ILocalSymbol;
        var javaType = loopVarSymbol?.Type is { } varType && varType is not IErrorTypeSymbol
            ? context.MapType(varType)
            : context.MapTypeFromSyntax(stmt.Type);
        var identifier = context.MethodState.GetEffectiveLocalJavaName(
            context.GetDeclaredSymbol(stmt), ConversionContext.EscapeJavaKeyword(stmt.Identifier.Text));

        // array-method 预处理把多维数组 foreach 摊平为嵌套增强 for
        // （int[,] → Java int[][]）：合成外行变量 __cs2j_rowN 在 C# 语义里
        // 绑定到元素类型 int，但在 Java 端必须是行数组类型（元素 + rank-1 个
        // []）。中间树经文本往返，行变量名是唯一持久识别信号。
        if (ArrayRewriteAnnotations.IsRowVariableName(stmt.Identifier.Text)
            && context.GetTypeInfo(stmt.Expression).Type is IArrayTypeSymbol { Rank: > 1 } rowCollection)
        {
            javaType = BuildFlattenedRowJavaType(rowCollection, context);
        }

        var expression = exprTransformer.Transform(stmt.Expression, context);
        // A residual LINQ Where/Select pipeline is a java.util.stream.Stream, not
        // an Iterable; materialize it before the enhanced-for cast. The source of
        // the pipeline may itself materialize a compat iterable (Enumerable.Range
        // emits IntStream...collect(...)), so decide structurally: scanning the
        // whole text for `.collect(` misclassifies the Stream and emits
        // `(Iterable) (StreamSupport...)`, which fails at runtime with
        // ClassCastException: ReferencePipeline cannot be cast to Iterable.
        if (context.GetTypeInfo(stmt.Expression).Type is not IArrayTypeSymbol
            && JavaStreamText.LooksLikeStreamPipeline(expression))
        {
            expression = JavaStreamText.MaterializeAsIterable(expression, context);
        }

        var expressionType = context.GetTypeInfo(stmt.Expression).Type;
        if (expressionType?.SpecialType == SpecialType.System_String)
        {
            throw new NotSupportedException(
                "CS2J normalization required: foreach over a string must be lowered (e.g. toCharArray()) by a preprocessor before Java conversion.");
        }
        // C16 M5: after the foreach-source preprocessor (default-on in the CLI
        // chain) a surviving foreach iterates a Java array or an Iterable source
        // only — strings, spans/index loops, custom (pattern) enumerators,
        // deconstruction and ref foreach are all lowered in the intermediate
        // tree. A source that is neither an array nor IEnumerable therefore
        // reached the converter without preprocessing; fail fast instead of
        // emitting an enhanced-for that javac would reject.
        if (expressionType is { } nonNullExpressionType
            and not IArrayTypeSymbol
            && !IsFlattenedInnerRowLoop(stmt)
            && context.SemanticModel is { } foreachModel
            && !ForeachSourceAnalyzer.ImplementsIEnumerable(nonNullExpressionType, foreachModel.Compilation))
        {
            throw new NotSupportedException(
                "CS2J normalization required: foreach over non-Iterable source '"
                + nonNullExpressionType.ToDisplayString()
                + "' must be lowered (enumerator adapter, index loop, ...) by the foreach-source preprocessor before Java conversion.");
        }

        var preamble = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            preamble = string.Concat(pre.Select(s => s.TrimEnd(';') + ";\n"));
        }
        if (context.HasPendingPostStatements)
        {
            context.DrainPostStatements();
            throw new NotSupportedException(
                "CS2J normalization required: ref/out arguments producing read-backs in a foreach expression must be lowered by a preprocessor before Java conversion.");
        }

        // Captured foreach variable lowering (display class or per-iteration
        // holder) is owned by the closure-lift preprocessor; nothing to inject here.
        var body = TransformLoopBody(stmt.Statement, bodyPrelude: null, context);
        // Compat collection interfaces expose a raw iterator (the generated classes
        // bridge generic/non-generic GetEnumerator), so an enhanced-for over them
        // yields Object. Re-type the iterable to the C# element type; javac then
        // inserts the element checkcast (unchecked, same runtime objects).
        var iterableExpression = expression;
        if (expressionType is not IArrayTypeSymbol
            && !IsCompatTypeInfoArraySource(stmt.Expression, context)
            && javaType is not "Object" and not "var")
        {
            var iterableElement = javaType switch
            {
                "char" => "Character",
                "int" => "Integer",
                "long" => "Long",
                "short" => "Short",
                "byte" => "Byte",
                "boolean" => "Boolean",
                "float" => "Float",
                "double" => "Double",
                _ => javaType,
            };
            if (IsIGroupingType(expressionType))
            {
                // C# IGrouping<K,V> iterates its values, but the Java mapping is a
                // Map.Entry (runtime CSharpKeyValuePair), which is not Iterable. The
                // element-typed cast below would throw ClassCastException; adapt to
                // the entry's value list through the compat bridge instead.
                context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                iterableExpression = $"CSharpGenericIterable.<{iterableElement}>fromAnyTyped({expression})";
            }
            else
            {
                iterableExpression = $"(Iterable<{iterableElement}>)(Iterable) ({expression})";
            }
        }
        return new JavaStatementNode(
            $"{preamble}for ({javaType} {identifier} : {iterableExpression}) {body}");
    }

    private JavaSyntaxNode TransformForEachVariableStatement(ForEachVariableStatementSyntax stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: deconstructing foreach must be lowered by a preprocessor before Java conversion.");

    /// <summary>
    /// True when the bound C# type is <c>System.Linq.IGrouping&lt;K,V&gt;</c>, which
    /// maps to a Java <c>Map.Entry</c> rather than an Iterable.
    /// </summary>
    private static bool IsIGroupingType(ITypeSymbol? type)
        => type is INamedTypeSymbol named
           && named.OriginalDefinition?.ToDisplayString()
               .StartsWith("System.Linq.IGrouping<", StringComparison.Ordinal) == true;

    /// <summary>
    /// True for foreach sources that are C# IEnumerable but render as a JAVA ARRAY:
    /// System.Reflection.TypeInfo's Declared* members map to compat TypeInfo, whose
    /// getDeclaredProperties()/getDeclaredFields()/getDeclaredMethods()/
    /// getDeclaredConstructors() return raw arrays. The (Iterable&lt;T&gt;) re-type
    /// cast would then wrap a Java array (javac: 数组无法转换为Iterable — real chain
    /// TypeScope's hide-by-name scan); an enhanced-for iterates arrays natively.
    /// </summary>
    private static bool IsCompatTypeInfoArraySource(ExpressionSyntax expression, ConversionContext context)
    {
        if (context.SemanticModel == null
            || expression is not MemberAccessExpressionSyntax memberAccess
            || context.GetSymbolInfo(memberAccess).Symbol is not IPropertySymbol property
            || property.ContainingType?.ToDisplayString() is not ("System.Reflection.TypeInfo"))
        {
            return false;
        }
        return property.Name
            is "DeclaredProperties" or "DeclaredFields" or "DeclaredMethods" or "DeclaredConstructors"
            or "DeclaredMembers";
    }

    /// <summary>
    /// True for the synthesized inner loop of an array-method flatten:
    /// <c>foreach (var x in __cs2j_rowN)</c> nested directly inside the outer
    /// <c>foreach (var __cs2j_rowN in …)</c>. C# binds the row to the scalar
    /// element type (int), so the non-Iterable fail-fast must not fire on it.
    /// </summary>
    private static bool IsFlattenedInnerRowLoop(ForEachStatementSyntax stmt)
    {
        if (stmt.Expression is not IdentifierNameSyntax rowId
            || !ArrayRewriteAnnotations.IsRowVariableName(rowId.Identifier.Text))
        {
            return false;
        }

        return (stmt.Parent as BlockSyntax)?.Parent is ForEachStatementSyntax outer
               && outer.Identifier.Text == rowId.Identifier.Text;
    }

    /// <summary>
    /// Java type of a synthesized multidim-array row variable: the mapped
    /// element type followed by <c>rank-1</c> array brackets
    /// (int[,] row → <c>int[]</c>, int[,,] row → <c>int[][]</c>).
    /// </summary>
    private static string BuildFlattenedRowJavaType(
        IArrayTypeSymbol multiDimCollection, ConversionContext context)
    {
        var brackets = string.Empty;
        for (var i = 1; i < multiDimCollection.Rank; i++)
        {
            brackets += "[]";
        }

        return context.MapType(multiDimCollection.ElementType) + brackets;
    }

    private static string BoxJavaType(string t)
        => t switch
        {
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "boolean" => "Boolean",
            "short" => "Short",
            "byte" => "Byte",
            "char" => "Character",
            _ => t
        };

    private static string DrainPendingStatementsAsPreamble(ConversionContext context)
    {
        if (!context.HasPendingPreStatements) return "";
        var pre = context.DrainPreStatements();
        return string.Concat(pre.Select(s => s.TrimEnd(';') + ";\n"));
    }

    private static void RejectConditionPostStatements(ConversionContext context, string loopKind)
    {
        if (context.HasPendingPostStatements)
        {
            context.DrainPostStatements();
            throw new NotSupportedException(
                $"CS2J normalization required: ref/out arguments producing read-backs in a {loopKind} condition must be lowered by a preprocessor before Java conversion.");
        }
    }

    /// <summary>
    /// Loop body text; <paramref name="bodyPrelude"/> is emitted before the
    /// original body statements (inside the loop, so it runs per iteration —
    /// e.g. the read-modify-write sequence of a condition-side increment).
    /// </summary>
    private string TransformLoopBody(StatementSyntax statement, string? bodyPrelude, ConversionContext context)
    {
        if (statement is BlockSyntax block)
        {
            var bodyStr = TransformBlock(block, context);
            var prefix = bodyPrelude != null ? bodyPrelude + "\n        " : "";
            return $"{{\n        {prefix}{bodyStr}\n    }}";
        }

        var bodyText = new StatementTransformer().Transform(statement, context).ToString("");
        if (bodyPrelude != null)
            return $"{{\n        {bodyPrelude}\n        {bodyText}\n    }}";
        return $"{{\n        {bodyText}\n    }}";
    }

    private string TransformLoopBodyBlockText(StatementSyntax statement, ConversionContext context)
    {
        return statement is BlockSyntax block
            ? TransformBlock(block, context)
            : new StatementTransformer().Transform(statement, context).ToString("");
    }
}
