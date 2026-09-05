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
    [Fact]
    public void TypeOfOnClassTypeParameter_UsesClassField_NotMethodTypeParam()
    {
        var result = Convert(@"
abstract class Base<T>
{
    public abstract T Read();
}
class Derived<T> : Base<T>
{
    public override T Read()
    {
        System.Type t = typeof(T);
        return default(T);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("public T read()", code);
        Assert.DoesNotContain("<T> T read(", code);
        Assert.DoesNotContain("_cs2j_T", code);
        Assert.Contains("tClass", code);
    }

    /// <summary>
    /// Mirrors System.Text.Json's <c>JsonPropertyInfo&lt;T&gt;.GetMemberAndWriteJson</c>:
    /// an override declared in a GENERIC class whose base lives in a different file, and
    /// whose body uses both <c>default(T) == null</c> and <c>default</c> (bare literal)
    /// for the CLASS-level T. The class-level T is satisfied by the class runtime
    /// Class field (tClass / _cs2jDefault_T), so neither the override nor its base
    /// declaration may gain a leading <c>Class&lt;T&gt; _cs2j_T</c> parameter — doing so
    /// adds a method-level &lt;T&gt; that shadows the class T ("T cannot be converted to T").
    /// </summary>
    [Fact]
    public async Task OverrideInGenericClass_DefaultTForClassTypeParam_DoesNotInjectClassToken()
    {
        var results = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "PropInfoBase.cs", Content = """
                namespace Demo
                {
                    public abstract class PropInfoBase
                    {
                        public abstract bool Write(System.Object obj);
                    }
                }
                """ },
            new SourceFile { FilePath = "PropInfoOfT.cs", Content = """
                namespace Demo
                {
                    public sealed class PropInfoOfT<T> : PropInfoBase
                    {
                        public System.Func<object, T> Get;
                        public override bool Write(System.Object obj)
                        {
                            T value = Get(obj);
                            return default(T) == null
                                ? value == null
                                : System.Collections.Generic.EqualityComparer<T>.Default.Equals(default, value);
                        }
                    }
                }
                """ },
        });

        var derived = Assert.Single(results, r => r.FileName == "PropInfoOfT.java");
        Assert.True(derived.Success, string.Join("\n", derived.Diagnostics) + "\n---\n" + derived.GeneratedCode);
        var derivedCode = derived.GeneratedCode ?? "";
        // The override must keep the base slot's erasure signature: no leading Class<T>.
        Assert.DoesNotContain("_cs2j_T", derivedCode);
        Assert.DoesNotContain("<T> boolean write(", derivedCode);
        Assert.Contains("boolean write(", derivedCode);
        // The class-level default(T) still resolves through the class runtime Class field.
        Assert.Contains("tClass", derivedCode);

        var baseFile = Assert.Single(results, r => r.FileName == "PropInfoBase.java");
        Assert.True(baseFile.Success, string.Join("\n", baseFile.Diagnostics) + "\n---\n" + baseFile.GeneratedCode);
        var baseCode = baseFile.GeneratedCode ?? "";
        // The abstract base is non-generic: it must not gain any Class token either,
        // otherwise the override would no longer match its slot.
        Assert.DoesNotContain("_cs2j_", baseCode);
    }

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
