using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Represents a goto statement and its target label information.
/// </summary>
public record GotoInfo(
    GotoStatementSyntax GotoStatement,
    string TargetLabel,
    int GotoPosition,
    int LabelPosition,
    bool IsBackwardJump);

/// <summary>
/// Analyzes goto statements in a method to determine if cross-scope goto
/// transformation is needed (state machine wrapper).
/// </summary>
public class GotoAnalyzer
{
    private readonly List<GotoInfo> _crossScopeGotos = new();
    private readonly Dictionary<string, int> _labelPositions = new();
    private int _currentPosition = 0;

    /// <summary>
    /// Returns true if the method contains cross-scope goto statements
    /// that require state machine transformation.
    /// </summary>
    public bool HasCrossScopeGoto => _crossScopeGotos.Count > 0;

    /// <summary>
    /// Gets all cross-scope goto information.
    /// </summary>
    public IReadOnlyList<GotoInfo> CrossScopeGotos => _crossScopeGotos;

    /// <summary>
    /// Gets all labels that are targets of cross-scope gotos.
    /// </summary>
    public IReadOnlySet<string> CrossScopeLabels =>
        _crossScopeGotos.Select(g => g.TargetLabel).ToHashSet();

    /// <summary>
    /// Analyzes a method body for cross-scope goto patterns.
    /// </summary>
    public static GotoAnalyzer Analyze(BlockSyntax methodBody, LabelRegistry labelRegistry)
    {
        var analyzer = new GotoAnalyzer();
        analyzer.AnalyzeBlock(methodBody, labelRegistry);
        return analyzer;
    }

    private void AnalyzeBlock(BlockSyntax block, LabelRegistry labelRegistry)
    {
        foreach (var statement in block.Statements)
        {
            AnalyzeStatement(statement, labelRegistry);
        }
    }

    private void AnalyzeStatement(StatementSyntax statement, LabelRegistry labelRegistry)
    {
        _currentPosition++;

        switch (statement)
        {
            case LabeledStatementSyntax labeled:
                var labelName = labeled.Identifier.Text;
                _labelPositions[labelName] = _currentPosition;
                AnalyzeStatement(labeled.Statement, labelRegistry);
                break;

            case GotoStatementSyntax gotoStmt:
                if (gotoStmt.Expression is IdentifierNameSyntax identifier)
                {
                    var targetLabel = identifier.Identifier.Text;
                    if (labelRegistry.Contains(targetLabel) && !IsInLabelScope(gotoStmt, targetLabel))
                    {
                        // This is a cross-scope goto
                        var gotoPosition = _currentPosition;
                        var labelPosition = _labelPositions.GetValueOrDefault(targetLabel, int.MaxValue);
                        var isBackward = labelPosition < gotoPosition;

                        _crossScopeGotos.Add(new GotoInfo(
                            gotoStmt,
                            targetLabel,
                            gotoPosition,
                            labelPosition,
                            isBackward));
                    }
                }
                break;

            case BlockSyntax blockStmt:
                AnalyzeBlock(blockStmt, labelRegistry);
                break;

            case IfStatementSyntax ifStmt:
                AnalyzeStatement(ifStmt.Statement, labelRegistry);
                if (ifStmt.Else != null)
                    AnalyzeStatement(ifStmt.Else.Statement, labelRegistry);
                break;

            case WhileStatementSyntax whileStmt:
                AnalyzeStatement(whileStmt.Statement, labelRegistry);
                break;

            case ForStatementSyntax forStmt:
                AnalyzeStatement(forStmt.Statement, labelRegistry);
                break;

            case ForEachStatementSyntax foreachStmt:
                AnalyzeStatement(foreachStmt.Statement, labelRegistry);
                break;

            case DoStatementSyntax doStmt:
                AnalyzeStatement(doStmt.Statement, labelRegistry);
                break;

            case SwitchStatementSyntax switchStmt:
                foreach (var section in switchStmt.Sections)
                    foreach (var stmt in section.Statements)
                        AnalyzeStatement(stmt, labelRegistry);
                break;

            case TryStatementSyntax tryStmt:
                AnalyzeStatement(tryStmt.Block, labelRegistry);
                foreach (var catchClause in tryStmt.Catches)
                    AnalyzeStatement(catchClause.Block, labelRegistry);
                if (tryStmt.Finally != null)
                    AnalyzeStatement(tryStmt.Finally.Block, labelRegistry);
                break;

            case UsingStatementSyntax usingStmt:
                AnalyzeStatement(usingStmt.Statement, labelRegistry);
                break;

            case LockStatementSyntax lockStmt:
                AnalyzeStatement(lockStmt.Statement, labelRegistry);
                break;

            case FixedStatementSyntax fixedStmt:
                AnalyzeStatement(fixedStmt.Statement, labelRegistry);
                break;

            case UnsafeStatementSyntax unsafeStmt:
                if (unsafeStmt.Block != null)
                    AnalyzeStatement(unsafeStmt.Block, labelRegistry);
                break;

            case CheckedStatementSyntax checkedStmt:
                AnalyzeStatement(checkedStmt.Block, labelRegistry);
                break;
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
