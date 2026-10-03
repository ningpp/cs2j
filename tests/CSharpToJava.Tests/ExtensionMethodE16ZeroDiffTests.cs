using CSharpToJava.Core.Context;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// M4 gate ⑤ / design §7.3 (E16 zero-impact). C19 never rewrites LINQ-owned
/// sites, and mixed user/LINQ chains are conservatively kept fully as sugar,
/// so feeding the converter the C19 output must behave exactly like feeding
/// it the original sugar — in BOTH E16 modes (procedural LinqRewriter and
/// prefer-stream).
///
/// Scope note (M5 gap, recorded in the M4 design log): a user reduced
/// extension node that C19 DOES explicitize (isolated, non-LINQ) is not yet
/// consumed equivalently by the Java converter (reduced → instance-style
/// call vs explicit → static-qualified call). That is converter-side work
/// tracked for M5; it is deliberately not asserted here. What this gate
/// locks is zero regression on every site C19 leaves sugar.
/// </summary>
public class ExtensionMethodE16ZeroDiffTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private const string MixedChainHost = """
        using System.Linq;
        using System.Collections.Generic;
        public static class UserChain
        {
            public static IEnumerable<int> Mark(this IEnumerable<int> source)
            {
                var marked = new List<int>();
                foreach (var item in source) marked.Add(item + 100);
                return marked;
            }
        }
        """;

    public static IEnumerable<object[]> BothModes()
    {
        yield return new object[] { false };
        yield return new object[] { true };
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public void PureLinqSites_C19ChangesNothing_JavaIdentical(bool preferStream)
    {
        // No user extension anywhere: every reduced site is LINQ-owned.
        // Operator shapes exercised by the existing LINQ completion cluster
        // (known-good in both E16 modes).
        var source = """
            using System.Linq;
            using System.Collections.Generic;
            public class C
            {
                public List<int> Sorted(List<int> items) => items.OrderBy(x => x).ToList();

                public List<string> Joined(
                    List<int> ids, List<KeyValuePair<int, string>> names) =>
                    ids.Join(names, id => id, n => n.Key, (id, n) => n.Value).ToList();
            }
            """;

        var c19 = RunC19(source);
        Assert.False(c19.Changed, "pure LINQ sites must stay sugar for E16");
        Assert.Equal(0, c19.Statistics.CallsitesRewritten);

        var baseline = Convert(source, preferStream);
        var afterC19 = Convert(c19.OutputCode, preferStream);

        Assert.True(baseline.Success, "baseline LINQ conversion failed: " + Describe(baseline));
        Assert.True(afterC19.Success, "C19-output LINQ conversion failed: " + Describe(afterC19));
        Assert.Equal(baseline.GeneratedCode, afterC19.GeneratedCode);
    }

    [Theory]
    [MemberData(nameof(BothModes))]
    public void MixedChain_ConservativeSkip_PreservesBaselineConverterBehavior(bool preferStream)
    {
        // The whole mixed chain stays sugar (M4 policy). Whether the current
        // converter handles this shape or reports its pre-existing
        // normalization limitation, C19 must not move the needle: the exact
        // same observable result either way, in both modes.
        var source = MixedChainHost + """


            public class C
            {
                public int M()
                {
                    var a = new[] { 1, 2, 3 };
                    return a.Where(x => x > 1).Mark().Sum();
                }
            }
            """;

        var c19 = RunC19(source);
        Assert.False(c19.Changed);
        Assert.Equal(1, c19.Statistics.MixedLinqChainSites);

        var baseline = Convert(source, preferStream);
        var afterC19 = Convert(c19.OutputCode, preferStream);

        Assert.True(
            baseline.Success == afterC19.Success && Describe(baseline) == Describe(afterC19),
            $"baseline success={baseline.Success} diag=[{Describe(baseline)}] | " +
            $"c19 success={afterC19.Success} diag=[{Describe(afterC19)}]");
        if (baseline.Success)
        {
            Assert.Equal(baseline.GeneratedCode, afterC19.GeneratedCode);
        }
    }

    private static ExtensionFileResult RunC19(string source)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "em-e16-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions());
        return preprocessor.RewriteFile(model);
    }

    private static ConversionResult Convert(string csharp, bool preferStream)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharp,
            FileName = "Test.cs",
            Options = new ConversionOptions { PreferStreamApi = preferStream },
        });
    }

    private static string Describe(ConversionResult result) =>
        string.Join("\n", result.Diagnostics);
}
