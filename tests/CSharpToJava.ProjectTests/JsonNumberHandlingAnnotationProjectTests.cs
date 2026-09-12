using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// A type-level C# [JsonNumberHandling(...)] must reach Java as the compat marker annotation.
/// The generated JsonNumberHandlingAttribute is a class, not an annotation, so compat
/// materializes it from that marker when JsonClassInfo asks a type for its number handling.
/// Without it getNumberHandlingForType() answers null and a List&lt;int&gt; subclass that allows
/// reading numbers from strings is read with strict handling ("1" fails for int).
///
/// This has to go through the project pipeline: the single-file pipeline does not run
/// ClassTransformer.TransformMerged, where type-level annotations are attached.
/// </summary>
public class JsonNumberHandlingAnnotationProjectTests
{
    private readonly ITestOutputHelper _out;

    public JsonNumberHandlingAnnotationProjectTests(ITestOutputHelper output) => _out = output;

    private const string Source = """
using System;
using System.Collections.Generic;

namespace dotnet.text.json.Serialization
{
    [Flags]
    public enum JsonNumberHandling
    {
        Strict = 0x0,
        AllowReadingFromString = 0x1,
        WriteAsString = 0x2,
        AllowNamedFloatingPointLiterals = 0x4,
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class JsonNumberHandlingAttribute : Attribute
    {
        public JsonNumberHandlingAttribute(JsonNumberHandling handling) { Handling = handling; }
        public JsonNumberHandling Handling { get; }
    }
}

public class Sample
{
    [dotnet.text.json.Serialization.JsonNumberHandling(dotnet.text.json.Serialization.JsonNumberHandling.AllowReadingFromString | dotnet.text.json.Serialization.JsonNumberHandling.WriteAsString)]
    public class MyCustomList : List<int> { }

    public class Plain { public int Value { get; set; } }
}
""";

    [Fact]
    public async Task TypeLevelNumberHandling_IsEmittedAsCompatAnnotationWithFlagsValue()
    {
        var dir = CreateFixtureDirectory();
        try
        {
            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(dir, new ConversionOptions());

            Assert.NotEmpty(results);
            var code = string.Join("\n// ----\n", results.Select(r => r.GeneratedCode ?? ""));
            _out.WriteLine(code);
            // AllowReadingFromString | WriteAsString == 0x3
            Assert.Contains("@CompatJsonNumberHandling(3)", code);
            Assert.Contains("CompatJsonNumberHandling;", code);
            // Only the annotated type carries the marker.
            Assert.Equal(1, code.Split("@CompatJsonNumberHandling(").Length - 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateFixtureDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2j-numhandling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Sample.cs"), Source);
        return dir;
    }
}
