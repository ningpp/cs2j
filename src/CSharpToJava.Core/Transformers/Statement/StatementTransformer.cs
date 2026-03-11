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
            SyntaxKind.IfStatement => TransformIfStatement(node as IfStatementSyntax, context),
            SyntaxKind.WhileStatement => TransformWhileStatement(node as WhileStatementSyntax, context),
            SyntaxKind.ForStatement => TransformForStatement(node as ForStatementSyntax, context),
            SyntaxKind.ForEachStatement => TransformForEachStatement(node as ForEachStatementSyntax, context),
            SyntaxKind.DoStatement => TransformDoStatement(node as DoStatementSyntax, context),
            SyntaxKind.SwitchStatement => TransformSwitchStatement(node as SwitchStatementSyntax, context),
            SyntaxKind.TryStatement => TransformTryStatement(node as TryStatementSyntax, context),
            SyntaxKind.UsingStatement => TransformUsingStatement(node as UsingStatementSyntax, context),
            SyntaxKind.LockStatement => TransformLockStatement(node as LockStatementSyntax, context),
            SyntaxKind.FixedStatement => TransformFixedStatement(node as FixedStatementSyntax, context),
            SyntaxKind.UnsafeStatement => TransformUnsafeStatement(node as UnsafeStatementSyntax, context),
            SyntaxKind.EmptyStatement => new JavaStatementNode(""),
            SyntaxKind.LocalDeclarationStatement => TransformLocalDeclaration(node as LocalDeclarationStatementSyntax, context),
            _ => new JavaStatementNode($"// TODO: {node.Kind()} - {node}")
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
                    !stmtText.TrimStart().StartsWith("// TODO: UncheckedStatement"))
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
        var expr = exprTransformer.Transform(stmt.Expression, context);
        return new JavaStatementNode(expr + ";");
    }

    private JavaSyntaxNode TransformReturnStatement(ReturnStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            return new JavaStatementNode("return;");
        }

        var exprTransformer = new ExpressionTransformer();
        var expr = exprTransformer.Transform(stmt.Expression, context);
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
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")}; }}";

        return new JavaStatementNode($"while ({condition}) {body}");
    }

    private JavaSyntaxNode TransformForStatement(ForStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = new ExpressionTransformer();

        // 初始值
        var initializers = string.Join(", ", stmt.Declaration?.Variables.Select(v =>
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration!.Type);
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";
            var init = v.Initializer != null ? $" = {exprTransformer.Transform(v.Initializer.Value, context)}" : "";
            return $"{javaType} {v.Identifier}{init}";
        }) ?? Enumerable.Empty<string>());

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
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")}; }}";

        return new JavaStatementNode($"for ({initializers}; {condition}; {incrementors}) {body}");
    }

    private JavaSyntaxNode TransformForEachStatement(ForEachStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = new ExpressionTransformer();
        var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Type);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";
        var identifier = stmt.Identifier.Text;
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")}; }}";

        // 检查是否需要使用流式处理
        var needsStream = expression.Contains(".stream()");

        if (needsStream)
        {
            // 使用 forEach
            return new JavaStatementNode($"{expression}.forEach({identifier} -> {body})");
        }

        return new JavaStatementNode($"for ({javaType} {identifier} : {expression}) {body}");
    }

    private JavaSyntaxNode TransformDoStatement(DoStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = new ExpressionTransformer();
        var condition = exprTransformer.Transform(stmt.Condition, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")}; }}";

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
                        labels.Add($"case {exprTransformer.Transform(caseLabel.Value, context)}");
                        break;
                    case DefaultSwitchLabelSyntax:
                        labels.Add("default");
                        break;
                }
            }

            var stmtTransformer = new StatementTransformer();
            var statements = section.Statements.Select(s =>
                stmtTransformer.Transform(s, context).ToString("")).ToList();

            var sectionStr = string.Join(" ", labels) + ":\n            " +
                           string.Join("\n            ", statements);

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
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")}; }}";

        return new JavaStatementNode($"try ({string.Join("; ", resources)}) {body}");
    }

    private JavaSyntaxNode TransformLockStatement(LockStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = new ExpressionTransformer();
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")}; }}";

        return new JavaStatementNode($"synchronized ({expression}) {body}");
    }

    private JavaSyntaxNode TransformFixedStatement(FixedStatementSyntax stmt, ConversionContext context)
    {
        context.Diagnostics.Error(
            "Java doesn't support fixed buffers. Manual conversion required.",
            stmt.GetLocation()
        );
        return new JavaStatementNode("// TODO: Fixed statement - manual conversion required");
    }

    private JavaSyntaxNode TransformUnsafeStatement(UnsafeStatementSyntax stmt, ConversionContext context)
    {
        context.Diagnostics.Error(
            "Java doesn't support unsafe code. Manual conversion required.",
            stmt.GetLocation()
        );
        return new JavaStatementNode("// TODO: Unsafe statement - manual conversion required");
    }

    private JavaSyntaxNode TransformLocalDeclaration(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";

        var exprTransformer = new ExpressionTransformer();
        var declarations = string.Join(", ", stmt.Declaration.Variables.Select(v =>
        {
            var init = v.Initializer != null
                ? $" = {exprTransformer.Transform(v.Initializer.Value, context)}"
                : "";
            return $"{v.Identifier}{init}";
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
