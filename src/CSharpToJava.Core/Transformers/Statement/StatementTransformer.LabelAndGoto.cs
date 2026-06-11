using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using System.Text;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    internal static void PrescanLabels(SyntaxList<StatementSyntax> statements, ConversionContext context)
    {
        foreach (var stmt in statements)
        {
            if (stmt is LabeledStatementSyntax labeled)
            {
                var kind = GetLabelKind(labeled.Statement);
                context.Labels.Register(labeled.Identifier.Text, kind);
                PrescanLabelsInStatement(labeled.Statement, context);
            }
            else
            {
                PrescanLabelsInStatement(stmt, context);
            }
        }
    }

    private static void PrescanLabelsInStatement(StatementSyntax stmt, ConversionContext context)
    {
        switch (stmt)
        {
            case BlockSyntax block:
                PrescanLabels(block.Statements, context);
                break;
            case IfStatementSyntax ifStmt:
                PrescanLabelsInStatement(ifStmt.Statement, context);
                if (ifStmt.Else != null)
                    PrescanLabelsInStatement(ifStmt.Else.Statement, context);
                break;
            case WhileStatementSyntax whileStmt:
                PrescanLabelsInStatement(whileStmt.Statement, context);
                break;
            case ForStatementSyntax forStmt:
                PrescanLabelsInStatement(forStmt.Statement, context);
                break;
            case ForEachStatementSyntax foreachStmt:
                PrescanLabelsInStatement(foreachStmt.Statement, context);
                break;
            case DoStatementSyntax doStmt:
                PrescanLabelsInStatement(doStmt.Statement, context);
                break;
            case SwitchStatementSyntax switchStmt:
                foreach (var section in switchStmt.Sections)
                    PrescanLabels(section.Statements, context);
                break;
            case TryStatementSyntax tryStmt:
                PrescanLabelsInStatement(tryStmt.Block, context);
                foreach (var catchClause in tryStmt.Catches)
                    PrescanLabelsInStatement(catchClause.Block, context);
                if (tryStmt.Finally != null)
                    PrescanLabelsInStatement(tryStmt.Finally.Block, context);
                break;
            case UsingStatementSyntax usingStmt:
                PrescanLabelsInStatement(usingStmt.Statement, context);
                break;
            case LockStatementSyntax lockStmt:
                PrescanLabelsInStatement(lockStmt.Statement, context);
                break;
            case FixedStatementSyntax fixedStmt:
                PrescanLabelsInStatement(fixedStmt.Statement, context);
                break;
            case UnsafeStatementSyntax unsafeStmt:
                if (unsafeStmt.Block != null)
                    PrescanLabelsInStatement(unsafeStmt.Block, context);
                break;
            case CheckedStatementSyntax checkedStmt:
                PrescanLabelsInStatement(checkedStmt.Block, context);
                break;
            case LabeledStatementSyntax labeled:
                PrescanLabelsInStatement(labeled.Statement, context);
                break;
        }
    }

    private static LabelKind GetLabelKind(StatementSyntax labeledStatement)
    {
        return labeledStatement switch
        {
            ForStatementSyntax => LabelKind.Loop,
            WhileStatementSyntax => LabelKind.Loop,
            DoStatementSyntax => LabelKind.Loop,
            ForEachStatementSyntax => LabelKind.Loop,
            BlockSyntax => LabelKind.Block,
            _ => LabelKind.Other
        };
    }

    private JavaSyntaxNode TransformLabelStatement(LabeledStatementSyntax stmt, ConversionContext context)
    {
        var labelName = stmt.Identifier.Text;
        var innerResult = Transform(stmt.Statement, context).ToString("");

        if (stmt.Statement is ForStatementSyntax
            or WhileStatementSyntax
            or DoStatementSyntax
            or ForEachStatementSyntax
            or BlockSyntax)
        {
            return new JavaStatementNode($"{labelName}: {innerResult}");
        }
        else
        {
            return new JavaStatementNode($"{labelName}: {{ {innerResult} }}");
        }
    }

    private JavaSyntaxNode TransformGotoStatement(GotoStatementSyntax stmt, ConversionContext context)
    {
        // D class: goto case / goto default inside a switch-with-goto-case context.
        if (stmt.Kind() == SyntaxKind.GotoCaseStatement || stmt.Kind() == SyntaxKind.GotoDefaultStatement)
        {
            if (context.TryGetSwitchGotoCaseInfo(out var stateName, out var loopName, out var stateByCaseValue, out var defaultState))
            {
                if (stmt.Kind() == SyntaxKind.GotoDefaultStatement)
                {
                    if (defaultState.HasValue)
                    {
                        return new JavaStatementNode($"{stateName} = {defaultState.Value}; continue {loopName};");
                    }
                    return new JavaStatementNode("/* TODO: goto default - default label not found */");
                }

                // goto case
                var target = stmt.Expression != null
                    ? stmt.Expression.NormalizeWhitespace().ToFullString()
                    : "";
                if (stateByCaseValue.TryGetValue(target, out var state))
                {
                    return new JavaStatementNode($"{stateName} = {state}; continue {loopName};");
                }
                return new JavaStatementNode($"/* TODO: goto case {target} - case label not found */");
            }

            if (stmt.Kind() == SyntaxKind.GotoCaseStatement)
            {
                return new JavaStatementNode("/* TODO: goto case - unsupported */");
            }
            return new JavaStatementNode("/* TODO: goto default - unsupported */");
        }

        var targetLabel = (stmt.Expression as IdentifierNameSyntax)?.Identifier.Text;
        if (targetLabel == null)
        {
            return new JavaStatementNode("/* TODO: goto - unsupported pattern */");
        }

        if (!context.Labels.Contains(targetLabel))
        {
            return new JavaStatementNode($"/* TODO: goto {targetLabel} - label not found in scope */");
        }

        // Use the GotoAnalyzer classification to determine the transformation
        var gotoAnalyzer = context.MethodState.GotoAnalyzer;
        if (gotoAnalyzer != null)
        {
            var gotoInfo = gotoAnalyzer.AllGotos.FirstOrDefault(g =>
                g.GotoStatement == stmt && g.TargetLabel == targetLabel);

            if (gotoInfo != null)
            {
                switch (gotoInfo.Classification)
                {
                    case GotoScopeClassification.InScopeLoop:
                        // A1: goto inside labeled loop -> continue label.
                        return new JavaStatementNode($"continue {targetLabel};");

                    case GotoScopeClassification.InScopeBlock:
                        // A2: goto inside labeled block -> break label.
                        return new JavaStatementNode($"break {targetLabel};");

                    case GotoScopeClassification.BreakFromEnclosingLoop:
                        // D: goto target is a sibling of the enclosing loop -> break;
                        return new JavaStatementNode("break;");

                    case GotoScopeClassification.SameBodyLoop:
                    case GotoScopeClassification.SameBodyOther:
                    case GotoScopeClassification.CrossScope:
                        // B1/B2/B3/C: state machine goto
                        var stateValue = GetLabelBlockIndex(targetLabel, context);
                        return new JavaStatementNode($"__state = {stateValue}; continue __gotoLoop;");
                }
            }
        }

        // Fallback: use old logic for unclassified gotos
        if (!IsInLabelScope(stmt, targetLabel))
        {
            return new JavaStatementNode($"/* TODO: goto {targetLabel} - cross-scope goto unsupported */");
        }

        context.Labels.TryGetLabel(targetLabel, out var labelInfo);
        if (labelInfo!.Kind == LabelKind.Loop)
        {
            return new JavaStatementNode($"continue {targetLabel};");
        }
        else
        {
            return new JavaStatementNode($"break {targetLabel};");
        }
    }

    /// <summary>
    /// Gets the basic block index for a label in the state machine.
    /// </summary>
    private static int GetLabelBlockIndex(string labelName, ConversionContext context)
    {
        var gotoAnalyzer = context.MethodState.GotoAnalyzer;
        if (gotoAnalyzer != null && gotoAnalyzer.LabelToBlockIndex.TryGetValue(labelName, out var index))
        {
            return index;
        }

        // Fallback: use old state value calculation
        return GetLabelStateValue(labelName, context);
    }

    private static int GetLabelStateValue(string labelName, ConversionContext context)
    {
        var gotoAnalyzer = context.MethodState.GotoAnalyzer;
        if (gotoAnalyzer == null) return 0;

        var labels = gotoAnalyzer.StateMachineLabels.OrderBy(l => l).ToList();
        var index = labels.IndexOf(labelName);
        return index + 1; // State 0 is initial, states 1+ are labels
    }

    private static bool IsInLabelScope(GotoStatementSyntax gotoStmt, string targetLabel)
    {
        var current = gotoStmt.Parent;
        while (current != null)
        {
            if (current is LabeledStatementSyntax labeled
                && labeled.Identifier.Text == targetLabel)
            {
                return true;
            }
            if (current is BlockSyntax block && block.Parent is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax
                or ArrowExpressionClauseSyntax)
            {
                break;
            }
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Transforms a method body that requires a state machine using basic blocks.
    /// </summary>
    internal string TransformBlockWithStateMachine(BlockSyntax block, ConversionContext context, GotoAnalyzer gotoAnalyzer)
    {
        var sb = new StringBuilder();
        var basicBlocks = gotoAnalyzer.BasicBlocks;
        var labelToBlockIndex = gotoAnalyzer.LabelToBlockIndex;

        // Hoist variable declarations
        var (hoistedDeclarations, hoistedVarNames) = CollectStateMachineLocalDeclarations(basicBlocks, context);
        foreach (var hoistedLocal in hoistedDeclarations)
        {
            sb.AppendLine(hoistedLocal);
        }

        // Drain any pending pre-statements BEFORE the switch statement.
        // Pre-statements like base segment declarations (MemorySegment __baseN = ptr)
        // must be placed before the switch, not inside a case, to avoid Java
        // "may not have been initialized" errors when referenced across cases.
        if (context.HasPendingPreStatements)
        {
            var pendingPre = context.DrainPreStatements();
            foreach (var pre in pendingPre)
                sb.AppendLine(pre.TrimEnd(';') + ";");
        }

        sb.AppendLine("int __state = 0;");
        sb.AppendLine("__gotoLoop: while (true) {");
        sb.AppendLine("    switch (__state) {");

        context.MethodState.PushScope();
        try
        {
            for (var i = 0; i < basicBlocks.Count; i++)
            {
                var basicBlock = basicBlocks[i];
                var labelComment = basicBlock.Label != null
                    ? $" // {basicBlock.Label}"
                    : $" // block_{basicBlock.Index}";
                sb.AppendLine($"        case {basicBlock.Index}:{labelComment}");

                // Transform statements in the block
                for (var statementIndex = 0; statementIndex < basicBlock.Statements.Count; statementIndex++)
                {
                    var stmt = basicBlock.Statements[statementIndex];
                    var transformed = statementIndex == 0 && basicBlock.Label != null
                        ? TransformLabeledBasicBlockStatement(basicBlock.Label, stmt, context, labelToBlockIndex)
                        : TransformBasicBlockStatement(stmt, context, labelToBlockIndex);

                    if (!string.IsNullOrWhiteSpace(transformed))
                    {
                        AppendIndentedLines(sb, transformed, "            ");
                    }
                }

                // Handle block exit
                switch (basicBlock.Exit)
                {
                    case BlockExit.FallThrough:
                        if (basicBlock.FallThroughTarget.HasValue)
                        {
                            sb.AppendLine($"            __state = {basicBlock.FallThroughTarget.Value};");
                            sb.AppendLine("            continue __gotoLoop;");
                        }
                        else
                        {
                            sb.AppendLine("            break __gotoLoop;");
                        }
                        break;

                    case BlockExit.Goto:
                        // Already handled by TransformBasicBlockStatement (goto -> __state = N; continue __gotoLoop;).
                        break;

                    case BlockExit.ConditionalGoto:
                        // Conditional goto is handled within the if statement transformation
                        // Fall through to next block if condition is false
                        if (basicBlock.FallThroughTarget.HasValue)
                        {
                            sb.AppendLine($"            __state = {basicBlock.FallThroughTarget.Value};");
                            sb.AppendLine("            continue __gotoLoop;");
                        }
                        else
                        {
                            sb.AppendLine("            break __gotoLoop;");
                        }
                        break;

                    case BlockExit.Return:
                    case BlockExit.Break:
                        // Already handled by the statement itself
                        break;
                }
            }
        }
        finally
        {
            context.MethodState.PopScope();
        }

        sb.AppendLine("        default:");
        sb.AppendLine("            break __gotoLoop;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        // Add a fallback return if the method has a non-void return type
        // (Java requires all paths to return a value, and the compiler can't
        // verify that the state machine always hits a return case)
        if (block.Parent is MethodDeclarationSyntax methodDecl &&
            !methodDecl.ReturnType.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PredefinedType) &&
            !(methodDecl.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax pt &&
              pt.Keyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword)))
        {
            // Non-predefined return type - add throw as fallback.
            sb.AppendLine("throw new IllegalStateException(\"Unexpected state\");");
        }
        else if (block.Parent is MethodDeclarationSyntax md &&
            md.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax pdt &&
            !pdt.Keyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword))
        {
            // Predefined non-void return type - add default return.
            var returnType = pdt.Keyword.Text;
            var defaultReturn = returnType switch
            {
                "int" or "long" or "short" or "byte" or "float" or "double" or "char" => "return 0;",
                "bool" => "return false;",
                _ => "return null;"
            };
            sb.AppendLine(defaultReturn);
        }

        // Post-process: convert hoisted variable declarations inside the while loop to assignments.
        // Variables declared inside try-catch, fixed, or other nested blocks were hoisted outside
        // the while loop, but their declarations inside the loop body were not converted to assignments
        // by TransformBasicBlockStatement (which only handles top-level LocalDeclarationStatementSyntax).
        var result = sb.ToString();
        if (hoistedVarNames.Count > 0)
        {
            result = ConvertHoistedDeclarationsToAssignments(result, hoistedVarNames);
        }

        // Post-process: remove unreachable code after while(true) loops inside the state machine.
        // When a while(true) loop only exits via state transitions (__state = N; continue __gotoLoop;),
        // any code after it (labels, state transitions) is unreachable and causes Java compilation errors.
        result = RemoveUnreachableCodeAfterInfiniteLoops(result);

        return result;
    }

    private string TransformLabeledBasicBlockStatement(
        string labelName,
        StatementSyntax stmt,
        ConversionContext context,
        IReadOnlyDictionary<string, int> labelToBlockIndex)
    {
        var transformed = TransformBasicBlockStatement(stmt, context, labelToBlockIndex);

        if (stmt is ForStatementSyntax
            or WhileStatementSyntax
            or DoStatementSyntax
            or ForEachStatementSyntax
            or BlockSyntax)
        {
            return $"{labelName}: {transformed}";
        }

        return $"{labelName}: {{ {transformed} }}";
    }

    /// <summary>
    /// Transforms a single statement within a basic block, handling goto conversions
    /// to state machine transitions.
    /// </summary>
    private string TransformBasicBlockStatement(
        StatementSyntax stmt,
        ConversionContext context,
        IReadOnlyDictionary<string, int> labelToBlockIndex)
    {
        switch (stmt)
        {
            case GotoStatementSyntax gotoStmt:
                return TransformGotoInStateMachine(gotoStmt, context, labelToBlockIndex);

            case IfStatementSyntax ifStmt:
                return TransformIfWithGotoInStateMachine(ifStmt, context, labelToBlockIndex);

            case LocalDeclarationStatementSyntax localDecl:
                return TransformHoistedLocalDeclarationAssignment(localDecl, context);

            case LabeledStatementSyntax labeled:
                // Labels inside basic blocks are handled by the case statement
                // Just transform the inner statement
                return TransformBasicBlockStatement(labeled.Statement, context, labelToBlockIndex);

            default:
                return Transform(stmt, context).ToString("");
        }
    }

    /// <summary>
    /// Transforms a goto statement within the state machine to a state transition.
    /// </summary>
    private string TransformGotoInStateMachine(
        GotoStatementSyntax gotoStmt,
        ConversionContext context,
        IReadOnlyDictionary<string, int> labelToBlockIndex)
    {
        // goto case / goto default should not appear here (handled by switch transformer)
        if (gotoStmt.Kind() == SyntaxKind.GotoCaseStatement ||
            gotoStmt.Kind() == SyntaxKind.GotoDefaultStatement)
        {
            return Transform(gotoStmt, context).ToString("");
        }

        var targetLabel = (gotoStmt.Expression as IdentifierNameSyntax)?.Identifier.Text;
        if (targetLabel == null)
        {
            return "/* TODO: goto - unsupported pattern */";
        }

        if (labelToBlockIndex.TryGetValue(targetLabel, out var blockIndex))
        {
            return $"__state = {blockIndex}; continue __gotoLoop;";
        }

        // Fallback: check if it's a loop label that can use continue
        if (context.Labels.TryGetLabel(targetLabel, out var labelInfo) &&
            labelInfo!.Kind == LabelKind.Loop)
        {
            return $"continue {targetLabel};";
        }

        return $"/* TODO: goto {targetLabel} - label not found in state machine */";
    }

    /// <summary>
    /// Transforms an if statement that contains goto within the state machine.
    /// The goto in the if body becomes a conditional state transition.
    /// </summary>
    private string TransformIfWithGotoInStateMachine(
        IfStatementSyntax ifStmt,
        ConversionContext context,
        IReadOnlyDictionary<string, int> labelToBlockIndex)
    {
        var exprTransformer = Expression.ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(ifStmt.Condition, context);
        var conditionPreamble = DrainPendingPreStatementText(context);
        var effectiveCondition = condition;
        var thenReadBacks = "";

        if (context.HasPendingPostStatements)
        {
            var postStatements = context.DrainPostStatements();
            var bodyScoped = new List<string>();
            var preIfScoped = new List<string>();
            foreach (var statement in postStatements)
            {
                var eqIdx = statement.IndexOf('=');
                if (eqIdx > 0 && statement.Substring(0, eqIdx).Trim().Contains(' '))
                    bodyScoped.Add(statement);
                else
                    preIfScoped.Add(statement);
            }

            if (preIfScoped.Count > 0)
            {
                var conditionTemp = context.GenerateSyntheticName("_ifCond");
                conditionPreamble += $"var {conditionTemp} = {condition};\n";
                conditionPreamble += FormatStatementLines(preIfScoped) + "\n";
                effectiveCondition = conditionTemp;
            }

            if (bodyScoped.Count > 0)
            {
                thenReadBacks = FormatStatementLines(bodyScoped);
            }
        }

        var sb = new StringBuilder();
        sb.Append(conditionPreamble);
        sb.Append($"if ({effectiveCondition}) ");

        // Transform the if body
        if (ifStmt.Statement is BlockSyntax ifBlock)
        {
            var bodyStmts = new List<string>();
            foreach (var s in ifBlock.Statements)
            {
                var transformed = TransformBasicBlockStatement(s, context, labelToBlockIndex);
                if (!string.IsNullOrWhiteSpace(transformed))
                    bodyStmts.Add(transformed);

                if (IsUnconditionalJump(s))
                    break;
            }
            sb.AppendLine("{");
            if (!string.IsNullOrWhiteSpace(thenReadBacks))
                AppendIndentedLines(sb, thenReadBacks, "        ");
            foreach (var s in bodyStmts)
                AppendIndentedLines(sb, s, "        ");
            sb.Append("    }");
        }
        else
        {
            var body = TransformBasicBlockStatement(ifStmt.Statement, context, labelToBlockIndex);
            // If the body contains multiple statements (e.g., "__state = N; continue __gotoLoop;"),
            // wrap them in a block to avoid dangling statements after the if condition
            if (body.Contains('\n') || body.Contains(';') && body.IndexOf(';') != body.LastIndexOf(';'))
            {
                sb.AppendLine("{");
                if (!string.IsNullOrWhiteSpace(thenReadBacks))
                    AppendIndentedLines(sb, thenReadBacks, "        ");
                AppendIndentedLines(sb, body, "        ");
                sb.Append("    }");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(thenReadBacks))
                {
                    sb.AppendLine("{");
                    AppendIndentedLines(sb, thenReadBacks, "        ");
                    AppendIndentedLines(sb, body, "        ");
                    sb.Append("    }");
                }
                else
                {
                sb.AppendLine(body);
                }
            }
        }

        // Handle else
        if (ifStmt.Else != null)
        {
            sb.Append(" else ");
            if (ifStmt.Else.Statement is BlockSyntax elseBlock)
            {
                var elseStmts = new List<string>();
                foreach (var s in elseBlock.Statements)
                {
                    var transformed = TransformBasicBlockStatement(s, context, labelToBlockIndex);
                    if (!string.IsNullOrWhiteSpace(transformed))
                        elseStmts.Add(transformed);

                    if (IsUnconditionalJump(s))
                        break;
                }
                sb.AppendLine("{");
                foreach (var s in elseStmts)
                    AppendIndentedLines(sb, s, "        ");
                sb.Append("    }");
            }
            else if (ifStmt.Else.Statement is IfStatementSyntax elseIf)
            {
                var elseIfText = TransformIfWithGotoInStateMachine(elseIf, context, labelToBlockIndex);
                if (elseIfText.TrimStart().StartsWith("if ", StringComparison.Ordinal))
                {
                    sb.Append(elseIfText);
                }
                else
                {
                    sb.AppendLine("{");
                    AppendIndentedLines(sb, elseIfText, "        ");
                    sb.Append("    }");
                }
            }
            else
            {
                var elseBody = TransformBasicBlockStatement(ifStmt.Else.Statement, context, labelToBlockIndex);
                if (elseBody.Contains('\n') || elseBody.Contains(';') && elseBody.IndexOf(';') != elseBody.LastIndexOf(';'))
                {
                    sb.AppendLine("{");
                    AppendIndentedLines(sb, elseBody, "        ");
                    sb.Append("    }");
                }
                else
                {
                    sb.AppendLine(elseBody);
                }
            }
        }

        return sb.ToString();
    }

    private string TransformHoistedLocalDeclarationAssignment(
        LocalDeclarationStatementSyntax stmt,
        ConversionContext context)
    {
        var exprTransformer = Expression.ExpressionTransformerFacade.Instance;
        var assignments = new List<string>();

        foreach (var variable in stmt.Declaration.Variables)
        {
            if (variable.Initializer == null)
            {
                continue;
            }

            var varName = ConversionContext.EscapeJavaKeyword(variable.Identifier.Text);
            var expr = exprTransformer.Transform(variable.Initializer.Value, context);
            if (context.HasPendingPreStatements)
            {
                assignments.Add(DrainPendingPreStatementText(context).TrimEnd());
            }

            assignments.Add($"{varName} = {expr};");

            if (context.HasPendingPostStatements)
            {
                assignments.Add(DrainPendingPostStatementText(context).TrimEnd());
            }
        }

        return string.Join("\n", assignments);
    }

    private static string DrainPendingPreStatementText(ConversionContext context)
    {
        return context.HasPendingPreStatements
            ? FormatStatementLines(context.DrainPreStatements()) + "\n"
            : "";
    }

    private static string DrainPendingPostStatementText(ConversionContext context)
    {
        return context.HasPendingPostStatements
            ? FormatStatementLines(context.DrainPostStatements()) + "\n"
            : "";
    }

    private static string FormatStatementLines(IEnumerable<string> statements)
    {
        return string.Join("\n", statements.Select(FormatStatementLine));
    }

    private static string FormatStatementLine(string statement)
    {
        return statement.Trim().TrimEnd(';') + ";";
    }

    /// <summary>
    /// Post-processes the state machine code to convert hoisted variable declarations
    /// inside the while loop to assignments. This handles cases where declarations are
    /// inside try-catch, fixed, or other nested blocks that weren't converted by
    /// TransformBasicBlockStatement.
    /// </summary>
    private static string ConvertHoistedDeclarationsToAssignments(string code, HashSet<string> hoistedVarNames)
    {
        // Pattern matches: Type varName = (where Type is a Java type and varName is a hoisted variable)
        // We need to find the while loop section and only replace within it
        var whileStart = code.IndexOf("__gotoLoop: while (true)", StringComparison.Ordinal);
        if (whileStart < 0)
            return code;

        var prefix = code.Substring(0, whileStart);
        var body = code.Substring(whileStart);

        foreach (var varName in hoistedVarNames)
        {
            // Match: Type varName = or Type[] varName = or Type<Generic> varName =
            // The type can be: simple (int, String), qualified (MemorySegment, UriFormatException),
            // generic (Span<Character>), or array (byte[], Character[][])
            var pattern = $@"((?<=^\s*)[\w.]+(?:<[^>]+>)?(?:\[\])*)\s+\b{Regex.Escape(varName)}\b\s*=";
            body = Regex.Replace(body, pattern, $"{varName} =", RegexOptions.Multiline);
        }

        return prefix + body;
    }

    /// <summary>
    /// Removes unreachable code that appears after while(true) loops inside the state machine.
    /// When a while(true) loop only exits via state transitions (__state = N; continue __gotoLoop;),
    /// any code after the closing brace of that while(true) is unreachable.
    /// Pattern: while (true) { ... } labelName: { } __state = N; continue __gotoLoop;
    /// The label block and state transition after the while(true) are removed.
    /// </summary>
    private static string RemoveUnreachableCodeAfterInfiniteLoops(string code)
    {
        // Pattern: after a while(true) { ... } closing brace, there may be:
        // 1. A label block: labelName: { }
        // 2. A state transition: __state = N; continue __gotoLoop;
        // These are unreachable if the while(true) only exits via state transitions.
        // We detect this by finding while(true) { ... } blocks where the body only
        // exits via __state/continue __gotoLoop, and remove code after the closing }.

        var lines = code.Split('\n');
        var result = new List<string>(lines.Length);
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            result.Add(line);

            // Detect closing brace of a while(true) block that only exits via state transitions
            var trimmed = line.Trim();
            if (trimmed == "}" && i >= 2)
            {
                // Look backwards to see if this closes a while(true) { ... } block
                // where the body only has state-transition exits
                if (IsClosingBraceOfInfiniteWhileLoop(lines, i, result))
                {
                    // Skip unreachable lines after this closing brace until we hit
                    // a line that is not a label block or state transition
                    i++;
                    while (i < lines.Length)
                    {
                        var nextTrimmed = lines[i].Trim();
                        // Skip label blocks: labelName: { } or labelName: { ... }
                        if (Regex.IsMatch(nextTrimmed, @"^\w+:\s*\{?\s*\}?\s*$"))
                        {
                            i++;
                            continue;
                        }
                        // Skip state transitions: __state = N; continue __gotoLoop;
                        if (Regex.IsMatch(nextTrimmed, @"^__state\s*=\s*\d+;\s*continue\s+__gotoLoop;") ||
                            nextTrimmed.StartsWith("__state =") ||
                            nextTrimmed == "continue __gotoLoop;")
                        {
                            i++;
                            continue;
                        }
                        // Not unreachable pattern - stop skipping
                        break;
                    }
                    continue;
                }
            }

            i++;
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Checks if the closing brace at lineIndex closes a while(true) block
    /// that only exits via state transitions (no break/return that would make
    /// code after it reachable).
    /// </summary>
    private static bool IsClosingBraceOfInfiniteWhileLoop(string[] lines, int lineIndex, List<string> result)
    {
        // Find the matching opening brace by scanning backwards
        var depth = 1;
        var start = lineIndex - 1;
        while (start >= 0 && depth > 0)
        {
            var trimmed = lines[start].Trim();
            foreach (var ch in trimmed.Reverse())
            {
                if (ch == '}') depth++;
                else if (ch == '{') depth--;
                if (depth == 0) break;
            }
            if (depth > 0) start--;
        }

        if (start < 0) return false;

        // Check if the line before or at start contains "while (true)"
        for (var j = start; j >= Math.Max(0, start - 2); j--)
        {
            if (lines[j].Contains("while (true)"))
            {
                // Check if the while(true) body only exits via state transitions
                // by checking if there are any break/return statements that aren't
                // inside nested blocks
                var bodySb = new StringBuilder();
                for (var k = j + 1; k < lineIndex; k++)
                {
                    bodySb.AppendLine(lines[k]);
                }
                var bodyTextStr = bodySb.ToString();

                // If the body contains "break __gotoLoop" that
                // would exit the while(true) normally, code after it might be reachable
                if (bodyTextStr.Contains("break __gotoLoop") || bodyTextStr.Contains("break __gotoLoop;"))
                {
                    return false;
                }

                // Check for standalone "break;" statements that exit THIS while(true) loop
                // (not inside nested loops). We track nested loop depth to determine this.
                var nestedLoopDepth = 0;
                for (var k = j + 1; k < lineIndex; k++)
                {
                    var lineTrimmed = lines[k].Trim();
                    // Track entering nested loops
                    if (lineTrimmed.StartsWith("while (") || lineTrimmed.StartsWith("for (") ||
                        lineTrimmed.StartsWith("for (;") || lineTrimmed.StartsWith("do {") ||
                        lineTrimmed.StartsWith("do "))
                    {
                        nestedLoopDepth++;
                    }
                    // Track exiting nested loops (closing braces reduce depth if we're in a nested loop)
                    // This is a simple heuristic - we count { and } to track nesting
                    if (nestedLoopDepth > 0)
                    {
                        foreach (var ch in lineTrimmed)
                        {
                            if (ch == '{') nestedLoopDepth++; // rough tracking
                        }
                        foreach (var ch in lineTrimmed.Reverse())
                        {
                            if (ch == '}' && nestedLoopDepth > 0) nestedLoopDepth--;
                        }
                    }

                    if (lineTrimmed == "break;" && nestedLoopDepth == 0)
                    {
                        // This break; exits the while(true) loop at the top level
                        return false;
                    }
                }

                // If the body only has state transitions as exits, code after is unreachable
                return true;
            }
        }

        return false;
    }

    private (List<string> declarations, HashSet<string> varNames) CollectStateMachineLocalDeclarations(
        IReadOnlyList<BasicBlock> basicBlocks,
        ConversionContext context)
    {
        var locals = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var varNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in CollectRewrittenLocalDeclarations(basicBlocks))
        {
            var typeInfo = context.GetTypeInfo(declaration.Declaration.Type);
            var javaType = typeInfo.Type != null
                ? context.MapType(typeInfo.Type)
                : context.MapTypeFromSyntax(declaration.Declaration.Type);

            foreach (var variable in declaration.Declaration.Variables)
            {
                var varName = ConversionContext.EscapeJavaKeyword(variable.Identifier.Text);
                if (!seen.Add(varName))
                {
                    continue;
                }

                varNames.Add(varName);
                locals.Add($"{javaType} {varName} = {GetJavaDefaultValue(javaType)};");
            }
        }

        // Also collect variables from fixed statements (e.g., fixed (char* str = _string))
        foreach (var fixedDecl in CollectFixedStatementDeclarations(basicBlocks))
        {
            var varName = ConversionContext.EscapeJavaKeyword(fixedDecl.Identifier.Text);
            if (!seen.Add(varName))
            {
                continue;
            }

            varNames.Add(varName);
            // fixed (char* str = ...) maps to MemorySegment in Java
            locals.Add($"MemorySegment {varName} = null;");
        }

        return (locals, varNames);
    }

    private static IEnumerable<LocalDeclarationStatementSyntax> CollectRewrittenLocalDeclarations(
        IReadOnlyList<BasicBlock> basicBlocks)
    {
        foreach (var basicBlock in basicBlocks)
        {
            foreach (var statement in basicBlock.Statements)
            {
                foreach (var declaration in CollectRewrittenLocalDeclarations(statement))
                {
                    yield return declaration;
                }
            }
        }
    }

    /// <summary>
    /// Collects variable declarators from fixed statements (e.g., fixed (char* str = _string))
    /// within basic blocks. These are not LocalDeclarationStatementSyntax and need separate handling.
    /// </summary>
    private static IEnumerable<VariableDeclaratorSyntax> CollectFixedStatementDeclarations(
        IReadOnlyList<BasicBlock> basicBlocks)
    {
        foreach (var basicBlock in basicBlocks)
        {
            foreach (var statement in basicBlock.Statements)
            {
                foreach (var decl in CollectFixedStatementDeclarations(statement))
                {
                    yield return decl;
                }
            }
        }
    }

    private static IEnumerable<VariableDeclaratorSyntax> CollectFixedStatementDeclarations(
        StatementSyntax statement)
    {
        switch (statement)
        {
            case FixedStatementSyntax fixedStmt:
                foreach (var variable in fixedStmt.Declaration.Variables)
                {
                    yield return variable;
                }
                // Also recurse into the fixed body
                if (fixedStmt.Statement is BlockSyntax block)
                {
                    foreach (var child in block.Statements)
                    {
                        foreach (var decl in CollectFixedStatementDeclarations(child))
                        {
                            yield return decl;
                        }
                    }
                }
                else
                {
                    foreach (var decl in CollectFixedStatementDeclarations(fixedStmt.Statement))
                    {
                        yield return decl;
                    }
                }
                break;

            case TryStatementSyntax tryStatement:
                foreach (var decl in CollectFixedStatementDeclarationsInBlock(tryStatement.Block))
                    yield return decl;
                foreach (var catchClause in tryStatement.Catches)
                    foreach (var decl in CollectFixedStatementDeclarationsInBlock(catchClause.Block))
                        yield return decl;
                if (tryStatement.Finally != null)
                    foreach (var decl in CollectFixedStatementDeclarationsInBlock(tryStatement.Finally.Block))
                        yield return decl;
                break;

            case IfStatementSyntax ifStatement:
                foreach (var decl in CollectFixedStatementDeclarationsInIfBranch(ifStatement.Statement))
                    yield return decl;
                if (ifStatement.Else != null)
                    foreach (var decl in CollectFixedStatementDeclarationsInIfBranch(ifStatement.Else.Statement))
                        yield return decl;
                break;

            case BlockSyntax blockStmt:
                foreach (var decl in CollectFixedStatementDeclarationsInBlock(blockStmt))
                    yield return decl;
                break;

            case LabeledStatementSyntax labeled:
                foreach (var decl in CollectFixedStatementDeclarations(labeled.Statement))
                    yield return decl;
                break;

            case WhileStatementSyntax whileStatement:
                if (whileStatement.Statement is BlockSyntax whileBlock)
                    foreach (var decl in CollectFixedStatementDeclarationsInBlock(whileBlock))
                        yield return decl;
                else
                    foreach (var decl in CollectFixedStatementDeclarations(whileStatement.Statement))
                        yield return decl;
                break;

            case ForStatementSyntax forStatement:
                if (forStatement.Statement is BlockSyntax forBlock)
                    foreach (var decl in CollectFixedStatementDeclarationsInBlock(forBlock))
                        yield return decl;
                break;

            case ForEachStatementSyntax forEachStatement:
                if (forEachStatement.Statement is BlockSyntax forEachBlock)
                    foreach (var decl in CollectFixedStatementDeclarationsInBlock(forEachBlock))
                        yield return decl;
                break;

            case SwitchStatementSyntax switchStatement:
                foreach (var section in switchStatement.Sections)
                    foreach (var s in section.Statements)
                        foreach (var decl in CollectFixedStatementDeclarations(s))
                            yield return decl;
                break;

            case UsingStatementSyntax usingStatement:
                if (usingStatement.Statement is BlockSyntax usingBlock)
                    foreach (var decl in CollectFixedStatementDeclarationsInBlock(usingBlock))
                        yield return decl;
                else
                    foreach (var decl in CollectFixedStatementDeclarations(usingStatement.Statement))
                        yield return decl;
                break;

            case LockStatementSyntax lockStatement:
                if (lockStatement.Statement is BlockSyntax lockBlock)
                    foreach (var decl in CollectFixedStatementDeclarationsInBlock(lockBlock))
                        yield return decl;
                else
                    foreach (var decl in CollectFixedStatementDeclarations(lockStatement.Statement))
                        yield return decl;
                break;
        }
    }

    private static IEnumerable<VariableDeclaratorSyntax> CollectFixedStatementDeclarationsInBlock(
        BlockSyntax? block)
    {
        if (block == null) yield break;
        foreach (var child in block.Statements)
        {
            foreach (var decl in CollectFixedStatementDeclarations(child))
            {
                yield return decl;
            }
        }
    }

    private static IEnumerable<VariableDeclaratorSyntax> CollectFixedStatementDeclarationsInIfBranch(
        StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
        {
            foreach (var child in block.Statements)
            {
                foreach (var decl in CollectFixedStatementDeclarations(child))
                {
                    yield return decl;
                }
            }
            yield break;
        }

        foreach (var decl in CollectFixedStatementDeclarations(statement))
        {
            yield return decl;
        }
    }

    private static IEnumerable<LocalDeclarationStatementSyntax> CollectRewrittenLocalDeclarations(
        StatementSyntax statement)
    {
        switch (statement)
        {
            case LocalDeclarationStatementSyntax localDeclaration:
                yield return localDeclaration;
                break;

            case LabeledStatementSyntax labeled:
                foreach (var declaration in CollectRewrittenLocalDeclarations(labeled.Statement))
                {
                    yield return declaration;
                }
                break;

            case IfStatementSyntax ifStatement:
                foreach (var declaration in CollectRewrittenLocalDeclarationsInIfBranch(ifStatement.Statement))
                {
                    yield return declaration;
                }

                if (ifStatement.Else != null)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInIfBranch(ifStatement.Else.Statement))
                    {
                        yield return declaration;
                    }
                }
                break;

            case TryStatementSyntax tryStatement:
                foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(tryStatement.Block))
                {
                    yield return declaration;
                }
                foreach (var catchClause in tryStatement.Catches)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(catchClause.Block))
                    {
                        yield return declaration;
                    }
                }
                if (tryStatement.Finally != null)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(tryStatement.Finally.Block))
                    {
                        yield return declaration;
                    }
                }
                break;

            case FixedStatementSyntax fixedStatement:
                foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(fixedStatement.Statement as BlockSyntax))
                {
                    yield return declaration;
                }
                if (fixedStatement.Statement is not BlockSyntax)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarations(fixedStatement.Statement))
                    {
                        yield return declaration;
                    }
                }
                break;

            case BlockSyntax block:
                foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(block))
                {
                    yield return declaration;
                }
                break;

            case WhileStatementSyntax whileStatement:
                if (whileStatement.Statement is BlockSyntax whileBlock)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(whileBlock))
                    {
                        yield return declaration;
                    }
                }
                else
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarations(whileStatement.Statement))
                    {
                        yield return declaration;
                    }
                }
                break;

            case ForStatementSyntax forStatement:
                // for variable declarations are handled separately (they stay in the for loop)
                if (forStatement.Statement is BlockSyntax forBlock)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(forBlock))
                    {
                        yield return declaration;
                    }
                }
                break;

            case ForEachStatementSyntax forEachStatement:
                if (forEachStatement.Statement is BlockSyntax forEachBlock)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(forEachBlock))
                    {
                        yield return declaration;
                    }
                }
                break;

            case SwitchStatementSyntax switchStatement:
                foreach (var section in switchStatement.Sections)
                {
                    foreach (var s in section.Statements)
                    {
                        foreach (var declaration in CollectRewrittenLocalDeclarations(s))
                        {
                            yield return declaration;
                        }
                    }
                }
                break;

            case UsingStatementSyntax usingStatement:
                if (usingStatement.Statement is BlockSyntax usingBlock)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(usingBlock))
                    {
                        yield return declaration;
                    }
                }
                else
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarations(usingStatement.Statement))
                    {
                        yield return declaration;
                    }
                }
                break;

            case LockStatementSyntax lockStatement:
                if (lockStatement.Statement is BlockSyntax lockBlock)
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarationsInBlock(lockBlock))
                    {
                        yield return declaration;
                    }
                }
                else
                {
                    foreach (var declaration in CollectRewrittenLocalDeclarations(lockStatement.Statement))
                    {
                        yield return declaration;
                    }
                }
                break;
        }
    }

    private static IEnumerable<LocalDeclarationStatementSyntax> CollectRewrittenLocalDeclarationsInBlock(
        BlockSyntax? block)
    {
        if (block == null) yield break;
        foreach (var child in block.Statements)
        {
            foreach (var declaration in CollectRewrittenLocalDeclarations(child))
            {
                yield return declaration;
            }
        }
    }

    private static IEnumerable<LocalDeclarationStatementSyntax> CollectRewrittenLocalDeclarationsInIfBranch(
        StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
        {
            foreach (var child in block.Statements)
            {
                foreach (var declaration in CollectRewrittenLocalDeclarations(child))
                {
                    yield return declaration;
                }

                if (IsUnconditionalJump(child))
                {
                    yield break;
                }
            }

            yield break;
        }

        foreach (var declaration in CollectRewrittenLocalDeclarations(statement))
        {
            yield return declaration;
        }
    }

    private static string GetJavaDefaultValue(string javaType)
    {
        return javaType switch
        {
            "boolean" => "false",
            "byte" or "short" or "int" or "long" => "0",
            "float" => "0f",
            "double" => "0d",
            "char" => "'\\0'",
            _ => "null"
        };
    }

    private static void AppendIndentedLines(StringBuilder sb, string text, string indentation)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine();
            }
            else
            {
                sb.Append(indentation).AppendLine(line.TrimEnd());
            }
        }
    }
}
