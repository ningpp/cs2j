using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that typeof(T) / default(T) with a CLASS-level type parameter uses the
/// class runtime Class field instead of shadowing the class T with a
/// method-level type parameter. NullableConverter.Read previously became
/// `&lt;T&gt; T read(Class&lt;T&gt; _cs2j_T, ...)` — shadowing the class T, failing
/// the abstract override and producing "T cannot be converted to T".
/// </summary>
public class ClassLevelRuntimeClassFieldTests
{

    /// <summary>
    /// Mirrors System.Text.Json's <c>JsonPropertyInfo&lt;T&gt;.GetMemberAndWriteJson</c>:
    /// an override declared in a GENERIC class whose base lives in a different file, and
    /// whose body uses both <c>default(T) == null</c> and <c>default</c> (bare literal)
    /// for the CLASS-level T. The class-level T is satisfied by the class runtime
    /// Class field (tClass / _cs2jDefault_T), so neither the override nor its base
    /// declaration may gain a leading <c>Class&lt;T&gt; _cs2j_T</c> parameter — doing so
    /// adds a method-level &lt;T&gt; that shadows the class T ("T cannot be converted to T").
    /// </summary>

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
