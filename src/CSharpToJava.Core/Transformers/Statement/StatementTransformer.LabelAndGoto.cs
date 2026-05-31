using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

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
}
