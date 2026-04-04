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

/// <summary>
/// 语句转换器
/// </summary>
public partial class StatementTransformer : IStatementTransformer
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
            SyntaxKind.CheckedStatement => TransformCheckedStatement(node as CheckedStatementSyntax, context),
            SyntaxKind.UncheckedStatement => TransformUncheckedStatement(node as CheckedStatementSyntax, context),
            _ => new JavaStatementNode($"/* TODO: {node.Kind()} - {node} */")
        };
    }

    public string TransformBlock(BlockSyntax block, ConversionContext context)
    {
        if (block == null) return "{}";

        context.MethodState.PushScope();
        var statements = TransformStatements(block.Statements, context);
        context.MethodState.PopScope();
        return string.Join("\n        ", statements);
    }

    /// <summary>
    /// 将块语法转换为结构化 JavaMethodBody（使用 JavaRawStatement 包装现有字符串输出）。
    /// 这是从字符串输出过渡到结构化 IR 的桥梁方法。
    /// </summary>
    public Java.JavaMethodBody TransformBlockToStructuredBody(BlockSyntax block, ConversionContext context)
    {
        var body = new Java.JavaMethodBody();
        if (block == null) return body;

        context.MethodState.PushScope();
        var statements = TransformStatements(block.Statements, context);
        context.MethodState.PopScope();
        foreach (var stmt in statements)
        {
            body.Statements.Add(new Java.JavaRawStatement(stmt));
        }
        return body;
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
                        results.Add(AttachStatementComments(statement, memberText));
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
                    results.Add(AttachStatementComments(statement, stmtText));
                }
            }
        }

        return results;
    }

    private static string AttachStatementComments(StatementSyntax statement, string statementText)
    {
        var leadingComments = CommentConversion.ExtractStatementLeadingComments(statement);
        var trailingComments = CommentConversion.ExtractStatementTrailingComments(statement);
        var result = statementText;

        if (!string.IsNullOrWhiteSpace(leadingComments))
            result = leadingComments + "\n" + result;

        if (!string.IsNullOrWhiteSpace(trailingComments))
        {
            result = trailingComments.Contains('\n')
                ? result + "\n" + trailingComments
                : result + " " + trailingComments;
        }

        return result;
    }

}
