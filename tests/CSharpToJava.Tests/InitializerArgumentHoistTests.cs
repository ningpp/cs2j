using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C24 argument-position initializer sites (IN-11):
/// preceding self-lowered initializer sites must NOT be hoisted a second time
/// (the duplicate hoist captured the ORIGINAL creation expression and the
/// argument-list rewrite clobbered the site's own receiver temp, leaving the
/// initializer node on disk -> CS2J0545).
/// </summary>
public class InitializerArgumentHoistTests
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

    private static ExpressionDesugarResult RunC24(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-args-" + Guid.NewGuid().ToString("N"),
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
            new ExpressionDesugarOptions { Initializers = new InitializerOptions() });
    }

    private static void AssertStrictSuccess(ExpressionDesugarResult result)
    {
        var errors = string.Join("\n",
            result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(result.Changed, "file should be rewritten. Diagnostics:\n" + errors);
        Assert.Equal("", errors);
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    [Fact]
    public void ParamsArray_MultipleInitializerArguments_EachLoweredOnce()
    {
        const string source = """
            public class Token { public int A { get; set; } }
            public class Result
            {
                public Result(int currentPosition, params Token[] tokens) { }
            }
            public class C
            {
                public void M()
                {
                    var expected = new Result(0,
                        new Token { A = 1 },
                        new Token { A = 2 });
                }
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        Assert.Contains("var __ed_0 = new Token();", code);
        Assert.Contains("__ed_0.A = 1;", code);
        Assert.Contains("var __ed_1 = new Token();", code);
        Assert.Contains("__ed_1.A = 2;", code);
        Assert.Contains("new Result(0, __ed_0, __ed_1);", code);

        // The regression: no leftover initializer syntax (duplicate hoist
        // captured the ORIGINAL creation and left it on disk).
        Assert.DoesNotContain("new Token {", code);
    }

    [Fact]
    public void NormalArguments_SelfLoweredSitesInterleavedWithSideEffects()
    {
        const string source = """
            using System;
            public class Token { public int A { get; set; } }
            public class Result
            {
                public Result(int x, Token a, int y, Token b) { }
            }
            public class C
            {
                static int Side() => 1;
                public void M()
                {
                    var expected = new Result(
                        Side(),
                        new Token { A = 1 },
                        2,
                        new Token { A = 2 });
                }
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        // The ordinary side-effecting preceding argument is hoisted once;
        // the self-lowered initializer site is not hoisted again; the literal
        // needs no temp.
        Assert.Contains("var __ed_0 = Side();", code);
        Assert.Contains("__ed_0, __ed_1, 2, __ed_2);", code);
        Assert.DoesNotContain("new Token {", code);
    }

    [Fact]
    public void EnclosingCreationWithInitializer_NestedArgumentSiteNotDoubleHoisted()
    {
        const string source = """
            public class Token { public int A { get; set; } }
            public class Holder
            {
                public Token Token { get; set; }
                public Holder(Token token) { }
            }
            public class Result
            {
                public Result(int currentPosition, params Token[] tokens) { }
            }
            public class C
            {
                public void M()
                {
                    var x = new Holder(new Token { A = 5 });
                    var expected = new Result(0,
                        new Token { A = 1 },
                        new Token { A = 2 });
                }
            }
            """;

        var result = RunC24(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        Assert.Contains("new Holder(__ed_0);", code);
        Assert.Contains("new Result(0, __ed_1, __ed_2);", code);
        Assert.DoesNotContain("new Token {", code);
    }
}
