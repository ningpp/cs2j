using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0201 in void expression lambdas such as
/// <c>AssertThrows(json, (jsonReader) =&gt; jsonReader.TryGet(out _))</c>:
/// the lifted call is materialized as <c>var __callN = …;</c> in the hoisted
/// statements, and the conversion to a block body used to append the temp
/// identifier as a statement (<c>__callN;</c>), which is not a legal
/// statement-expression. The discarded result must simply be dropped, while
/// non-void delegates still <c>return __callN;</c>.
/// </summary>
public class RefOutInVoidLambdaLiftedTempTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
                public static bool ReadbackBool(ByRef<bool> slot) => slot.Value;
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override ref T Value => ref _slot;
            }
        }
        """;

    private const string Source = """
        using System;

        public struct S
        {
            public bool TryGet(out int v) { v = 1; return true; }
        }

        public static class H
        {
            public static void Expect(Action<S> action) { action(new S()); }
            public static bool ExpectFunc(Func<S, bool> f) => f(new S());

            public static void M(S s)
            {
                Expect((x) => x.TryGet(out _));
            }

            public static void MResult(S s)
            {
                ExpectFunc((x) => x.TryGet(out _));
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

    private static RefOutInRewriterResult Run()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-void-lambda-temp-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(Source, model);
    }

    /// <summary>
    /// The preprocessing compilation carries no MSTest metadata: <c>Assert</c>
    /// does not bind, so the callback's converted delegate type is unknown and
    /// the lifted <c>__callN</c> temp used to be returned from a callback that
    /// the assertion API discards.
    /// </summary>
    private const string AssertCallbackSource = """
        #pragma warning disable
        using System;

        public struct Router
        {
            public bool RouteEdgeToPort(out int v) { v = 0; return true; }
        }

        public static class H
        {
            public static void M(Router router)
            {
                Assert.ThrowsException<ArgumentNullException>(
                    () => router.RouteEdgeToPort(out _));
            }
        }
        """;

    private static RefOutInRewriterResult RunAssertCallback()
    {
        var tree = CSharpSyntaxTree.ParseText(AssertCallbackSource, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-void-assert-temp-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(AssertCallbackSource, model);
    }

    [Fact]
    public void AssertThrowsException_OutDiscard_Lambda_Does_Not_Return_Lifted_Temp()
    {
        var result = RunAssertCallback();

        Assert.True(result.Changed,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("out _", result.OutputCode);
        Assert.Contains("__call", result.OutputCode);

        var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions);
        var callbackReturns = outputTree.GetRoot().DescendantNodes()
            .OfType<AnonymousFunctionExpressionSyntax>()
            .SelectMany(l => l.DescendantNodes().OfType<ReturnStatementSyntax>())
            .ToList();
        Assert.Empty(callbackReturns);
        Assert.DoesNotContain("return __call", result.OutputCode);
    }

    private static Compilation AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-void-lambda-temp-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten output has errors:\n" + string.Join("\n", errors)
            + "\n---Generated---\n" + code);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return compilation;
    }

    [Fact]
    public void Void_Lambda_Discarded_Lifted_Call_Does_Not_Emit_Bare_Temp_Statement()
    {
        var result = Run();

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.DoesNotContain("out _", result.OutputCode);

        var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions);
        var bareTemps = outputTree.GetRoot().DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression)
            .OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.ValueText.StartsWith("__call", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(bareTemps);

        var callDeclarations = outputTree.GetRoot().DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText.StartsWith("__call", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(callDeclarations);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Non_Void_Lambda_Still_Returns_Lifted_Call_Temp()
    {
        var result = Run();

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions);
        var returnedTemps = outputTree.GetRoot().DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(r => r.Expression is IdentifierNameSyntax id
                && id.Identifier.ValueText.StartsWith("__call", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(returnedTemps);

        AssertOutputCompiles(result.OutputCode);
    }
}
