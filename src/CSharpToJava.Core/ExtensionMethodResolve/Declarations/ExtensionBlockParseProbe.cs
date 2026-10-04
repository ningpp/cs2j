using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// CS2J1213 startup parse self-check: parses a minimal C#14 extension-block
/// fragment with the bound Roslyn and requires zero parse errors plus one
/// recognized block node. Runs once before block analysis; a failure means the
/// pass cannot run, so every block is left unchanged. The seams accept an
/// explicit fragment / language version so both the passing and failing paths
/// are unit-testable (Roslyn 5.0.0 GA must pass the default probe).
/// </summary>
internal static class ExtensionBlockParseProbe
{
    /// <summary>Minimal legal M1 fragment: get-only extension property in a block.</summary>
    public const string MinimalFragment =
        "static class E { extension(int x) { public int P => x; } }";

    /// <summary>Run the default probe under <see cref="LanguageVersion.Latest"/>.</summary>
    public static bool Passes()
        => Passes(MinimalFragment, LanguageVersion.Latest);

    /// <summary>
    /// Parse <paramref name="fragment"/> under <paramref name="version"/> and
    /// require: zero parse diagnostics, exactly one block node detected by
    /// the dual <see cref="ExtensionBlockSyntaxFacts"/> guard.
    /// </summary>
    public static bool Passes(string fragment, LanguageVersion version)
    {
        var parseOptions = new CSharpParseOptions(version);
        var tree = CSharpSyntaxTree.ParseText(fragment, parseOptions);

        var root = tree.GetCompilationUnitRoot();
        if (tree.GetDiagnostics().Any())
        {
            return false;
        }

        var blocks = ExtensionBlockSyntaxFacts.EnumerateBlocks(root).ToList();
        return blocks.Count == 1;
    }
}
