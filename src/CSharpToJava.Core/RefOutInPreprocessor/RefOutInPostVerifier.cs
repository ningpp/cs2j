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
                // Deconstruction-protocol Deconstruct keeps native out params
                // (the implicit tuple-deconstruction binding has no invocation
                // node to lift), so they are not residual debt.
                if (symbol is IMethodSymbol deconstruct
                    && RefOutInAliasAnnotator.IsDeconstructionProtocol(deconstruct)) continue;

                foreach (var param in symbol.Parameters)
                {
                    // Pointer/function-pointer types have no ByRef<T> form
                    // (CS0306): keeping their native ref/out syntax is the
                    // intended rewrite result, not residual debt.
                    if (param.RefKind is RefKind.Ref or RefKind.Out
                        && !RefOutInAliasAnnotator.IsPointerType(param.Type)
                        && !IsByRefHolderParameter(param)
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

    /// <summary>
    /// A ref-struct holder parameter is <c>ref Cs2jByRef.RefStructByRef&lt;T&gt;</c>
    /// BY DESIGN: <c>RefStructByRef&lt;T&gt;</c> is itself a ref struct, so a
    /// by-value parameter would copy its slot and swallow every write the callee
    /// performs on the reader. The <c>ref</c> here is the rewrite RESULT, not a
    /// leftover native ref/out declaration.
    /// </summary>
    private static bool IsByRefHolderParameter(IParameterSymbol parameter) =>
        parameter.RefKind == RefKind.Ref
        && parameter.Type is INamedTypeSymbol holder
        // The residual-gate compilation may not reference the Cs2jByRef support
        // library, in which case this type binds to an error type (no
        // IsRefLikeType / namespace): match on the name alone — the original
        // sources can never declare a `ref RefStructByRef<T>` parameter, the
        // type only exists after the rewrite.
        && holder.Name == "RefStructByRef";
}

public sealed record ResidualViolation(string FilePath, string Message);
