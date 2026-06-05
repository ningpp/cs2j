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
            SyntaxKind.Block => TransformNestedBlock(node as BlockSyntax, context),
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
            SyntaxKind.LabeledStatement => TransformLabelStatement(node as LabeledStatementSyntax, context),
            SyntaxKind.GotoStatement => TransformGotoStatement(node as GotoStatementSyntax, context),
            SyntaxKind.GotoCaseStatement => TransformGotoStatement(node as GotoStatementSyntax, context),
            SyntaxKind.GotoDefaultStatement => TransformGotoStatement(node as GotoStatementSyntax, context),
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

        // Pre-scan for lambda capture analysis at method body level (scope depth 0).
        // This identifies variables captured by lambdas that are externally reassigned,
        // so we can create holder declarations right after variable declarations.
        if (context.MethodState.ScopeDepth == 0 && !context.MethodState.LambdaCapturePreScanDone)
        {
            PreScanLambdaCaptures(block, context);
            context.MethodState.LambdaCapturePreScanDone = true;
        }

        if (context.MethodState.ScopeDepth == 0)
        {
            PrescanLabels(block.Statements, context);

            // Analyze goto patterns to determine if state machine is needed
            var gotoAnalyzer = GotoAnalyzer.Analyze(block, context.Labels);
            context.MethodState.GotoAnalyzer = gotoAnalyzer;

            // If state machine is needed (B2/B3/C class gotos), wrap in state machine
            if (gotoAnalyzer.NeedsStateMachine)
            {
                return TransformBlockWithStateMachine(block, context, gotoAnalyzer);
            }
        }

        context.MethodState.PushScope();
        var statements = TransformStatements(block.Statements, context);
        context.MethodState.PopScope();
        return string.Join("\n        ", statements);
    }

    private JavaSyntaxNode TransformNestedBlock(BlockSyntax? block, ConversionContext context)
    {
        if (block == null)
            return new JavaStatementNode("{ }");

        return new JavaStatementNode($"{{\n        {TransformBlock(block, context)}\n    }}");
    }

    /// <summary>
    /// 将块语法转换为结构化 JavaMethodBody（使用 JavaRawStatement 包装现有字符串输出）。
    /// 这是从字符串输出过渡到结构化 IR 的桥梁方法。
    /// </summary>
    public Java.JavaMethodBody TransformBlockToStructuredBody(BlockSyntax block, ConversionContext context)
    {
        var body = new Java.JavaMethodBody();
        if (block == null) return body;

        // Pre-scan for lambda capture analysis at method body level (scope depth 0).
        if (context.MethodState.ScopeDepth == 0 && !context.MethodState.LambdaCapturePreScanDone)
        {
            PreScanLambdaCaptures(block, context);
            context.MethodState.LambdaCapturePreScanDone = true;
        }

        if (context.MethodState.ScopeDepth == 0)
        {
            PrescanLabels(block.Statements, context);

            // Analyze goto patterns to determine if state machine is needed
            var gotoAnalyzer = GotoAnalyzer.Analyze(block, context.Labels);
            context.MethodState.GotoAnalyzer = gotoAnalyzer;

            // If state machine is needed (B2/B3/C class gotos), fall back to string-based body with state machine
            if (gotoAnalyzer.NeedsStateMachine)
            {
                var stateMachineCode = TransformBlockWithStateMachine(block, context, gotoAnalyzer);
                body.Statements.Add(new Java.JavaRawStatement(stateMachineCode));
                return body;
            }
        }

        context.MethodState.PushScope();
        var irStatements = TransformStatementsToIR(block.Statements, context);
        context.MethodState.PopScope();
        foreach (var stmt in irStatements)
        {
            body.Statements.Add(stmt);
        }
        return body;
    }

    public List<string> TransformStatements(SyntaxList<StatementSyntax> statements, ConversionContext context)
    {
        var results = new List<string>();

        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            if (statement is LocalDeclarationStatementSyntax usingDeclaration
                && IsUsingDeclaration(usingDeclaration))
            {
                var resources = CollectConsecutiveUsingDeclarationResources(statements, ref i, context);
                var bodyStatements = TransformStatementsFrom(statements, i + 1, context);
                var body = string.Join("\n        ", bodyStatements);
                results.Add($"try ({string.Join("; ", resources)}) {{\n        {body}\n    }}");
                break;
            }

            var result = Transform(statement, context);
            if (result is JavaMemberCollection collection)
            {
                foreach (var member in collection.Members)
                {
                    var memberText = member.ToString("");
                    if (ShouldEmitStatementText(memberText))
                    {
                        results.Add(AttachStatementComments(statement, memberText));
                    }
                }
            }
            else if (result is Java.JavaStatement javaStmt)
            {
                var stmtText = javaStmt.ToString("");
                if (ShouldEmitStatementText(stmtText))
                {
                    results.Add(AttachStatementComments(statement, stmtText));
                }
            }
            else if (result is JavaStatementNode stmt)
            {
                var stmtText = stmt.ToString("");
                if (ShouldEmitStatementText(stmtText))
                {
                    results.Add(AttachStatementComments(statement, stmtText));
                }
            }

            if (IsUnconditionalJump(statement))
                break;
        }

        return results;
    }

    /// <summary>
    /// Transforms statements to structured IR nodes. For statements that are already
    /// <see cref="Java.JavaStatement"/> subclasses, they are preserved as-is.
    /// Other results are wrapped in <see cref="Java.JavaRawStatement"/>.
    /// </summary>
    public List<Java.JavaStatement> TransformStatementsToIR(SyntaxList<StatementSyntax> statements, ConversionContext context)
    {
        var results = new List<Java.JavaStatement>();

        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            if (statement is LocalDeclarationStatementSyntax usingDeclaration
                && IsUsingDeclaration(usingDeclaration))
            {
                var resources = CollectConsecutiveUsingDeclarationResources(statements, ref i, context);
                var bodyStatements = TransformStatementsFrom(statements, i + 1, context);
                var body = string.Join("\n        ", bodyStatements);
                results.Add(new Java.JavaRawStatement(
                    $"try ({string.Join("; ", resources)}) {{\n        {body}\n    }}"));
                break;
            }

            var result = Transform(statement, context);

            if (result is JavaMemberCollection collection)
            {
                foreach (var member in collection.Members)
                {
                    var memberText = member.ToString("");
                    if (ShouldEmitStatementText(memberText))
                    {
                        var text = AttachStatementComments(statement, memberText);
                        results.Add(new Java.JavaRawStatement(text));
                    }
                }
            }
            else if (result is Java.JavaStatement javaStmt)
            {
                // Structured IR node produced by the transformer - keep as-is.
                var comments = CommentConversion.ExtractStatementLeadingComments(statement);
                if (!string.IsNullOrWhiteSpace(comments))
                    javaStmt.LeadingComment = comments;
                results.Add(javaStmt);
            }
            else if (result is JavaStatementNode stmt)
            {
                var stmtText = stmt.ToString("");
                if (ShouldEmitStatementText(stmtText))
                {
                    var text = AttachStatementComments(statement, stmtText);
                    results.Add(new Java.JavaRawStatement(text));
                }
            }

            if (IsUnconditionalJump(statement))
                break;
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

    private static bool ShouldEmitStatementText(string? statementText)
    {
        return !string.IsNullOrWhiteSpace(statementText)
            && !statementText.TrimStart().StartsWith("#")
            && !statementText.TrimStart().StartsWith("/* TODO: UncheckedStatement");
    }

    private static bool IsUnconditionalJump(StatementSyntax statement)
        => statement is ReturnStatementSyntax
            or ThrowStatementSyntax
            or BreakStatementSyntax
            or ContinueStatementSyntax
            or GotoStatementSyntax;

    private static bool IsUsingDeclaration(LocalDeclarationStatementSyntax statement)
        => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);

    private static List<string> CollectConsecutiveUsingDeclarationResources(
        SyntaxList<StatementSyntax> statements,
        ref int index,
        ConversionContext context)
    {
        var resources = new List<string>();
        var currentIndex = index;

        while (currentIndex < statements.Count
            && statements[currentIndex] is LocalDeclarationStatementSyntax usingDeclaration
            && IsUsingDeclaration(usingDeclaration))
        {
            resources.AddRange(BuildUsingDeclarationResources(usingDeclaration, context));
            currentIndex++;
        }

        index = currentIndex - 1;
        return resources;
    }

    private List<string> TransformStatementsFrom(
        SyntaxList<StatementSyntax> statements,
        int startIndex,
        ConversionContext context)
    {
        var results = new List<string>();

        for (var i = startIndex; i < statements.Count; i++)
        {
            var statement = statements[i];
            if (statement is LocalDeclarationStatementSyntax usingDeclaration
                && IsUsingDeclaration(usingDeclaration))
            {
                var resources = CollectConsecutiveUsingDeclarationResources(statements, ref i, context);
                var bodyStatements = TransformStatementsFrom(statements, i + 1, context);
                var body = string.Join("\n        ", bodyStatements);
                results.Add($"try ({string.Join("; ", resources)}) {{\n        {body}\n    }}");
                break;
            }

            var result = Transform(statement, context);
            if (result is JavaMemberCollection collection)
            {
                foreach (var member in collection.Members)
                {
                    var memberText = member.ToString("");
                    if (ShouldEmitStatementText(memberText))
                    {
                        results.Add(AttachStatementComments(statement, memberText));
                    }
                }
            }
            else if (result is Java.JavaStatement javaStmt)
            {
                var stmtText = javaStmt.ToString("");
                if (ShouldEmitStatementText(stmtText))
                {
                    results.Add(AttachStatementComments(statement, stmtText));
                }
            }
            else if (result is JavaStatementNode stmt)
            {
                var stmtText = stmt.ToString("");
                if (ShouldEmitStatementText(stmtText))
                {
                    results.Add(AttachStatementComments(statement, stmtText));
                }
            }

            if (IsUnconditionalJump(statement))
                break;
        }

        return results;
    }

}
