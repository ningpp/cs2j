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
    // Lambda-capture holder statements for method parameters (no declaration statement
    // inside the body to attach them to). Emitted at the start of the method body.
    private readonly List<string> _pendingParameterHolderStatements = new();

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
            SyntaxKind.ForEachVariableStatement => TransformForEachVariableStatement(node as ForEachVariableStatementSyntax, context),
            SyntaxKind.DoStatement => TransformDoStatement(node as DoStatementSyntax, context),
            SyntaxKind.SwitchStatement => TransformSwitchStatement(node as SwitchStatementSyntax, context),
            SyntaxKind.TryStatement => TransformTryStatement(node as TryStatementSyntax, context),
            SyntaxKind.UsingStatement => TransformUsingStatement(node as UsingStatementSyntax, context),
            SyntaxKind.BreakStatement => TransformBreakStatement(node as BreakStatementSyntax, context),
            SyntaxKind.ContinueStatement => TransformContinueStatement(node as ContinueStatementSyntax, context),
            // goto/label 语法必须在 Java 转换前由 GotoEliminator 预处理消除；
            // 若此处仍遇到，说明预处理缺失或失败，直接抛异常而不是生成非法 Java。
            SyntaxKind.LabeledStatement => throw new InvalidOperationException(
                $"Labeled statement '{(node as LabeledStatementSyntax)?.Identifier.Text}' should have been eliminated by GotoEliminator before Java conversion."),
            SyntaxKind.GotoStatement
            or SyntaxKind.GotoCaseStatement
            or SyntaxKind.GotoDefaultStatement => throw new InvalidOperationException(
                $"'{node.Kind()}' should have been eliminated by GotoEliminator before Java conversion."),
            SyntaxKind.YieldReturnStatement => TransformYieldReturn(node as YieldStatementSyntax, context),
            SyntaxKind.YieldBreakStatement => TransformYieldBreak(node as YieldStatementSyntax, context),
            SyntaxKind.LockStatement => TransformLockStatement(node as LockStatementSyntax, context),
            SyntaxKind.FixedStatement => TransformFixedStatement(node as FixedStatementSyntax, context),
            SyntaxKind.UnsafeStatement => TransformUnsafeStatement(node as UnsafeStatementSyntax, context),
            SyntaxKind.EmptyStatement => new JavaStatementNode(""),
            SyntaxKind.LocalDeclarationStatement => TransformLocalDeclaration(node as LocalDeclarationStatementSyntax, context),
            SyntaxKind.LocalFunctionStatement => TransformLocalFunctionStatement(node as LocalFunctionStatementSyntax, context),
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

            // Parameters have no declaration statement inside the body, so emit their
            // lambda-capture holders at the very start of the method body (before any
            // reassignment) and activate the mappings so subsequent references —
            // including the reassignments themselves — read/write the holder element.
            foreach (var holder in context.MethodState.GetPendingParameterLambdaCaptureHolders())
            {
                var javaParamName = ConversionContext.EscapeJavaKeyword(holder.VarName);
                var holderStatement = holder.JavaType.Contains('<')
                    ? $"{holder.JavaType}[] {holder.HolderName} = ({holder.JavaType}[]) new Object[] {{ {javaParamName} }}"
                    : $"{holder.JavaType}[] {holder.HolderName} = new {holder.JavaType}[] {{ {javaParamName} }}";
                context.MethodState.ActivateLambdaCaptureHolder(holder.VarName, holder.DeclaringSyntax);
                _pendingParameterHolderStatements.Add(holderStatement);
            }
        }

        if (context.MethodState.ScopeDepth == 0)
        {
            PrescanLocalFunctions(block, context);
        }

        context.MethodState.PushScope();
        var statements = TransformStatements(block.Statements, context);
        context.MethodState.PopScope();
        if (_pendingParameterHolderStatements.Count > 0)
        {
            statements = _pendingParameterHolderStatements.Concat(statements).ToList();
            _pendingParameterHolderStatements.Clear();
        }
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

            // Parameters have no declaration statement inside the body, so emit their
            // lambda-capture holders at the very start of the method body (before any
            // reassignment) and activate the mappings so subsequent references —
            // including the reassignments themselves — read/write the holder element.
            foreach (var holder in context.MethodState.GetPendingParameterLambdaCaptureHolders())
            {
                var javaParamName = ConversionContext.EscapeJavaKeyword(holder.VarName);
                var holderStatement = holder.JavaType.Contains('<')
                    ? $"{holder.JavaType}[] {holder.HolderName} = ({holder.JavaType}[]) new Object[] {{ {javaParamName} }}"
                    : $"{holder.JavaType}[] {holder.HolderName} = new {holder.JavaType}[] {{ {javaParamName} }}";
                context.MethodState.ActivateLambdaCaptureHolder(holder.VarName, holder.DeclaringSyntax);
                body.Statements.Add(new Java.JavaRawStatement(holderStatement + ";"));
            }
        }

        if (context.MethodState.ScopeDepth == 0)
        {
            PrescanLocalFunctions(block, context);
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
                // Later resource initializers may emit pre-statements that reference
                // the first resource variable (e.g. new JsonTextWriter(stringWriter)).
                // Hoist the first resource declaration before those pre-statements.
                var usingPre = context.DrainPreStatements().ToList();
                if (usingPre.Count > 0 && resources.Count > 1)
                {
                    var firstResource = resources[0];
                    var firstEq = firstResource.IndexOf(" = ", System.StringComparison.Ordinal);
                    if (firstEq > 0)
                    {
                        results.Add(firstResource + ";");
                        resources.RemoveAt(0);
                    }
                }
                foreach (var pre in usingPre)
                    results.Add(pre.TrimEnd(';') + ";");
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
            else if (result is Java.JavaStatement javaStmt and not JavaStatementNode)
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
    {
        switch (statement)
        {
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case BreakStatementSyntax:
            case ContinueStatementSyntax:
            case GotoStatementSyntax:
                return true;

            case WhileStatementSyntax whileStatement:
                return IsTrueLiteral(whileStatement.Condition)
                    && !ContainsReachableUnlabeledBreak(whileStatement.Statement)
                    && !ContainsGotoTargetingOutsideLoop(whileStatement.Statement);

            case ForStatementSyntax forStatement:
                return forStatement.Condition == null
                    && !ContainsReachableUnlabeledBreak(forStatement.Statement)
                    && !ContainsGotoTargetingOutsideLoop(forStatement.Statement);

            case LabeledStatementSyntax labeled:
                return IsUnconditionalJump(labeled.Statement);

            case BlockSyntax block:
                return block.Statements.Count > 0
                    && !ContainsUsingDeclaration(block)
                    && IsUnconditionalJump(block.Statements[^1]);

            case IfStatementSyntax ifStatement:
                return ifStatement.Else != null
                    && IsUnconditionalJump(ifStatement.Statement)
                    && IsUnconditionalJump(ifStatement.Else.Statement);

            case TryStatementSyntax tryStatement:
                var finallyCannotComplete = tryStatement.Finally != null
                    && IsUnconditionalJump(tryStatement.Finally.Block);
                if (finallyCannotComplete)
                    return true;

                var tryCannotComplete = IsUnconditionalJump(tryStatement.Block);
                var allCatchesCannotComplete = tryStatement.Catches.Count > 0
                    && tryStatement.Catches.All(c => IsUnconditionalJump(c.Block));
                return tryCannotComplete && allCatchesCannotComplete;

            default:
                return false;
        }
    }

    private static bool IsTrueLiteral(ExpressionSyntax expression)
    {
        return expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.TrueLiteralExpression);
    }

    private static bool ContainsReachableUnlabeledBreak(StatementSyntax statement)
    {
        switch (statement)
        {
            case BreakStatementSyntax:
                return true;

            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case GotoStatementSyntax:
            case ContinueStatementSyntax:
                return false;

            case BlockSyntax block:
                foreach (var child in block.Statements)
                {
                    if (ContainsReachableUnlabeledBreak(child))
                    {
                        return true;
                    }

                    if (IsUnconditionalJump(child))
                    {
                        return false;
                    }
                }

                return false;

            case IfStatementSyntax ifStatement:
                return ContainsReachableUnlabeledBreak(ifStatement.Statement)
                    || (ifStatement.Else != null && ContainsReachableUnlabeledBreak(ifStatement.Else.Statement));

            case LabeledStatementSyntax labeled:
                return ContainsReachableUnlabeledBreak(labeled.Statement);

            case SwitchStatementSyntax:
            case WhileStatementSyntax:
            case ForStatementSyntax:
            case ForEachStatementSyntax:
            case DoStatementSyntax:
                return false;

            default:
                return false;
        }
    }

    private static bool ContainsGotoTargetingOutsideLoop(StatementSyntax loopBody)
    {
        var innerLabels = new HashSet<string>(StringComparer.Ordinal);
        CollectLabelNames(loopBody, innerLabels);

        foreach (var gotoStmt in loopBody.DescendantNodes().OfType<GotoStatementSyntax>())
        {
            if (gotoStmt.Kind() is SyntaxKind.GotoCaseStatement or SyntaxKind.GotoDefaultStatement)
                continue;
            var target = (gotoStmt.Expression as IdentifierNameSyntax)?.Identifier.Text;
            if (target != null && !innerLabels.Contains(target))
                return true;
        }

        return false;
    }

    private static void CollectLabelNames(SyntaxNode node, HashSet<string> labels)
    {
        foreach (var labeled in node.DescendantNodes().OfType<LabeledStatementSyntax>())
            labels.Add(labeled.Identifier.Text);
    }

    private static bool IsUsingDeclaration(LocalDeclarationStatementSyntax statement)
        => statement.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);

    private static bool ContainsUsingDeclaration(BlockSyntax block)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is LocalDeclarationStatementSyntax localDecl && IsUsingDeclaration(localDecl))
                return true;
        }
        return false;
    }

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
