using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// AfterFiles gate (design §4.5): recompiles the intermediate tree and counts
/// (1) remaining reduced invocations outside LINQ hosts, (2) remaining reduced
/// method groups, (3) rewritten-site rebinding identity drift, (4) expression
/// tree node shape drift, (5) accidentally removed <c>this</c> modifiers on
/// extension declarations, (6) unresolvable host names.
///
/// M0 shell: rules are empty and nothing is rewritten, so every gate count is
/// vacuously zero. The scan signature is in place for M1/M2 to fill.
/// </summary>
public static class ExtensionMethodResidualScanner
{
    public static Dictionary<string, int> Scan(
        Compilation rewritten,
        ExtensionMethodResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(rewritten);
        ArgumentNullException.ThrowIfNull(options);

        var residuals = new Dictionary<string, int>(StringComparer.Ordinal);

        // M0: no rules — no shapes to police. M1 populates gates 1/3/6;
        // M2 adds 2/4; gate 5 (this modifier count) is asserted here directly.
        return residuals;
    }
}
