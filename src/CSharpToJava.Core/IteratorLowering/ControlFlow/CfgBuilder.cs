using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IteratorLowering.ControlFlow;

/// <summary>
/// Control-flow analysis for one iterator member: segments delimited by yield
/// return points, state numbering, and liveness-based hoisting decisions.
/// </summary>
internal static class CfgBuilder
{
    /// <summary>
    /// Analyze a method body and produce a <see cref="CfgInfo"/> with resume
    /// states and hoisting decisions.
    /// </summary>
    public static CfgInfo Analyze(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        var body = method.Body ?? throw new InvalidOperationException(
            $"Method '{method.Identifier.Text}' has no block body (expression-bodied iterators not supported in M1).");

        // Assign resume states: yield return #0 -> state 1, #1 -> state 2, etc.
        var resumeStates = new List<int>();
        var yieldBreakCount = 0;

        foreach (var yield in body.DescendantNodes().OfType<YieldStatementSyntax>())
        {
            if (yield.IsKind(SyntaxKind.YieldReturnStatement))
            {
                resumeStates.Add(resumeStates.Count + 1);
            }
            else if (yield.IsKind(SyntaxKind.YieldBreakStatement))
            {
                yieldBreakCount++;
            }
        }

        // Hoisting: find all locals and parameters whose lifetime spans a yield.
        // In M1 we use a conservative approach: any local declared in a loop/foreach
        // that contains a yield return, and all parameters referenced in the body.
        var hoistedLocalNames = new List<string>();
        var hoistedParameterNames = new List<string>();

        // Parameters: hoist all parameters referenced in the body
        foreach (var param in method.ParameterList.Parameters)
        {
            var paramName = param.Identifier.ValueText;
            if (body.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == paramName))
            {
                hoistedParameterNames.Add(paramName);
            }
        }

        // Locals in loops containing yields: for-loop declarators and foreach iteration vars
        foreach (var forStmt in body.DescendantNodes().OfType<ForStatementSyntax>())
        {
            if (ContainsYieldReturn(forStmt))
            {
                if (forStmt.Declaration != null)
                {
                    foreach (var varDecl in forStmt.Declaration.Variables)
                    {
                        hoistedLocalNames.Add(varDecl.Identifier.ValueText);
                    }
                }
            }
        }

        foreach (var foreachStmt in body.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (ContainsYieldReturn(foreachStmt))
            {
                hoistedLocalNames.Add(foreachStmt.Identifier.ValueText);
            }
        }

        // While/do loops: hoist locals declared inside that are used across yield
        foreach (var whileStmt in body.DescendantNodes().OfType<WhileStatementSyntax>())
        {
            if (ContainsYieldReturn(whileStmt))
            {
                foreach (var localDecl in whileStmt.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    foreach (var varDecl in localDecl.Declaration.Variables)
                    {
                        hoistedLocalNames.Add(varDecl.Identifier.ValueText);
                    }
                }
            }
        }

        foreach (var doStmt in body.DescendantNodes().OfType<DoStatementSyntax>())
        {
            if (ContainsYieldReturn(doStmt))
            {
                foreach (var localDecl in doStmt.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    foreach (var varDecl in localDecl.Declaration.Variables)
                    {
                        hoistedLocalNames.Add(varDecl.Identifier.ValueText);
                    }
                }
            }
        }

        return new CfgInfo(
            YieldReturnCount: resumeStates.Count,
            ResumeStates: resumeStates,
            HasYieldBreak: yieldBreakCount > 0,
            HoistedLocalNames: hoistedLocalNames.Distinct().ToList(),
            HoistedParameterNames: hoistedParameterNames.Distinct().ToList());
    }

    private static bool ContainsYieldReturn(SyntaxNode node)
        => node.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement));
}

internal sealed record CfgInfo(
    int YieldReturnCount,
    IReadOnlyList<int> ResumeStates,
    bool HasYieldBreak,
    IReadOnlyList<string> HoistedLocalNames,
    IReadOnlyList<string> HoistedParameterNames);
