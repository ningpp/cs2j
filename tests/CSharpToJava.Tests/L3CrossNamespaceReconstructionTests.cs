using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0246 in the generated regular-expressions tree: the L3
/// DataContainer write reconstruction <c>b.Prop = v → b = new S(p1: …, p: v)</c>
/// emitted the bare struct name. Generated tuple shapes live in
/// <c>global::Cs2jTuples</c> while the write sites live in other namespaces, so
/// the bare name did not bind. Cross-container sites must emit the fully
/// qualified <c>global::</c> name; same-container sites keep the simple name.
/// </summary>
public class L3CrossNamespaceReconstructionTests
{
    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static string RunRewriter(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "l3-qualify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);

        var structFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Box"] = new() { "Item1", "Item2" },
        };
        var l3Structs = new Dictionary<string, (HashSet<string> Props, List<string> CtorParams)>(
            StringComparer.Ordinal)
        {
            ["Box"] = (new HashSet<string> { "Item1", "Item2" },
                new List<string> { "item1", "item2" }),
        };
        // Cross-file accumulation: every declaration named Box is registered
        // with its namespace/type chain, exactly like the pass engine does.
        var qualifiedNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Box"] = new(StringComparer.Ordinal) { "Tuples.Box", "App.Box" },
        };

        var rewriter = new AssignmentRewriter(
            structFields,
            model,
            l3Structs: l3Structs,
            qualifiedNames: qualifiedNames);
        rewriter.BuildVariableTypeMap(tree.GetRoot());
        var root = (CompilationUnitSyntax)rewriter.Visit(tree.GetRoot())!;
        return root.NormalizeWhitespace().ToFullString();
    }

    private static string? EmitAndGo(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "l3-qualify-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var runner = asm.GetType("App.Runner")!;
        return (string?)runner.GetMethod("Go", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
    }

    private const string CrossNamespaceSource = """
        namespace Tuples
        {
            internal readonly struct Box
            {
                public int Item1 { get; }
                public string Item2 { get; }
                public Box(int item1, string item2) { Item1 = item1; Item2 = item2; }
            }
        }

        namespace App
        {
            public static class Runner
            {
                public static string Go()
                {
                    var b = new global::Tuples.Box(1, "a");
                    b.Item2 = "x";
                    return b.Item1 + "," + b.Item2;
                }
            }
        }
        """;

    private const string SameNamespaceSource = """
        namespace App
        {
            internal readonly struct Box
            {
                public int Item1 { get; }
                public string Item2 { get; }
                public Box(int item1, string item2) { Item1 = item1; Item2 = item2; }
            }

            public static class Runner
            {
                public static string Go()
                {
                    var b = new Box(1, "a");
                    b.Item2 = "x";
                    return b.Item1 + "," + b.Item2;
                }
            }
        }
        """;

    [Fact]
    public void Cross_Namespace_Write_Is_Reconstructed_With_Global_Qualified_Type()
    {
        var output = RunRewriter(CrossNamespaceSource);

        Assert.Contains(
            "b = new global::Tuples.Box(item1: b.Item1, item2: \"x\")",
            output);
        Assert.DoesNotContain("= new Box(", output);
        Assert.Equal("1,x", EmitAndGo(output));
    }

    [Fact]
    public void Same_Namespace_Write_Keeps_The_Simple_Type_Name()
    {
        var output = RunRewriter(SameNamespaceSource);

        Assert.Contains("b = new Box(item1: b.Item1, item2: \"x\")", output);
        Assert.DoesNotContain("global::App.Box", output);
        Assert.Equal("1,x", EmitAndGo(output));
    }
}
