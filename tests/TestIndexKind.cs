using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

var tree = CSharpSyntaxTree.ParseText("var x = arr[^1];");
foreach (var node in tree.GetRoot().DescendantNodes())
{
    string kind = node.Kind().ToString();
    if (kind.Contains("Index") || kind.Contains("Prefix"))
        Console.WriteLine($"{node.GetType().Name} : {kind}");
}
