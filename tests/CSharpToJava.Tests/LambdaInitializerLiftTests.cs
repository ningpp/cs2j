using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for initializer sites inside expression-bodied lambdas
/// (CS2J0537 root fix): the family TempLifter converts the hosting lambda
/// itself into a block body — a return tail for Func-style inferred
/// delegates, an expression-statement tail for Action-style ones — and the
/// lifted statements stay at the lambda's deferred evaluation position.
/// The emitted C# must compile cleanly and no initializer syntax may survive
/// (CS2J0545 gate stays closed).
/// </summary>
public class LambdaInitializerLiftTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.HashSet<>).Assembly.Location),
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
            "c24-lambda-" + Guid.NewGuid().ToString("N"),
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
            });
    }

    private static void AssertStrictSuccess(ExpressionDesugarResult result)
    {
        var errors = string.Join("\n",
            result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(result.Changed, "file should be rewritten. Diagnostics:\n" + errors);
        Assert.Equal("", errors);
        Assert.DoesNotContain("CS2J0537", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CS2J0545", result.OutputCode, StringComparison.Ordinal);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static string CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-lambda-out-" + Guid.NewGuid().ToString("N"),
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
    public void VoidActionLambda_ArgumentInitializer_ExpandsToBlockBody()
    {
        // Mirrors the RegexCharacterSetTests.cs corpus sites:
        // Assert.Throws<XunitException>(() => ValidateSet(..., new HashSet<char> { 'b' }, ...))
        const string source = """
            using System;
            using System.Collections.Generic;
            public class C
            {
                private static void Validate(char c, HashSet<char> set) { }
                private static void M()
                {
                    Action a = () => Validate('b', new HashSet<char> { 'b' });
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        // Action delegate -> expression-statement tail (no return).
        Assert.Contains("var __ed_0 = new HashSet<char>();", code);
        Assert.Contains("__ed_0.Add('b');", code);
        Assert.Contains("Validate('b', __ed_0);", code);
        Assert.DoesNotContain("return Validate", code);
    }

    [Fact]
    public void FuncLambda_CreationBody_ExpandsWithReturnTail()
    {
        const string source = """
            using System;
            public class Node { public int A { get; set; } }
            public class C
            {
                private static void M()
                {
                    Func<Node> f = () => new Node { A = 1 };
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        Assert.Contains("var __ed_0 = new Node();", code);
        Assert.Contains("__ed_0.A = 1;", code);
        Assert.Contains("return __ed_0;", code);
    }

    [Fact]
    public void FuncLambda_ArgumentInitializer_ReturnsCallWithTemp()
    {
        const string source = """
            using System;
            public class Node { public int A { get; set; } }
            public class C
            {
                private static int Compute(Node n) => 0;
                private static void M()
                {
                    Func<int> f = () => Compute(new Node { A = 1 });
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        var declare = code.IndexOf("var __ed_0 = new Node();", StringComparison.Ordinal);
        var ret = code.IndexOf("return Compute(__ed_0);", StringComparison.Ordinal);
        Assert.True(declare >= 0 && ret >= 0 && declare < ret);
    }

    [Fact]
    public void BlockBodiedLambda_SiteLiftsAtInnerStatement()
    {
        // Already block-bodied lambdas must keep using their inner statement
        // anchor (no lambda conversion, evaluation timing unchanged).
        const string source = """
            using System;
            public class Node { public int A { get; set; } }
            public class C
            {
                private static void Use(Node n) { }
                private static void M()
                {
                    Action a = () =>
                    {
                        Use(new Node { A = 1 });
                    };
                }
            }
            """;

        var result = Run(source);
        AssertStrictSuccess(result);
        var code = Squish(result.OutputCode);

        Assert.Contains("var __ed_0 = new Node();", code);
        Assert.Contains("Use(__ed_0);", code);
    }
}
