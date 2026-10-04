using System;
using SampleNullConditionalLib;

internal static class Program
{
    private static void Main()
    {
        var node = new TreeNode();
        var leaf = new Leaf { Value = 42 };
        node.Leaf = leaf;

        var simple = new Simple { B = 5 };
        Console.WriteLine(NullConditionalSites.MemberFallback(simple));
        Console.WriteLine(NullConditionalSites.MemberFallback(null));
        Console.WriteLine(NullConditionalSites.ArrayIndex(new[] { 10, 20 }, 1));
        Console.WriteLine(NullConditionalSites.MultiValue(node));
        NullConditionalSites.MultiVoid(node, 1);
        Console.WriteLine(node.Leaf!.FireCalls);
    }
}
