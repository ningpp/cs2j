using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using System.Text;

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
        if (stmt.Kind() == SyntaxKind.GotoCaseStatement)
        {
            return new JavaStatementNode("/* TODO: goto case - unsupported */");
        }
        if (stmt.Kind() == SyntaxKind.GotoDefaultStatement)
        {
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

        // Check if this is a cross-scope goto
        var gotoAnalyzer = context.MethodState.GotoAnalyzer;
        if (gotoAnalyzer != null && gotoAnalyzer.CrossScopeLabels.Contains(targetLabel))
        {
            // Cross-scope goto - use state machine
            var stateValue = GetLabelStateValue(targetLabel, context);
            return new JavaStatementNode($"__gotoState = {stateValue}; continue __gotoLoop;");
        }

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

    private static int GetLabelStateValue(string labelName, ConversionContext context)
    {
        var gotoAnalyzer = context.MethodState.GotoAnalyzer;
        if (gotoAnalyzer == null) return 0;

        var labels = gotoAnalyzer.CrossScopeLabels.OrderBy(l => l).ToList();
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
    /// Transforms a method body that contains cross-scope goto statements
    /// into a state machine using a while loop and state variable.
    /// </summary>
    internal string TransformBlockWithStateMachine(BlockSyntax block, ConversionContext context, GotoAnalyzer gotoAnalyzer)
    {
        var sb = new StringBuilder();

        // Generate state variable
        sb.AppendLine("int __gotoState = 0;");
        sb.AppendLine("__gotoLoop: while (true) {");
        sb.AppendLine("    switch (__gotoState) {");
        sb.AppendLine("        case 0: // Initial state");

        // Transform the method body with state transitions
        context.MethodState.PushScope();
        var statements = TransformStatementsWithStateLabels(block.Statements, context, gotoAnalyzer);
        context.MethodState.PopScope();

        foreach (var stmt in statements)
        {
            sb.AppendLine($"            {stmt}");
        }

        sb.AppendLine("            break __gotoLoop; // Exit state machine");

        // Generate case labels for each cross-scope label
        var labels = gotoAnalyzer.CrossScopeLabels.OrderBy(l => l).ToList();
        for (int i = 0; i < labels.Count; i++)
        {
            var labelName = labels[i];
            var stateValue = i + 1;
            sb.AppendLine($"        case {stateValue}: // Label: {labelName}");
            sb.AppendLine($"            __gotoState = 0; // Reset to continue from label");
            sb.AppendLine($"            // Jump to label {labelName} - continue execution");
            sb.AppendLine($"            break;");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Transforms statements and inserts state labels for cross-scope goto targets.
    /// </summary>
    private List<string> TransformStatementsWithStateLabels(
        SyntaxList<StatementSyntax> statements,
        ConversionContext context,
        GotoAnalyzer gotoAnalyzer)
    {
        var result = new List<string>();
        var crossScopeLabels = gotoAnalyzer.CrossScopeLabels;

        foreach (var statement in statements)
        {
            // Check if this statement is a labeled statement with a cross-scope target
            if (statement is LabeledStatementSyntax labeled
                && crossScopeLabels.Contains(labeled.Identifier.Text))
            {
                // Insert state check for this label
                var labelName = labeled.Identifier.Text;
                var stateValue = GetLabelStateValue(labelName, context);
                result.Add($"// State label: {labelName}");
                result.Add($"if (__gotoState == {stateValue}) {{ __gotoState = 0; }}");
            }

            var transformed = Transform(statement, context);
            var code = transformed.ToString("");

            if (!string.IsNullOrWhiteSpace(code))
            {
                result.Add(code);
            }
        }

        return result;
    }
}

