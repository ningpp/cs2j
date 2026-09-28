using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Post-rewrite residual gate (C10, D3 §7): asserts the rewritten output has
/// zero INTERNAL ref/out declarations, ref returns, ref locals/fields or ref
/// expressions outside external/locked call arguments. Violations carry
/// CS2J3305 and fail the pass.
/// </summary>
public static class RefOutInPostVerifier
{
    public static IReadOnlyList<ResidualViolation> Verify(
        IEnumerable<SyntaxTree> outputTrees, CSharpCompilation compilation)
    {
        var violations = new List<ResidualViolation>();
        foreach (var tree in outputTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();

            foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(method);
                if (symbol == null) continue;
                if (RefOutInAliasAnnotator.IsLocked(symbol)) continue;

                foreach (var param in symbol.Parameters)
                {
                    if (param.RefKind is RefKind.Ref or RefKind.Out
                        && param.Locations.Any(l => l.IsInSource))
                        violations.Add(new ResidualViolation(tree.FilePath,
                            $"internal '{symbol.ToDisplayString()}' still declares ref/out parameter '{param.Name}'"));
                }
                if (symbol.RefKind is RefKind.Ref or RefKind.RefReadOnly)
                    violations.Add(new ResidualViolation(tree.FilePath,
                        $"'{symbol.ToDisplayString()}' still declares a ref return"));
            }

            foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                         .Where(l => l.Declaration.Type is RefTypeSyntax))
                violations.Add(new ResidualViolation(tree.FilePath,
                    "ref local survived rewriting"));

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                         .Where(f => f.Declaration.Type is RefTypeSyntax))
                violations.Add(new ResidualViolation(tree.FilePath,
                    "ref field survived rewriting"));

            foreach (var refExpr in root.DescendantNodes().OfType<RefExpressionSyntax>())
            {
                if (refExpr.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any()) continue;
                var arg = refExpr.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
                if (arg != null)
                {
                    var inv = arg.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    var target = inv != null
                        ? model.GetSymbolInfo(inv).Symbol as IMethodSymbol
                        : null;
                    if (target != null
                        && (target.Locations.All(l => !l.IsInSource)
                            || RefOutInAliasAnnotator.IsLocked(target)))
                        continue;   // external/locked residue is legitimate (CD1/CD3)
                }
                violations.Add(new ResidualViolation(tree.FilePath,
                    "ref expression outside external/locked arguments survived"));
            }
        }
        return violations;
    }
}

public sealed record ResidualViolation(string FilePath, string Message);
