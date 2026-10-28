using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// Regression for the GraphLayout corpus
/// (Test/AglNingppTests/Agl/DebugHelpers/GeometryGraphWriterTest.cs):
/// <code>
/// var writer = new GeometryGraphWriter {
///     Graph = graph,
///     Stream = ms,
///     XmlWriter = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true }),
///     EdgeEnumeration = graph.Edges
/// };
/// </code>
/// The <c>new XmlWriterSettings { … }</c> creation is buried inside a call
/// argument of an initializer member value. It is neither a direct initializer
/// item (TryNested) nor an Add argument (TryNestedBySyntax), so the analysis
/// never planned it; lowering the outer site emitted the member value verbatim,
/// the nested initializer survived on disk without a pairing entry, and the
/// strict residual gate reported CS2J0545 and aborted the file.
/// It must be lowered inline (before the outer member assignment, preserving
/// left-to-right evaluation order) exactly like a direct nested creation.
/// </summary>
public sealed class InitializerEmbeddedNestedCreationTests
{
    /// <summary>GraphLayout GeometryGraphWriterTest exact shape (condensed).</summary>
    private const string GeometryGraphWriterTestSource = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Xml;

        namespace Microsoft.Msagl.DebugHelpers.Persistence {
            public class GeometryGraph {
                public List<object> Edges { get; } = new List<object>();
                public List<object> Nodes { get; } = new List<object>();
            }

            public class GeometryGraphWriter {
                public GeometryGraphWriter() { }
                public GeometryGraphWriter(Stream stream, GeometryGraph graph, object settings) { }
                public GeometryGraph Graph { get; set; }
                public Stream Stream { get; set; }
                public XmlWriter XmlWriter { get; set; }
                public IEnumerable<object> EdgeEnumeration { get; set; }
                public void Write() { }
            }
        }

        namespace AglNingppTests.Agl.DebugHelpersTests {
            using Microsoft.Msagl.DebugHelpers.Persistence;

            public class GeometryGraphWriterTest {
                public void Stream_CanBeSetExternally() {
                    var graph = new GeometryGraph();
                    using (var ms = new MemoryStream()) {
                        var writer = new GeometryGraphWriter {
                            Graph = graph,
                            Stream = ms,
                            XmlWriter = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true }),
                            EdgeEnumeration = graph.Edges
                        };
                        Assert.Same(ms, writer.Stream);
                    }
                }
            }

            static class Assert {
                public static void Same(object expected, object actual) { }
            }
        }
        """;

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.IO.MemoryStream).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Xml.XmlWriter).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static ExpressionDesugarResult Run(string source)
    {
        var (compilation, model) = Compile(source, "init-embedded-in");
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList());
        var result = new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                Initializers = new InitializerOptions { Strict = true },
            });
        return result;
    }

    private static (CSharpCompilation Compilation, SemanticModel Model) Compile(
        string source, string name)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            name + "-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    [Fact]
    public void Creation_Inside_Call_Argument_Of_Member_Value_Is_Lowered_Inline()
    {
        var result = Run(GeometryGraphWriterTestSource);

        var errors = string.Join(
            "\n", result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(result.Changed, "file should be rewritten. Diagnostics:\n" + errors);
        Assert.Equal(string.Empty, errors);
        Assert.DoesNotContain("CS2J0545", string.Join(
            "\n", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));

        // No object initializer may survive, including the embedded one.
        Assert.Equal(0, result.Statistics.Initializers.ResidualInitializerCount);

        var output = Squish(result.OutputCode);
        Assert.DoesNotContain("new XmlWriterSettings {", output);
        Assert.Matches(@"var __ed_\d+ = new GeometryGraphWriter\(\)", output);
        Assert.Matches(@"var __ed_\d+ = new XmlWriterSettings\(\)", output);
        Assert.Contains(".Indent = true;", output);

        // The embedded sequence precedes the outer member assignment it feeds;
        // the outer construction stays first.
        var outerConstruct = output.IndexOf(
            "new GeometryGraphWriter()", StringComparison.Ordinal);
        var settingsConstruct = output.IndexOf(
            "new XmlWriterSettings()", StringComparison.Ordinal);
        var xmlWriterAssign = output.IndexOf(
            ".XmlWriter = XmlWriter.Create(ms,", StringComparison.Ordinal);
        Assert.True(outerConstruct >= 0);
        Assert.True(settingsConstruct > outerConstruct);
        Assert.True(xmlWriterAssign > settingsConstruct);

        Assert.Empty(CompileErrors(result.OutputCode));
    }

    /// <summary>
    /// GraphLayout RectilinearEdgeRouterApiTest / VisibilityGraphVariantsTest /
    /// BundleRouterSettingsTest shape:
    /// <code>
    /// var router = new RectilinearEdgeRouter(new List&lt;Shape&gt; { outer, inner }, 1, 0, false, 1) {
    ///     RouteToCenterOfObstacles = true
    /// };
    /// </code>
    /// The argument site is not inside an initializer expression, so the old
    /// analysis also planned it as an independent top-level site; the family
    /// lifter then placed its statements AFTER the outer construction that
    /// consumes its temp (CS0841: local variable used before declaration). The
    /// argument belongs to the outer site's emitted expression and must be
    /// emitted inline, before the construction statement.
    /// </summary>
    [Fact]
    public void Creation_In_Constructor_Argument_Is_Emitted_Before_The_Outer_Construction()
    {
        const string source = """
            using System.Collections.Generic;

            public class Shape {
                public Shape(int x) { }
            }

            public class RectilinearEdgeRouter {
                public RectilinearEdgeRouter(
                    List<Shape> obstacles, double scale, double padding, bool limit, double bend) { }
                public bool RouteToCenterOfObstacles { get; set; }
            }

            public class C {
                public void M() {
                    var outer = new Shape(0);
                    var inner = new Shape(20);
                    var router = new RectilinearEdgeRouter(new List<Shape> { outer, inner }, 1, 0, false, 1) {
                        RouteToCenterOfObstacles = true
                    };
                    Assert(router);
                }

                static void Assert(object o) { }
            }
            """;

        var result = Run(source);

        var errors = string.Join(
            "\n", result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(result.Changed, "file should be rewritten. Diagnostics:\n" + errors);
        Assert.Equal(string.Empty, errors);
        Assert.Equal(0, result.Statistics.Initializers.ResidualInitializerCount);

        var output = Squish(result.OutputCode);
        var listConstruct = output.IndexOf(
            "new List<Shape>()", StringComparison.Ordinal);
        var routerConstruct = output.IndexOf(
            "new RectilinearEdgeRouter(", StringComparison.Ordinal);
        Assert.True(listConstruct >= 0, output);
        Assert.True(routerConstruct > listConstruct, output);

        // This is the corpus CS0841: the argument temp must be declared before
        // the construction statement that references it.
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    private static List<Diagnostic> CompileErrors(string code)
    {
        var (compilation, _) = Compile(code, "init-embedded-out");
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }
}
