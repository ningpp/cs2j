using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>Syntax dirty check (deliberate superset) and the residual gate.</summary>
internal static class PatternDesugarAnalyzer
{
    /// <summary>
    /// Superset dirty check: any switch, any catch clause with a declaration
    /// (type-parameter catches need semantics), any catch filter, any pattern
    /// label. Files without matches are byte-level copies.
    /// </summary>
    public static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            n is SwitchStatementSyntax
            || n is CasePatternSwitchLabelSyntax
            || (n is CatchClauseSyntax catchClause
                && (catchClause.Filter != null || catchClause.Declaration != null)));

    /// <summary>Semantic residual gate: after rewriting, no target nodes may remain.</summary>
    public static int CountResidualTargets(string code, SemanticModel originalModel)
    {
        var newTree = CSharpSyntaxTree.ParseText(code, path: "pattern-desugar-output.cs");
        var newCompilation = originalModel.Compilation.ReplaceSyntaxTree(
            originalModel.SyntaxTree, newTree);
        var newModel = newCompilation.GetSemanticModel(newTree);
        var newRoot = (CompilationUnitSyntax)newTree.GetRoot();

        var count = 0;
        foreach (var node in newRoot.DescendantNodes())
        {
            if (node is CasePatternSwitchLabelSyntax)
            {
                count++;
            }
            else if (node is CatchClauseSyntax { Filter: not null })
            {
                count++;
            }
            else if (node is CatchDeclarationSyntax declaration
                     && newModel.GetTypeInfo(declaration.Type).Type is ITypeParameterSymbol)
            {
                count++;
            }
            else if (node is SwitchStatementSyntax switchStatement
                     && PatternDesugarAnalysis.IsNonIntegerGoverning(
                         newModel.GetTypeInfo(switchStatement.Expression).Type))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Syntax-only residual for the no-semantic-model (single file) path.</summary>
    public static int CountSyntaxResidualTargets(string code)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Count(n =>
            n is CasePatternSwitchLabelSyntax
            || (n is CatchClauseSyntax { Filter: not null }));
    }
}
