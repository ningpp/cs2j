using System.Diagnostics;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for a 2^d walk in RefOutInSyntaxRewriter: VisitIfStatement /
/// VisitSwitchStatement pre-visited every child and then re-dispatched the
/// base visit on the original node, walking the whole subtree a second time.
/// A pattern-desugar-produced else-if chain of ~40 levels made the pass spin
/// a full core for tens of minutes (the end-to-end convert never reached the
/// late ref-out-in pass before earlier gates were fixed).
/// </summary>
public class RefOutInNestedIfChainTests
{
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public sealed class ByRef<T>
            {
                public T Value = default!;
                public ByRef() { }
                public ByRef(T value) { Value = value; }
                public static ByRef<T> Create() => new ByRef<T>();
                public static ByRef<T> Create(T value) => new ByRef<T>(value);
                public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter)
                    => new ByRef<T>(getter());
            }
        }
        """;

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

    private static RefOutInRewriterResult Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "roi-nested-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-nested-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Deep_Else_If_Chain_With_Holder_Conditions_Rewrites_In_Linear_Time()
    {
        // ~40 nested else-if levels is exactly the shape pattern-desugar emits
        // and the shape that hung the convert pipeline (2^40+ revisits).
        const int depth = 40;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("class C");
        sb.AppendLine("{");
        sb.AppendLine("    static void M(ref int h)");
        sb.AppendLine("    {");
        // Each level is a self-contained `if { } else` dangling into the next
        // if; the terminal else block closes the chain (no wrap braces).
        for (var i = 0; i < depth; i++)
        {
            var indent = new string(' ', 8 + i * 4);
            sb.Append(indent).AppendLine($"if (h > {i})");
            sb.Append(indent).AppendLine("{");
            sb.Append(indent).AppendLine("    System.Console.WriteLine(h);");
            sb.Append(indent).AppendLine("}");
            sb.Append(indent).AppendLine("else");
        }

        var elseIndent = new string(' ', 8 + depth * 4);
        sb.Append(elseIndent).AppendLine("{");
        sb.Append(elseIndent).AppendLine("    System.Console.WriteLine(h);");
        sb.Append(elseIndent).AppendLine("}");

        sb.AppendLine("    }");
        sb.AppendLine("}");
        var src = sb.ToString();

        var stopwatch = Stopwatch.StartNew();
        var result = Run(src);
        stopwatch.Stop();

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The holder read in every condition and every body is injected once.
        Assert.Equal(depth + 1, CountOccurrences(result.OutputCode, "System.Console.WriteLine(h.Value)"));
        Assert.Equal(depth, CountOccurrences(result.OutputCode, "if (h.Value >"));
        Assert.DoesNotContain(".Value.Value", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        // Linear traversal of a 40-level chain finishes in well under a second;
        // the old exponential walk never finished (minutes of 100% CPU). Keep
        // the bound generous so the assertion is not flaky on loaded CI agents.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"rewrite took {stopwatch.Elapsed}; expected sub-second linear time");
    }

    [Fact]
    public void Switch_On_Holder_Governing_Expression_Is_Injected_Once_And_Compiles()
    {
        var src = """
            class C
            {
                static int M(ref int h)
                {
                    switch (h)
                    {
                        case 1: return h;
                        default: return 0;
                    }
                }
            }
            """;

        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("switch (h.Value)", result.OutputCode);
        Assert.Contains("return h.Value;", result.OutputCode);
        Assert.DoesNotContain(".Value.Value", result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Large_Plain_Expression_With_Many_Out_Calls_Splices_In_Linear_Time()
    {
        // One statement: a long `+` chain whose every operand is a liftable
        // internal out-call. The old SpliceChildren replaced one call and
        // re-scanned the whole expression with fresh semantic bindings per
        // iteration (O(L*n^2)); the generated serializer methods contain
        // exactly this shape and stalled the pass for many minutes.
        const int calls = 150;
        var declarations = string.Join(", ",
            System.Linq.Enumerable.Range(0, calls).Select(i => $"a{i}"));
        var sum = string.Join(" + ",
            System.Linq.Enumerable.Range(0, calls).Select(i => $"Fill({i}, out a{i})"));

        var src = $$"""
            class C
            {
                static int Fill(int i, out int v) { v = i; return i; }
                static void M()
                {
                    int {{declarations}};
                    int x = {{sum}};
                    System.Console.WriteLine(x);
                }
            }
            """;

        var stopwatch = Stopwatch.StartNew();
        var result = Run(src);
        stopwatch.Stop();

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"splice took {stopwatch.Elapsed}; expected sub-second linear time");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
