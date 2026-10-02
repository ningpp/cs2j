using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 site inventory. M0 is deliberately syntax-level only: it counts sites
/// and item kinds into <see cref="InitializerStatistics"/> with no semantic
/// binding and no diagnostics. The full IOperation-driven classification
/// (design §2 decisions 2/7/8/10/11/12) lands in M1/M2 and will replace this
/// body without changing the rule's contract.
/// </summary>
internal static class InitializerBindingAnalyzer
{
    public static void Analyze(RuleAnalysisContext context)
    {
        var stats = context.Statistics.Initializers;

        foreach (var node in context.Root.DescendantNodes())
        {
            switch (node)
            {
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    stats.TargetTypedNewSites++;
                    if (implicitCreation.Initializer is null)
                        stats.TargetTypedNewBareSites++;
                    else
                    {
                        stats.TargetTypedNewWithInitializerSites++;
                        InventoryCreation(implicitCreation.Initializer, stats);
                    }
                    break;

                case ObjectCreationExpressionSyntax creation
                    when creation.Initializer is { } initializer:
                    InventoryCreation(initializer, stats);
                    break;
            }
        }
    }

    private static void InventoryCreation(
        InitializerExpressionSyntax initializer, InitializerStatistics stats)
    {
        if (initializer.IsKind(SyntaxKind.ObjectInitializerExpression))
            stats.ObjectInitializerSites++;
        else if (initializer.IsKind(SyntaxKind.CollectionInitializerExpression))
            stats.CollectionInitializerSites++;

        var hasIndexAssignment = false;

        foreach (var element in initializer.Expressions)
        {
            switch (element)
            {
                case AssignmentExpressionSyntax assignment
                    when InitializerSyntaxFacts.IsIndexAssignment(assignment):
                    hasIndexAssignment = true;
                    stats.MemberAssignments++;
                    stats.MemberIndexerAssignments++;
                    break;

                case AssignmentExpressionSyntax assignment:
                    stats.MemberAssignments++;
                    if (assignment.Right is InitializerExpressionSyntax memberInitializer
                        && memberInitializer.IsKind(SyntaxKind.ObjectInitializerExpression))
                    {
                        stats.NestedMemberInitSites++;
                    }
                    else if (InitializerSyntaxFacts.IsNestedCreation(assignment.Right))
                    {
                        stats.NestedCreateSites++;
                    }
                    break;

                case ExpressionSyntax expression
                    when InitializerSyntaxFacts.IsComplexElement(expression):
                    stats.AddCalls++;
                    stats.AddMulti++;
                    break;

                case ExpressionSyntax expression:
                    stats.AddCalls++;
                    stats.AddSingle++;
                    if (InitializerSyntaxFacts.IsNestedCreation(expression))
                        stats.NestedCreateSites++;
                    break;
            }
        }

        if (hasIndexAssignment)
            stats.DictionaryIndexInitializerSites++;
    }
}