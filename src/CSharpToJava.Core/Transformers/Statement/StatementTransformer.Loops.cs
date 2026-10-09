using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    private JavaSyntaxNode TransformWhileStatement(WhileStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var preamble = DrainPendingStatementsAsPreamble(context);
        RejectConditionPostStatements(context, "while");

        var body = TransformLoopBody(stmt.Statement, bodyPostInjection: null, context);
        return new JavaStatementNode($"{preamble}while ({condition}) {body}");
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

        var condition = stmt.Condition != null
            ? exprTransformer.Transform(stmt.Condition, context)
            : "true";
        var incrementors = string.Join(", ", stmt.Incrementors.Select(i => exprTransformer.Transform(i, context)));

        var pendingPre = context.HasPendingPreStatements ? context.DrainPreStatements() : new List<string>();
        RejectConditionPostStatements(context, "for");

        var preamble = string.Concat(pendingPre.Select(s => s.TrimEnd(';') + ";\n"));

        var body = TransformLoopBody(stmt.Statement, bodyPostInjection: null, context);
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

        var expression = exprTransformer.Transform(stmt.Expression, context);

        var expressionType = context.GetTypeInfo(stmt.Expression).Type;
        if (expressionType?.SpecialType == SpecialType.System_String)
        {
            throw new NotSupportedException(
                "CS2J normalization required: foreach over a string must be lowered (e.g. toCharArray()) by a preprocessor before Java conversion.");
        }
        if (expressionType is not (IArrayTypeSymbol or null))
        {
            // Only arrays and Iterable types support Java enhanced-for directly.
            // The compat type mapping is expected to make C# collection types Iterable.
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
        var body = TransformLoopBody(stmt.Statement, bodyPostInjection: null, context);
        return new JavaStatementNode($"{preamble}for ({javaType} {identifier} : {expression}) {body}");
    }

    private JavaSyntaxNode TransformForEachVariableStatement(ForEachVariableStatementSyntax stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: deconstructing foreach must be lowered by a preprocessor before Java conversion.");

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

    private string TransformLoopBody(StatementSyntax statement, string? bodyPostInjection, ConversionContext context)
    {
        if (statement is BlockSyntax block)
        {
            var bodyStr = TransformBlock(block, context);
            var prefix = bodyPostInjection != null ? bodyPostInjection + "\n        " : "";
            return $"{{\n        {prefix}{bodyStr}\n    }}";
        }

        var bodyText = new StatementTransformer().Transform(statement, context).ToString("");
        if (bodyPostInjection != null)
            return $"{{\n        {bodyPostInjection}\n        {bodyText}\n    }}";
        return $"{{\n        {bodyText}\n    }}";
    }

    private string TransformLoopBodyBlockText(StatementSyntax statement, ConversionContext context)
    {
        return statement is BlockSyntax block
            ? TransformBlock(block, context)
            : new StatementTransformer().Transform(statement, context).ToString("");
    }
}
