using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// The Java converter only sees ordinary implements clauses for C# type shapes;
/// no per-shape special case is needed.
/// </summary>
public class TypeShapeMarkerConversionTests
{
    private const string SupportTypes = """
        namespace Cs2jPrelude
        {
            public interface Cs2jTypeShapeMarker { }
            public interface Cs2jStructMarker : Cs2jTypeShapeMarker { }
            public interface Cs2jRecordMarker : Cs2jTypeShapeMarker { }
            public interface Cs2jReadOnlyMarker : Cs2jTypeShapeMarker { }
        }
        """;

    [Fact]
    public void Struct_IsMappedToJavaMarkerInterface()
    {
        var result = Convert(SupportTypes + """

            struct S : global::Cs2jPrelude.Cs2jStructMarker { public int X; }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("implements Cs2jStructMarker", result.GeneratedCode);
        Assert.DoesNotContain("__suppress__", result.GeneratedCode);
    }

    [Fact]
    public void Record_KeepsRecordShapeAndMarkerInterface()
    {
        var result = Convert(SupportTypes + """

            record R(int X) : global::Cs2jPrelude.Cs2jRecordMarker;
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("implements Cs2jRecordMarker", result.GeneratedCode);
        Assert.DoesNotContain("__suppress__", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = System.IO.Path.Combine(
                    AppContext.BaseDirectory, "config", "TypeMappings.json"),
            },
        });
    }
}
