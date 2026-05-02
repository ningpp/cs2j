using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// Pre-scans the full compilation to resolve types of var-declared locals and
/// foreach iteration variables. Uses the compilation-level semantic model which
/// has full type information (unlike per-file models in the project pipeline).
/// </summary>
public static class VarTypeResolver
{
    public static void PreScan(CSharpCompilation compilation, ConversionContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var sm = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var vd in root.DescendantNodes().OfType<VariableDeclarationSyntax>().Where(v => v.Type.IsVar))
            {
                foreach (var v in vd.Variables)
                {
                    var name = v.Identifier.Text;
                    if (seen.Add(name))
                    {
                        var ti = sm.GetTypeInfo(vd.Type);
                        if (ti.Type is { TypeKind: not (TypeKind.Error or TypeKind.Unknown) })
                            context.VarTypeMap[name] = ti.Type;
                    }
                }
            }

            foreach (var fe in root.DescendantNodes().OfType<ForEachStatementSyntax>().Where(f => f.Type.IsVar))
            {
                var name = fe.Identifier.Text;
                if (seen.Add(name))
                {
                    var ti = sm.GetTypeInfo(fe.Type);
                    if (ti.Type is { TypeKind: not (TypeKind.Error or TypeKind.Unknown) })
                        context.VarTypeMap[name] = ti.Type;
                }
            }
        }
    }
}
