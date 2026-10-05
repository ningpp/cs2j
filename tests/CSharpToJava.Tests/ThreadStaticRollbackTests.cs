using CSharpToJava.Core.ThreadStaticRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the Core rollback loop (CS2J1206 single-plan exclusion and
/// CS2J1200 whole-run rollback). Compile errors are injected by a test
/// validator and located inside the candidate output, mirroring the CLI
/// adapter's recompile-and-diff callback without touching the file system.
/// </summary>
public class ThreadStaticRollbackTests
{
    private const string TestPath = "Test.cs";

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.Thread).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Threading.ThreadLocal<int>).Assembly.Location),
    };

    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);

    private static CSharpCompilation CompilationFactory(IReadOnlyList<ThreadStaticSourceFile> files)
        => CSharpCompilation.Create(
            "ts-rollback-test",
            files.Select(f => CSharpSyntaxTree.ParseText(
                f.Content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: f.Path)).ToArray(),
            References,
            CompilationOptions);

    private static ThreadStaticCompileError CandidateError(
        ThreadStaticRewrittenFile candidate, TextSpan span, string id = "CS9999")
    {
        var position = CSharpSyntaxTree.ParseText(candidate.Output, path: candidate.Path)
            .GetLineSpan(span).StartLinePosition;
        return new ThreadStaticCompileError(
            candidate.Path,
            span,
            id,
            "injected test error",
            position.Line + 1,
            position.Character + 1);
    }

    private static TextSpan SpanOfMethodBody(ThreadStaticRewrittenFile candidate, string methodName)
    {
        var root = CSharpSyntaxTree.ParseText(candidate.Output, path: candidate.Path).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.ValueText == methodName);
        return method.Body!.Span;
    }

    private static TextSpan SpanOfFieldDeclaration(ThreadStaticRewrittenFile candidate, string fieldName)
    {
        var root = CSharpSyntaxTree.ParseText(candidate.Output, path: candidate.Path).GetRoot();
        var field = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .First(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName));
        return field.Span;
    }

    private static void AssertCompiles(ThreadStaticRollbackResult result)
    {
        var compilation = CompilationFactory(
            result.Files.Select(f => new ThreadStaticSourceFile(f.Path, f.Output)).ToList());
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void NoInjectedErrors_RewriteAccepted_NoRollback()
    {
        const string source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;

                static void M() { f = 1; }
            }
            """;

        var result = ThreadStaticRollbackRunner.Run(
            new[] { new ThreadStaticSourceFile(TestPath, source) },
            new ThreadStaticOptions(),
            CompilationFactory,
            (_, _) => Array.Empty<ThreadStaticCompileError>());

        Assert.True(result.Success);
        Assert.Equal(1, result.Rounds);
        Assert.Equal(0, result.Statistics.PlansRolledBack);
        Assert.Equal(0, result.Statistics.WholeRunRolledBack);
        Assert.Contains("f.Value = 1;", result.Files[0].Output);
        AssertCompiles(result);
    }

    [Fact]
    public void ErrorAttributedToMethodWithoutPlanSites_WholeRunRollsBackToOriginalBytes()
    {
        const string source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;

                static void UsesF() { f = 1; }

                static void Unrelated() { int x = 1; }
            }
            """;

        var rounds = 0;
        var result = ThreadStaticRollbackRunner.Run(
            new[] { new ThreadStaticSourceFile(TestPath, source) },
            new ThreadStaticOptions(),
            CompilationFactory,
            (_, candidate) =>
            {
                rounds++;
                return new[]
                {
                    CandidateError(candidate[0], SpanOfMethodBody(candidate[0], "Unrelated")),
                };
            });

        Assert.False(result.Success);
        Assert.Equal(1, rounds);
        Assert.Equal(1, result.Statistics.WholeRunRolledBack);
        Assert.Equal(1, result.Rounds);
        Assert.All(result.Files, f => Assert.False(f.Changed));
        Assert.Equal(source, result.Files[0].Output);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.WholeTreeRollback
            && d.Severity == ThreadStaticSeverity.Error);
    }

    [Fact]
    public void ErrorAttributedToOneFieldHolder_ExcludesThatPlanAndReplaysOthers()
    {
        const string source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;
                [ThreadStatic] static int g;

                static void UseF() { f = 1; }

                static void UseG() { g = 2; }
            }
            """;

        var rounds = 0;
        var result = ThreadStaticRollbackRunner.Run(
            new[] { new ThreadStaticSourceFile(TestPath, source) },
            new ThreadStaticOptions(),
            CompilationFactory,
            (_, candidate) =>
            {
                rounds++;
                if (rounds == 1)
                {
                    return new[]
                    {
                        CandidateError(candidate[0], SpanOfFieldDeclaration(candidate[0], "f")),
                    };
                }

                return Array.Empty<ThreadStaticCompileError>();
            });

        Assert.True(result.Success);
        Assert.Equal(2, rounds);
        Assert.Equal(2, result.Rounds);
        Assert.Equal(1, result.Statistics.PlansRolledBack);
        Assert.Equal(0, result.Statistics.WholeRunRolledBack);

        var output = result.Files[0].Output;
        Assert.Contains("static int f;", output);
        Assert.DoesNotContain("ThreadLocal<int> f", output);
        Assert.Contains("UseF", output);
        Assert.DoesNotContain("f.Value", output);
        Assert.Contains(
            "global::System.Threading.ThreadLocal<int> g = new global::System.Threading.ThreadLocal<int>();",
            output);
        Assert.Contains("g.Value = 2;", output);
        Assert.Contains(result.Diagnostics,
            d => d.Code == ThreadStaticCodes.PlanRolledBack
                && d.Severity == ThreadStaticSeverity.Warning
                && d.PlanId == "C.f");
        AssertCompiles(result);
    }

    [Fact]
    public void ErrorAttributedToMethodUsingTwoPlans_WholeRunRollsBack()
    {
        const string source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;
                [ThreadStatic] static int g;

                static void UseBoth(int v)
                {
                    f = v;
                    g = v + 1;
                }
            }
            """;

        var result = ThreadStaticRollbackRunner.Run(
            new[] { new ThreadStaticSourceFile(TestPath, source) },
            new ThreadStaticOptions(),
            CompilationFactory,
            (_, candidate) => new[]
            {
                CandidateError(candidate[0], SpanOfMethodBody(candidate[0], "UseBoth")),
            });

        Assert.False(result.Success);
        Assert.Equal(1, result.Rounds);
        Assert.Equal(1, result.Statistics.WholeRunRolledBack);
        Assert.Equal(source, result.Files[0].Output);
        Assert.Contains(result.Diagnostics, d => d.Code == ThreadStaticCodes.WholeTreeRollback);
    }

    [Fact]
    public void TwoPlansAttributedInSuccessiveRounds_BothExcludedAndReplayed()
    {
        const string source = """
            using System;

            class C
            {
                [ThreadStatic] static int f;
                [ThreadStatic] static int g;

                static void UseF() { f = 1; }

                static void UseG() { g = 2; }
            }
            """;

        var rounds = 0;
        var result = ThreadStaticRollbackRunner.Run(
            new[] { new ThreadStaticSourceFile(TestPath, source) },
            new ThreadStaticOptions(),
            CompilationFactory,
            (_, candidate) =>
            {
                rounds++;
                return rounds switch
                {
                    1 => new[] { CandidateError(candidate[0], SpanOfFieldDeclaration(candidate[0], "f")) },
                    2 => new[] { CandidateError(candidate[0], SpanOfFieldDeclaration(candidate[0], "g")) },
                    _ => Array.Empty<ThreadStaticCompileError>(),
                };
            });

        Assert.True(result.Success);
        Assert.Equal(3, result.Rounds);
        Assert.Equal(2, result.Statistics.PlansRolledBack);
        Assert.Equal(source, result.Files[0].Output);
        Assert.All(result.Files, f => Assert.False(f.Changed));
        var rolledBack = result.Diagnostics
            .Where(d => d.Code == ThreadStaticCodes.PlanRolledBack)
            .Select(d => d.PlanId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "C.f", "C.g" }, rolledBack);
    }
}
