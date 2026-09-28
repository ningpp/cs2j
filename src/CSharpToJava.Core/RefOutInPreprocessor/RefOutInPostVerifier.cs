using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Post-rewrite semantic verification (§7). Walks the output syntax tree
/// with a fresh SemanticModel and asserts that no internally-defined
/// ref/out/in remains. Returns categorized counts.
/// </summary>
public static class RefOutInPostVerifier
{
    public static PostVerificationResult Verify(
        string outputCode,
        MetadataReference[]? references = null)
    {
        references ??= new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var tree = CSharpSyntaxTree.ParseText(outputCode);
        var compilation = CSharpCompilation.Create("refout-verify",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var result = new PostVerificationResult();
        var hasUsing = root.Usings.Any(u => u.Name?.ToString() == "Cs2jByRef");
        result.HasCs2jByRefUsing = hasUsing;

        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(method);
            if (symbol == null) continue;

            foreach (var param in symbol.Parameters)
            {
                if (param.RefKind == RefKind.Ref || param.RefKind == RefKind.Out)
                {
                    var isInternal = param.Locations.Any(l => l.IsInSource);
                    if (isInternal)
                        result.InternalRefOutRemaining++;
                    else
                        result.ExternalRefOutKept++;
                }
            }
        }

        return result;
    }
}

public sealed record PostVerificationResult
{
    public int InternalRefOutRemaining;
    public int ExternalRefOutKept;
    public bool HasCs2jByRefUsing;
    public bool IsClean => InternalRefOutRemaining == 0;
}
