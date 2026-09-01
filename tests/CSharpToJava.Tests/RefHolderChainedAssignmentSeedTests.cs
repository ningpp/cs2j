using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ref holder is seeded with the variable's current value
/// when the variable is assigned via a chained assignment before the
/// ref call — e.g. page = pageEnd = _pageCurrent; Method(ref pageEnd)
/// must produce new ObjectHolder<>(pageEnd), not new ObjectHolder<>().
/// </summary>
public class RefHolderChainedAssignmentSeedTests
{
    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void ChainedAssignmentBeforeRefCall_HolderShouldBeSeeded()
    {
        var result = Convert(@"
using System;

class Node { }
class Helper
{
    public static bool GetNonDescendant(ref Node[] page, ref int idx)
    {
        page = null;
        idx = 0;
        return false;
    }
}
class Sample
{
    private Node[] _pageCurrent;
    private int _idxCurrent;

    public string GetValue()
    {
        Node[] page, pageEnd;
        int idx, idxEnd;

        page = pageEnd = _pageCurrent;
        idx = idxEnd = _idxCurrent;
        if (!Helper.GetNonDescendant(ref pageEnd, ref idxEnd))
        {
            pageEnd = null;
            idxEnd = 0;
        }
        return """";
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The ObjectHolder for pageEnd should be seeded with pageEnd's current value,
        // not created empty.
        Assert.Contains("new io.github.ningpp.compat.ObjectHolder<>(pageEnd)", code, StringComparison.Ordinal);
        // The IntHolder for idxEnd should be seeded with idxEnd's current value,
        // not created empty.
        Assert.Contains("new io.github.ningpp.compat.IntHolder(idxEnd)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainedAssignmentBeforeRefCall_WithIntermediateIfReturns_HolderShouldBeSeeded()
    {
        // This mirrors the XPathDocumentNavigator.Value pattern where
        // intermediate if-statements with early returns appear between declaration
        // and chained assignment. The data-flow analysis should still detect
        // the chained assignment as "always assigned" before the ref call.
        var result = Convert(@"
using System;
using System.Text;

class Node { }
class Helper
{
    public static bool GetNonDescendant(ref Node[] page, ref int idx)
    {
        page = null;
        idx = 0;
        return false;
    }
    public static bool GetTextFollowing(ref Node[] page, ref int idx, Node[] pageEnd, int idxEnd)
    {
        return false;
    }
}
class Sample
{
    private Node[] _pageCurrent;
    private int _idxCurrent;
    private Node[] _pageParent;
    private int _idxParent;

    public string Value
    {
        get
        {
            string value;
            Node[] page, pageEnd;
            int idx, idxEnd;

            value = _pageCurrent[_idxCurrent].ToString();
            if (value != null)
                return value;

            if (_idxParent != 0)
            {
                return _pageParent[_idxParent].ToString();
            }

            string s = string.Empty;
            StringBuilder bldr = null;

            page = pageEnd = _pageCurrent;
            idx = idxEnd = _idxCurrent;
            if (!Helper.GetNonDescendant(ref pageEnd, ref idxEnd))
            {
                pageEnd = null;
                idxEnd = 0;
            }

            while (Helper.GetTextFollowing(ref page, ref idx, pageEnd, idxEnd))
            {
                if (s.Length == 0)
                {
                    s = page[idx].ToString();
                }
                else
                {
                    if (bldr == null)
                        bldr = new StringBuilder();
                    bldr.Append(page[idx].ToString());
                }
            }

            return bldr != null ? bldr.ToString() : s;
        }
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Debug: output generated code
        // System.Console.WriteLine("=== GENERATED CODE ===");
        // System.Console.WriteLine(code);
        // System.Console.WriteLine("=== END GENERATED CODE ===");

        // The ObjectHolder for pageEnd should be seeded with pageEnd's current value,
        // not created empty. This is the bug: ShouldSeedRefHolderFromCurrentValue
        // returns false because the data-flow analysis across the intermediate
        // if-statements doesn't detect the chained assignment as "always assigned".
        Assert.Contains("new io.github.ningpp.compat.ObjectHolder<>(pageEnd)", code, StringComparison.Ordinal);
        // The IntHolder for idxEnd should be seeded with idxEnd's current value,
        // not created empty.
        Assert.Contains("new io.github.ningpp.compat.IntHolder(idxEnd)", code, StringComparison.Ordinal);
    }
}
