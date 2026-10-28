using CSharpToJava.Core.EnumPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

// Namespace deliberately differs from the folder name: a
// CSharpToJava.Tests.EnumPreprocessor namespace would shadow the
// EnumPreprocessor type for every existing test in CSharpToJava.Tests.
namespace CSharpToJava.Tests.EnumPreprocessing;

using EnumPreprocessorImpl = CSharpToJava.Core.EnumPreprocessor.EnumPreprocessor;

/// <summary>
/// Regression for the GraphLayout corpus
/// (MSAGL/Routing/Rectilinear/SsstRectilinearPath.cs):
/// <code>
/// private Direction EntryDirectionsToTarget { get; set; }
/// ...
/// EntryDirectionsToTarget |= CompassVector.DirectionsFromPointToPoint(edge.TargetPoint, vert.Point);
/// </code>
/// The compound-assignment target is a property reached through the implicit
/// <c>this</c> receiver. The expansion gate only recognised the explicit
/// <c>this.P</c> member-access shape, so both bare-identifier sites were
/// skipped with "not a stable reference", survived as residual enum compound
/// assignments and tripped the strict residual gate for the whole project.
/// </summary>
public sealed class CompoundAssignmentImplicitThisPropertyTests
{
    private static EnumPreprocessorResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("enum-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new EnumPreprocessorImpl().Preprocess(source, model);
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static string CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("enum-output-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return string.Join("\n", compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>GraphLayout SsstRectilinearPath.InitEntryDirectionsAtTarget exact shape.</summary>
    private const string SsstRectilinearPathSource = """
        using System;
        using System.Collections.Generic;

        namespace Microsoft.Msagl.Core.Geometry
        {
            [Flags]
            public enum Direction
            {
                None = 0,
                North = 1,
                East = 2,
                West = 4,
                South = 8
            }

            public struct Point { }

            public static class CompassVector
            {
                public static Direction DirectionsFromPointToPoint(Point from, Point to) => Direction.None;
            }
        }

        namespace Microsoft.Msagl.Routing.Rectilinear
        {
            using Microsoft.Msagl.Core.Geometry;

            public class VisibilityEdge
            {
                public Point SourcePoint { get; set; }
                public Point TargetPoint { get; set; }
            }

            public class VisibilityVertex
            {
                public Point Point { get; set; }
                public List<VisibilityEdge> OutEdges { get; } = new List<VisibilityEdge>();
                public List<VisibilityEdge> InEdges { get; } = new List<VisibilityEdge>();
            }

            public class VertexEntry
            {
                public Direction Direction { get; set; }
            }

            public class SsstRectilinearPath
            {
                private Direction EntryDirectionsToTarget { get; set; }

                private bool InitEntryDirectionsAtTarget(VisibilityVertex vert)
                {
                    EntryDirectionsToTarget = Direction.None;

                    foreach (var edge in vert.OutEdges)
                    {
                        EntryDirectionsToTarget |= CompassVector.DirectionsFromPointToPoint(edge.TargetPoint, vert.Point);
                    }
                    foreach (var edge in vert.InEdges)
                    {
                        EntryDirectionsToTarget |= CompassVector.DirectionsFromPointToPoint(edge.SourcePoint, vert.Point);
                    }
                    return EntryDirectionsToTarget != Direction.None;
                }

                private void RemoveEntryDirection(VertexEntry bestEntry)
                {
                    this.EntryDirectionsToTarget &= ~bestEntry.Direction;
                }
            }
        }
        """;

    [Fact]
    public void Implicit_This_Property_Compound_Or_Is_Expanded()
    {
        var result = Preprocess(SsstRectilinearPathSource);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);

        // Bare-identifier property targets must expand exactly like `this.P`
        // does; the getter/setter pair is still invoked once each.
        Assert.Contains(
            "EntryDirectionsToTarget = ((Direction)((int)EntryDirectionsToTarget"
            + " | (int)(CompassVector.DirectionsFromPointToPoint(edge.TargetPoint, vert.Point))))",
            code);
        Assert.Contains(
            "EntryDirectionsToTarget = ((Direction)((int)EntryDirectionsToTarget"
            + " | (int)(CompassVector.DirectionsFromPointToPoint(edge.SourcePoint, vert.Point))))",
            code);
        Assert.DoesNotContain("|=", code);
        Assert.DoesNotContain("&=", code);
        Assert.Equal(3, result.Statistics.CompoundAssignRewritten);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Implicit_This_Property_Compound_Or_Leaves_No_Warning()
    {
        var result = Preprocess(SsstRectilinearPathSource);

        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Severity == EnumPreprocessorSeverity.Warning
                 && d.Message.Contains("not a stable reference"));
        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Message.Contains("residual enum bitwise/HasFlag"));
    }

    [Theory]
    [InlineData("|=", "|")]
    [InlineData("&=", "&")]
    [InlineData("^=", "^")]
    public void Implicit_This_Property_Compound_Assignments_Are_Expanded(
        string compoundOperator, string binaryOperator)
    {
        var src = $$"""
            [System.Flags]
            enum E { A = 1, B = 2 }
            class C
            {
                private E Flags { get; set; }
                void M(E other)
                {
                    Flags {{compoundOperator}} other;
                }
            }
            """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains(
            $"Flags = ((E)((int)Flags {binaryOperator} (int)other))", code);
        Assert.DoesNotContain(compoundOperator, code);
        Assert.Equal(1, result.Statistics.CompoundAssignRewritten);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Implicit_This_Nullable_Property_Compound_Assignment_Stays_Skipped()
    {
        // A nullable property target is read twice (HasValue + Value) when
        // expanded, so it keeps requiring a stable reference (local/parameter/
        // field) even though the implicit-this receiver is deterministic.
        var src = """
            [System.Flags]
            enum E { A = 1, B = 2 }
            class C
            {
                private E? Flags { get; set; }
                void M(E other)
                {
                    Flags |= other;
                }
            }
            """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Contains(
            result.Diagnostics,
            d => d.Severity == EnumPreprocessorSeverity.Warning
                 && d.Message.Contains("Nullable enum compound assignment target"));
    }
}
