using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Regressions for the four GraphLayout failure classes of the ref/out/in pass
/// (for→while downgrade scope, short-circuit out-arg definite assignment,
/// yield-return ref lifting, foreach holder source reads).
/// </summary>
public class RefOutInGraphLayoutRegressionTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("refout-graphlayout",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    private static string RewriteAndAssertCompiles(string src)
    {
        var byRefSrc = File.ReadAllText(
            RefOutInRewriterUnitTests.FindRepoRoot("src/Cs2jRefOutIn/ByRef.cs"));
        var refStructSrc = File.ReadAllText(
            RefOutInRewriterUnitTests.FindRepoRoot("src/Cs2jRefOutIn/RefStructByRef.cs"));

        var (_, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);

        var compilation = CSharpCompilation.Create("refout-graphlayout-verify",
            new[]
            {
                CSharpSyntaxTree.ParseText(result.OutputCode),
                CSharpSyntaxTree.ParseText(byRefSrc),
                CSharpSyntaxTree.ParseText(refStructSrc),
            },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "output must compile: " + string.Join("; ", errors.Select(d => d.ToString()))
            + "\n===== OUTPUT =====\n" + result.OutputCode);
        return result.OutputCode;
    }

    [Fact]
    public void ShadowedForCondition_KeepsForForm_AndScopesLoopVariable()
    {
        var src = @"
public class C
{
    public int[] Counts = new int[10];

    public bool CalcJumpInfo(int upLayer, int lowLayer, out int jumperLayer, out int layerToJumpTo)
    {
        jumperLayer = 3;
        layerToJumpTo = -1;
        int min = Counts[jumperLayer] - 2;
        for (int i = upLayer - 1; i > jumperLayer; i--)
            if (Counts[i] < min)
            {
                min = Counts[i];
                layerToJumpTo = i;
            }

        for (int i = jumperLayer - 1; i > lowLayer; i--)
            if (Counts[i] < min)
            {
                min = Counts[i];
                layerToJumpTo = i;
            }
        return layerToJumpTo != -1;
    }
}";

        var output = RewriteAndAssertCompiles(src);

        // A pure holder-slot substitution in the condition must NOT degrade the
        // loop into `int i = ...; while (true)` (that leaks `i` into the method
        // block scope and collides with the sibling loop → CS0136).
        Assert.Contains("for (int i = upLayer - 1; i >", output);
        Assert.DoesNotContain("int i = upLayer - 1;\n", output);
    }

    [Fact]
    public void ShortCircuitOutArgument_UninitializedLocal_GetsDefaultInitializer()
    {
        var src = @"
public class C
{
    public sealed class V { public bool Has; }
    public sealed class E { }

    public bool TryGetEdge(V other, out E edge)
    {
        edge = new E();
        return other.Has;
    }

    public E FindEdge(V source, V target)
    {
        E edge;
        if (source != null && TryGetEdge(target, out edge))
            return edge;
        return null;
    }
}";

        var output = RewriteAndAssertCompiles(src);

        Assert.Contains("E edge = default", output);
    }

    [Fact]
    public void YieldReturn_RefArgumentCall_IsLifted()
    {
        var src = @"
using System.Collections.Generic;
public class C
{
    private string SegmentString(string segment, ref char previousInstruction)
    {
        previousInstruction = 'L';
        return segment;
    }

    public IEnumerable<string> Tokens()
    {
        return Collect();

        IEnumerable<string> Collect()
        {
            char previousInstruction = 'w';
            yield return SegmentString(""a"", ref previousInstruction);
            yield return SegmentString(""b"", ref previousInstruction);
        }
    }
}";

        var output = RewriteAndAssertCompiles(src);

        // The callee now takes ByRef<char>; the call site must be lifted, so
        // no native `ref` argument may survive.
        Assert.DoesNotContain("ref previousInstruction", output);
        Assert.Contains("yield return", output);
    }

    [Fact]
    public void NativeOutCallInLocalDeclaration_CopiesHolderThroughTemp()
    {
        var src = @"
public class C
{
    public bool GetDoubleOptionValue(string s, out double v)
    {
        string svalue = s;
        if (svalue == null)
        {
            v = 0;
            return false;
        }
        bool ret = double.TryParse(svalue, out v);
        return ret;
    }
}";

        var output = RewriteAndAssertCompiles(src);

        // BCL `double.TryParse` keeps its native `out`; a holder's Value is a
        // property, so the call must copy through a temp (CS0206 otherwise).
        Assert.DoesNotContain("out v.Value", output);
        Assert.Contains("out __out", output);
    }

    [Fact]
    public void DiscardOutArgument_InsideCall_IsLiftedToSlot()
    {
        var src = @"
public class C
{
    public bool TryGet(out int v) { v = 0; return false; }

    public bool M()
    {
        return Check(TryGet(out int _));
    }

    private static bool Check(bool b) { return b; }
}";

        var output = RewriteAndAssertCompiles(src);

        // `out int _` is a DiscardDesignation, not a named declaration; the
        // callee now takes ByRef<int>, so the native keyword must be dropped.
        Assert.DoesNotContain("out int _", output);
        Assert.Contains("ByRef<int> __out", output);
    }

    [Fact]
    public void ForeachOverOutParameter_ReadsHolderValue()
    {
        var src = @"
using System.Collections.Generic;
public class C
{
    public void Unite(out List<string> list)
    {
        list = new List<string>();
    }

    public int Count(out List<string> list)
    {
        Unite(out list);
        int n = 0;
        foreach (var s in list)
            n += s.Length;
        return n;
    }
}";

        var output = RewriteAndAssertCompiles(src);

        Assert.Contains("foreach (var s in list.Value)", output);
    }
}
