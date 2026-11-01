using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS1615 at internal <c>in</c> call sites (SDK json
/// <c>Utf8JsonReader.MultiSegment.cs</c>:
/// <c>RollBackState(in state, false)</c>). The declaration side strips
/// <c>in</c> from internal in-parameters, but a call that passes no ref/out
/// argument is never statement-lifted, so the explicit <c>in</c> keyword used
/// to survive against the rewritten by-value parameter.
/// </summary>
public class RefOutInCallSiteInTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

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

    private static IReadOnlyList<MetadataReference> References(MetadataReference? extra)
        => extra == null ? GetReferences().ToList() : GetReferences().Append(extra).ToList();

    private static RefOutInRewriterResult Run(string src, MetadataReference? extra = null)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-callsite-in-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static void AssertOutputCompiles(string code, MetadataReference? extra = null)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-callsite-in-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Emits a stub assembly so its <c>in</c> parameter is a METADATA
    /// declaration: the pass classifies it External and must leave the
    /// call-site keyword alone.
    /// </summary>
    private static MetadataReference ExternalLibrary(string src)
    {
        var compilation = CSharpCompilation.Create(
            "roi-callsite-in-ext-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(src, ParseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "external stub emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return MetadataReference.CreateFromImage(ms.ToArray());
    }

    private static string LineContaining(string code, string needle) =>
        code.Split('\n').First(l => l.Contains(needle));

    private const string InternalInSource = """
        public struct S { public int X; }

        public static class Reader
        {
            static int Read(in S s) => s.X;

            public static int M()
            {
                S s = new S();
                s.X = 7;
                return Read(in s);
            }
        }
        """;

    [Fact]
    public void Internal_In_Parameter_Drops_In_At_Declaration_And_Call_Site()
    {
        var result = Run(InternalInSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // Declaration side: `in` stripped by the pass.
        Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])in\s+S\b"), result.OutputCode);
        // Call site: the explicit keyword is gone (CS1615 otherwise).
        Assert.DoesNotContain("in s" + ")",
            LineContaining(result.OutputCode, "return Read("));

        AssertOutputCompiles(result.OutputCode);
    }

    private const string InOverloadCollisionSource = """
        public struct S { public int X; }

        public static class Reader
        {
            public static int Read(S s) => s.X;
            public static int Read(in S s) => s.X + 1;

            public static int M()
            {
                S s = new S();
                s.X = 7;
                return Read(in s);
            }
        }
        """;

    [Fact]
    public void Kept_In_Overload_Keeps_Keyword_At_Declaration_And_Call_Site()
    {
        var result = Run(InOverloadCollisionSource);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // Overload collision: the `in` overload keeps its modifier...
        Assert.Contains("Read(in S s)", result.OutputCode);
        // ...so the call site must keep it as well (never a false strip).
        Assert.Contains("Read(in s)", LineContaining(result.OutputCode, "return Read("));

        AssertOutputCompiles(result.OutputCode);
    }

    // Metadata-only callee (emitted as a separate assembly): the pass never
    // rewrites its declaration, so the call-site `in` must be preserved.
    private const string ExternalInLibrary = """
        namespace ExternalLib
        {
            public struct S { public int X; }

            public static class Reader
            {
                public static int Read(in S s) => s.X;
            }
        }
        """;

    private const string ExternalInCaller = """
        public static class Caller
        {
            public static int M()
            {
                ExternalLib.S s = new ExternalLib.S();
                s.X = 3;
                return ExternalLib.Reader.Read(in s);
            }
        }
        """;

    [Fact]
    public void External_Metadata_In_Parameter_Keeps_Keyword_At_Call_Site()
    {
        var external = ExternalLibrary(ExternalInLibrary);
        var result = Run(ExternalInCaller, external);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("Read(in s)", LineContaining(result.OutputCode, "return ExternalLib.Reader.Read("));

        AssertOutputCompiles(result.OutputCode, external);
    }
}
