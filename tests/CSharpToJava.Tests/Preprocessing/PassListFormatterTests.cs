using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Tests.Preprocessing;

public sealed class PassListFormatterTests
{
    [Fact]
    public void Lists_every_registered_pass_in_locked_order_with_status_and_default()
    {
        var registry = PassCatalog.BuildRegistry();

        var lines = PassListFormatter.BuildLines(registry);

        // Two header lines, one row per descriptor.
        Assert.Equal(2 + registry.Ordered.Count, lines.Count);

        foreach (var pass in registry.Ordered)
        {
            var row = Assert.Single(lines.Skip(2), line => line.StartsWith(pass.Id + " "));
            Assert.Contains(pass.Stage.ToString(), row);
            Assert.Contains(pass.Status.ToString(), row);
            Assert.Contains(pass.DefaultEnabled ? " on " : " off ", row);
        }
    }

    [Fact]
    public void First_pass_row_reports_no_run_after_anchor()
    {
        var lines = PassListFormatter.BuildLines(PassCatalog.BuildRegistry());

        var firstRow = lines[2];
        Assert.StartsWith(PassIds.Conditional, firstRow);
        Assert.EndsWith("-", firstRow.TrimEnd());
    }
}
