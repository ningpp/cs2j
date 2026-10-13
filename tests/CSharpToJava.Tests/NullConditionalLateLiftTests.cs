using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the C35 post-lift late rewrite (CS2J0566 root fix):
/// a null-conditional chain carried INSIDE C24's pending statement sequences
/// (object/collection initializer member values, argument-position hoists and
/// field builder methods) is invisible to the primary C35 rewrite because the
/// sequences are spliced into the tree only by the family TempLifter. The late
/// rewrite lowers those chains against their CURRENT hosting statements; the
/// emitted C# must compile cleanly and no CS2J0566 residual may remain.
/// </summary>
public class NullConditionalLateLiftTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static ExpressionDesugarResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c35-late-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        return new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                Initializers = new InitializerOptions(),
                NullConditional = new NullConditionalOptions(),
            });
    }

    private static void AssertStrictSuccess(ExpressionDesugarResult result)
    {
        var errors = string.Join("\n",
            result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(result.Changed, "file should be rewritten. Diagnostics:\n" + errors);
        Assert.Equal("", errors);
        Assert.DoesNotContain("CS2J0566", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(0, result.Statistics.NullConditional.ResidualNodeCount);
        Assert.DoesNotContain("?.", result.OutputCode, StringComparison.Ordinal);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static string CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c35-late-out-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return string.Join("\n", compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void InlineChain_InInitializerMemberTernaryCondition_LoweredLate()
    {
        // Mirrors the RegexNode.cs corpus site: the creation (with its member
        // assignment) rides into a C24 pending statement; the whitelist
        // receiver lowers inline there, with no extra declaration.
        const string source = """
            using System.Collections.Generic;
            public class Node
            {
                public Node(int kind, int options) { }
                public string? Str { get; set; }
            }
            public class C
            {
                private void M(string? s, int len, List<Node> nodes)
                {
                    nodes.Add(new Node(1, 0)
                    {
                        Str = s?.Length == len ? s : "fallback"
                    });
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        Assert.Contains("(s != null ? (int?)s.Length : (int?)null) == len", code);
        Assert.DoesNotContain("__ed_s", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TempTernaryChain_InPendingMemberAssignment_DeclaresBeforeUse()
    {
        // Non-whitelist receiver (an invocation) forces the segment-temp
        // shape; the declaration must be inserted BEFORE the synthesized
        // member-assignment statement that references it.
        const string source = """
            using System.Collections.Generic;
            public class Node
            {
                public string? Str { get; set; }
            }
            public class C
            {
                private static string? GetS() => null;
                private void M(List<Node> nodes)
                {
                    nodes.Add(new Node
                    {
                        Str = GetS()?.Length == 0 ? "zero" : "other"
                    });
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        var declare = code.IndexOf(
            "var __ed_s0 = GetS();", StringComparison.Ordinal);
        var use = code.IndexOf("__ed_s0 != null", StringComparison.Ordinal);
        Assert.True(declare >= 0, "expected segment temp declaration. Output:\n" + code);
        Assert.True(use >= 0, "expected segment temp use. Output:\n" + code);
        Assert.True(declare < use);
    }

    [Fact]
    public void ChainInCollectionAddPending_LoweredLate()
    {
        const string source = """
            using System.Collections.Generic;
            public class C
            {
                private static string? GetS() => null;
                private void M()
                {
                    var list = new List<string?> { GetS()?.Trim() };
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        Assert.Contains("var __ed_s0 = GetS();", code);
        Assert.Contains("__ed_s0 != null ? __ed_s0.Trim() : null", code);
    }

    [Fact]
    public void GenuinelyAnchorFreeChain_NoBuilderMaterializes_StillReportsCS2J0557()
    {
        // No initializer site here, so no statement context ever materializes:
        // the deferred chain must still be rejected with CS2J0557 (strict) and
        // the file stays atomically unchanged.
        const string source = """
            public class C
            {
                private static string? GetS() => null;
                private static readonly int? L = GetS()?.Length;
            }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(
            result.Diagnostics,
            d => d.Severity == ExpressionDesugarSeverity.Error
                 && d.Code == "CS2J0557");
    }

    [Fact]
    public void ChainInInitializerInsideExpressionLambda_LoweredAfterLambdaExpansion()
    {
        // e5 x e6 interaction: the lambda is expanded to a block (C24 pending
        // anchored on it), and only then does the C35 late pass find the chain
        // inside the inserted member-assignment statement.
        const string source = """
            using System;
            using System.Collections.Generic;
            public class Node
            {
                public string? Str { get; set; }
            }
            public class C
            {
                private static string? GetS() => null;
                private static void Consume(Node n) { }
                private static void M()
                {
                    Action a = () => Consume(new Node { Str = GetS()?.Trim() });
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        var declareReceiver = code.IndexOf(
            "var __ed_s0 = GetS();", StringComparison.Ordinal);
        var declareObject = code.IndexOf(
            "var __ed_0 = new Node();", StringComparison.Ordinal);
        var assign = code.IndexOf(
            "__ed_0.Str = (__ed_s0 != null ? __ed_s0.Trim() : null)",
            StringComparison.Ordinal);
        var call = code.IndexOf("Consume(__ed_0);", StringComparison.Ordinal);
        Assert.True(
            declareReceiver >= 0 && declareObject >= 0
            && assign >= 0 && call >= 0,
            "unexpected output:\n" + code);
        Assert.True(declareReceiver < assign);
        Assert.True(declareObject < assign);
        Assert.True(assign < call);
    }

    [Fact]
    public void ChainInsideFieldBuilder_LoweredLate()
    {
        // C24 lowers the field initializer into a static builder method; the
        // chain inside the collection sequence must lower inside that method.
        const string source = """
            using System.Collections.Generic;
            public class C
            {
                private static string? GetS() => null;
                private static readonly List<string?> _values =
                    new List<string?> { GetS()?.Trim() };
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);

        Assert.Contains("__cs2j_init__values()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("var __ed_s0 = GetS();", result.OutputCode, StringComparison.Ordinal);
    }
}
