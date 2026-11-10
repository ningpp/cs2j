using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.TypeShapeMarker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// C# type modifiers Java cannot express are carried by empty marker interfaces
/// appended by the C#-to-C# pass; the Java converter needs no special case.
/// </summary>
public sealed class TypeShapeMarkerTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private const string SupportSource = @"
namespace Cs2jPrelude
{
    public interface Cs2jTypeShapeMarker { }
    public interface Cs2jStructMarker : Cs2jTypeShapeMarker { }
    public interface Cs2jRecordMarker : Cs2jTypeShapeMarker { }
    public interface Cs2jReadOnlyMarker : Cs2jTypeShapeMarker { }
    public interface Cs2jByRefLikeMarker : Cs2jTypeShapeMarker { }
}
";

    private static TypeShapeMarkerResult Run(string source, string relativePath = "src/Sample.cs")
        => new TypeShapeMarkerPreprocessor()
            .RewriteFile(source, ParseWithSemantics(source), relativePath);

    [Fact]
    public void Struct_ReceivesStructMarker_AndCompiles()
    {
        var result = Run("struct S { public int X; }");

        Assert.True(result.Changed);
        Assert.Contains("Cs2jStructMarker", result.OutputCode);
        Assert.DoesNotContain("Cs2jRecordMarker", result.OutputCode);
        Assert.DoesNotContain("Cs2jReadOnlyMarker", result.OutputCode);
        Assert.Equal(1, result.TypesMarked);
        Assert.Equal(1, result.MarkersAdded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void ReadonlyStruct_ReceivesStructAndReadOnlyMarkers()
    {
        var result = Run("readonly struct S { public readonly int X; }");

        Assert.True(result.Changed);
        Assert.Contains("Cs2jStructMarker", result.OutputCode);
        Assert.Contains("Cs2jReadOnlyMarker", result.OutputCode);
        Assert.Equal(2, result.MarkersAdded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void RecordClass_ReceivesRecordMarkerOnly()
    {
        var result = Run("record R(int X);");

        Assert.True(result.Changed);
        Assert.Contains("Cs2jRecordMarker", result.OutputCode);
        Assert.DoesNotContain("Cs2jStructMarker", result.OutputCode);
        Assert.Equal(1, result.MarkersAdded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void RecordStruct_ReceivesStructAndRecordMarkers()
    {
        var result = Run("record struct RS(int X);");

        Assert.True(result.Changed);
        Assert.Contains("Cs2jStructMarker", result.OutputCode);
        Assert.Contains("Cs2jRecordMarker", result.OutputCode);
        Assert.Equal(2, result.MarkersAdded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void ReadonlyRecordStruct_ReceivesAllThreeMarkers()
    {
        var result = Run("readonly record struct RRS(int X);");

        Assert.True(result.Changed);
        Assert.Contains("Cs2jStructMarker", result.OutputCode);
        Assert.Contains("Cs2jRecordMarker", result.OutputCode);
        Assert.Contains("Cs2jReadOnlyMarker", result.OutputCode);
        Assert.Equal(3, result.MarkersAdded);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void OrdinaryAndStaticClass_AreUntouched()
    {
        var source = """
            class C { public int X; }
            static class SC { public static int Y; }
            """;

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void ExistingBaseList_IsPreservedAndMarkerAppended()
    {
        var result = Run("struct S : System.IDisposable { public void Dispose() { } }");

        Assert.True(result.Changed);
        Assert.Contains("System.IDisposable", result.OutputCode);
        Assert.Contains("Cs2jStructMarker", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void StillRefStruct_IsSkipped()
    {
        var result = Run("ref struct R { private ref int _x; }");

        Assert.False(result.Changed);
        Assert.Equal(0, result.TypesMarked);
    }

    [Fact]
    public void SupportLibraryPath_IsSkipped()
    {
        var result = Run("struct S { }", "Cs2jPrelude/CsPrelude.cs");

        Assert.False(result.Changed);
    }

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        var first = Run("readonly record struct RRS(int X);");
        Assert.True(first.Changed);

        var second = new TypeShapeMarkerPreprocessor().RewriteFile(
            first.OutputCode, ParseWithSemantics(first.OutputCode), "src/Sample.cs");

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void Registry_slots_after_ref_struct_lift_and_is_default_on()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.TypeShapeMarker);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.Equal(new[] { PassIds.RefStructSpanFieldMemoryLift }, descriptor.RunAfterIds);

        var plan = PassPlanResolver.Resolve(PassCatalog.BuildRegistry());
        Assert.True(plan.IsValid);
        var ordered = plan.Enabled.Select(p => p.Id).ToList();
        Assert.True(
            ordered.IndexOf(PassIds.RefStructSpanFieldMemoryLift)
            < ordered.IndexOf(PassIds.TypeShapeMarker),
            "type-shape-marker must run after ref-struct lowering/lifting");
        Assert.True(
            ordered.IndexOf(PassIds.TypeShapeMarker) < ordered.IndexOf(PassIds.JavaKeywords),
            "java-keywords must stay last");
    }

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(SupportSource, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "type-shape-marker-test",
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(SupportSource, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "type-shape-marker-output",
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten output has compile errors:\n" +
            string.Join("\n", errors) + "\n--- output ---\n" + output);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
