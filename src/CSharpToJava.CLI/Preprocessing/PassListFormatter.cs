using System.Text;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Renders the registered pass catalog for the <c>--list-passes</c> surface.
/// Rows follow the locked topological order; the format is line-oriented and
/// stable (used by CLI tests).
/// </summary>
internal static class PassListFormatter
{
    public static IReadOnlyList<string> BuildLines(PassRegistry registry)
    {
        var lines = new List<string>
        {
            "# cs2j preprocess passes (rows are in locked execution order)",
            "# id                        stage        status        default  run-after",
        };

        foreach (var pass in registry.Ordered)
        {
            var runAfter = pass.RunAfterIds.Count == 0
                ? "-"
                : string.Join(",", pass.RunAfterIds);

            var line = new StringBuilder()
                .Append(pass.Id.PadRight(28))
                .Append(pass.Stage.ToString().PadRight(13))
                .Append(pass.Status.ToString().PadRight(14))
                .Append((pass.DefaultEnabled ? "on" : "off").PadRight(9))
                .Append(runAfter);

            // Co-located catalog rules (e.g. numeric-boundary hosts C15 and C34)
            // are surfaced so --list-passes stays the single inventory surface.
            if (!string.IsNullOrEmpty(pass.CatalogItems))
            {
                line.Append("  rules=").Append(pass.CatalogItems);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    public static void Print(PassRegistry registry)
    {
        foreach (var line in BuildLines(registry))
        {
            Console.WriteLine(line);
        }
    }
}
