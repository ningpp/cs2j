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
        var crossScopeLabels = gotoAnalyzer.CrossScopeLabels.OrderBy(l => l).ToList();
        var labelStates = crossScopeLabels
            .Select((label, index) => (label, state: index + 1))
            .ToDictionary(item => item.label, item => item.state, StringComparer.Ordinal);
        var segments = BuildGotoSegments(block.Statements, labelStates);

        foreach (var hoistedLocal in CollectStateMachineLocalDeclarations(block, context))
        {
            sb.AppendLine(hoistedLocal);
        }

        sb.AppendLine("int __gotoState = 0;");
        sb.AppendLine("__gotoLoop: while (true) {");
        sb.AppendLine("    switch (__gotoState) {");

        context.MethodState.PushScope();
        try
        {
            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                sb.AppendLine($"        case {segment.State}:");
                var emitted = TransformGotoSegmentStatements(segment.Statements, context, hoistLocalDeclarations: true);
                foreach (var stmt in emitted)
                {
                    AppendIndentedLines(sb, stmt, "            ");
                }

                if (!SegmentEndsWithUnconditionalJump(segment.Statements))
                {
                    var nextState = i + 1 < segments.Count ? segments[i + 1].State : -1;
                    if (nextState >= 0)
                    {
                        sb.AppendLine($"            __gotoState = {nextState};");
                        sb.AppendLine("            continue __gotoLoop;");
                    }
                    else
                    {
                        sb.AppendLine("            break __gotoLoop;");
                    }
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
        sb.AppendLine("throw new IllegalStateException(\"Unreachable goto state\");");

        return sb.ToString();
    }

    private sealed record GotoSegment(int State, List<StatementSyntax> Statements);

    private static List<GotoSegment> BuildGotoSegments(
        SyntaxList<StatementSyntax> statements,
        IReadOnlyDictionary<string, int> labelStates)
    {
        var flattenedStatements = FlattenSegmentStatements(statements, labelStates);
        var segments = new List<GotoSegment>();
        var current = new GotoSegment(0, new List<StatementSyntax>());
        segments.Add(current);

        foreach (var statement in flattenedStatements)
        {
            if (statement is LabeledStatementSyntax labeled
                && labelStates.TryGetValue(labeled.Identifier.Text, out var state))
            {
                current = new GotoSegment(state, new List<StatementSyntax>());
                segments.Add(current);
            }

            current.Statements.Add(statement);
        }

        return segments.Where(segment => segment.Statements.Count > 0).ToList();
    }

    private static List<StatementSyntax> FlattenSegmentStatements(
        IEnumerable<StatementSyntax> statements,
        IReadOnlyDictionary<string, int> labelStates)
    {
        var result = new List<StatementSyntax>();

        foreach (var statement in statements)
        {
            if (statement is BlockSyntax block && ContainsTargetLabel(block, labelStates))
            {
                result.AddRange(FlattenSegmentStatements(block.Statements, labelStates));
            }
            else
            {
                result.Add(statement);
            }
        }

        return result;
    }

    private static bool ContainsTargetLabel(
        SyntaxNode node,
        IReadOnlyDictionary<string, int> labelStates)
    {
        return node.DescendantNodesAndSelf()
            .OfType<LabeledStatementSyntax>()
            .Any(label => labelStates.ContainsKey(label.Identifier.Text));
    }

    private List<string> TransformGotoSegmentStatements(
        IReadOnlyList<StatementSyntax> statements,
        ConversionContext context,
        bool hoistLocalDeclarations)
    {
        var result = new List<string>();

        foreach (var statement in statements)
        {
            var transformed = hoistLocalDeclarations && statement is LocalDeclarationStatementSyntax localDeclaration
                ? TransformHoistedLocalDeclarationAssignment(localDeclaration, context)
                : Transform(statement, context).ToString("");

            if (!string.IsNullOrWhiteSpace(transformed))
            {
                result.Add(transformed);
            }

            if (IsUnconditionalJump(statement))
            {
                break;
            }
        }

        return result;
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
            assignments.Add($"{varName} = {expr};");
        }

        return string.Join("\n", assignments);
    }

    private List<string> CollectStateMachineLocalDeclarations(BlockSyntax block, ConversionContext context)
    {
        var locals = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaration in block.DescendantNodes(descendIntoChildren: node =>
                 node is not AnonymousFunctionExpressionSyntax
                     and not LocalFunctionStatementSyntax)
                 .OfType<LocalDeclarationStatementSyntax>())
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

                locals.Add($"{javaType} {varName} = {GetJavaDefaultValue(javaType)};");
            }
        }

        return locals;
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

    private static bool SegmentEndsWithUnconditionalJump(IReadOnlyList<StatementSyntax> statements)
    {
        if (statements.Count == 0)
        {
            return false;
        }

        return LastExecutableStatement(statements[^1]) is ReturnStatementSyntax
            or ThrowStatementSyntax
            or GotoStatementSyntax
            or BreakStatementSyntax
            or ContinueStatementSyntax;
    }

    private static StatementSyntax LastExecutableStatement(StatementSyntax statement)
    {
        return statement switch
        {
            LabeledStatementSyntax labeled => LastExecutableStatement(labeled.Statement),
            BlockSyntax block when block.Statements.Count > 0 => LastExecutableStatement(block.Statements[^1]),
            _ => statement
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
