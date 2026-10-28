using CSharpToJava.Core.LinqChannel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// Regression (GraphLayout CS1073): stage-1 query desugaring must not splice a
/// fluent member access (".Where"/".Select"/…) directly onto a non-primary
/// source expression. <c>from x in items as int[]</c> desugared to
/// <c>items as int[].Where(x => …)</c> — a syntax error, because the <c>as</c>
/// operator binds looser than member access. Every such source must be wrapped
/// in a parenthesized expression (<c>(items as int[]).Where(…)</c>), while
/// ordinary primary sources keep the bare receiver
/// (<c>values.Where(…)</c>, no redundant parentheses).
/// </summary>
public sealed class LinqChannelQuerySourceParenthesizationTests
{
    [Fact]
    public void QuerySource_AsExpression_IsParenthesizedBeforeFluentChain()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class Host
            {
                public static IEnumerable<int> FromCast(object items)
                    => from x in items as int[]
                       where x > 0
                       select x;
            }
            """;

        var (raw, normalized) = Desugar(source);

        Assert.Contains("(items as int[]).Where", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain(" as int[].Where", normalized, StringComparison.Ordinal);
        AssertCompiles(raw);
    }

    [Fact]
    public void QuerySource_CoalesceExpression_IsParenthesizedBeforeFluentChain()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class Host
            {
                public static IEnumerable<int> FromCoalesce(int[] first, int[] second)
                    => from x in first ?? second
                       where x > 0
                       select x;
            }
            """;

        var (raw, normalized) = Desugar(source);

        Assert.Contains("(first ?? second).Where", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain(" second.Where", normalized, StringComparison.Ordinal);
        AssertCompiles(raw);
    }

    [Fact]
    public void QuerySource_CastExpression_IsParenthesizedBeforeFluentChain()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class Host
            {
                public static IEnumerable<int> FromCast(object items)
                    => from x in (IEnumerable<int>)items
                       where x > 0
                       select x;
            }
            """;

        var (raw, normalized) = Desugar(source);

        Assert.Contains("((IEnumerable<int>)items).Where", normalized, StringComparison.Ordinal);
        AssertCompiles(raw);
    }

    [Fact]
    public void QuerySource_PrimaryExpression_KeepsBareReceiver()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class Host
            {
                public static List<int> Values = new List<int>();

                public static IEnumerable<int> FromParameter(int[] values)
                    => from x in values
                       where x > 0
                       select x + 1;

                public static IEnumerable<int> FromMemberAccess()
                    => from x in Values
                       where x > 0
                       select x + 1;
            }
            """;

        var (raw, normalized) = Desugar(source);

        Assert.Contains("values.Where", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("(values).Where", normalized, StringComparison.Ordinal);
        Assert.Contains("Values.Where", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("(Values).Where", normalized, StringComparison.Ordinal);
        AssertCompiles(raw);
    }

    [Fact]
    public void QuerySource_AsExpression_NestedFromSelectMany_IsParenthesized()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class Host
            {
                public static IEnumerable<int> FromNested(object items, int[] more)
                    => from x in items as int[]
                       from y in more
                       where x > 0 && y > 0
                       select x + y;
            }
            """;

        var (raw, normalized) = Desugar(source);

        Assert.Contains("(items as int[]).SelectMany", normalized, StringComparison.Ordinal);
        AssertCompiles(raw);
    }

    private static (string Raw, string Normalized) Desugar(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        Assert.DoesNotContain(tree.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
        var (root, _) = LinqChannelRewriter.DesugarQueries(tree.GetRoot());
        return (root.ToFullString(), root.NormalizeWhitespace().ToFullString());
    }

    private static void AssertCompiles(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(
            text, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "linq-channel-query-source-parens",
            new[] { tree },
            LinqChannelDualPathHarness.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0, string.Join("; ", errors));
        Assert.DoesNotContain(errors, d => d.Id is "CS1073" or "CS1003");
    }
}
