using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class GCHandleConversionTests
{
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

    [Fact]
    public void GCHandle_IsAllocated_ConvertsToStaticHelper()
    {
        var result = Convert("""
            using System.Runtime.InteropServices;
            class Test {
                void M() {
                    GCHandle handle = GCHandle.Alloc(new char[10], GCHandleType.Pinned);
                    bool allocated = handle.IsAllocated;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("GCHandle.isAllocated(handle)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.GCHandle;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getIsAllocated", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GCHandle_FullApi_UsesCompatClasses()
    {
        var result = Convert("""
            using System.Runtime.InteropServices;
            class Test {
                void M() {
                    GCHandle handle = GCHandle.Alloc(new char[10], GCHandleType.Pinned);
                    object target = handle.Target;
                    handle.Target = target;
                    bool allocated = handle.IsAllocated;
                    handle.Free();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.GCHandle;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.GCHandleType;", result.GeneratedCode, StringComparison.Ordinal);
        // GCHandle maps to the compat class (not Object) and GCHandleType to its own enum-like class (not int)
        Assert.Contains("GCHandle handle = GCHandle.alloc(new char[10], GCHandleType.Pinned);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Object handle", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("GCHandleType.Pinned()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("GCHandle.isAllocated(handle)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("handle.getIsAllocated()", result.GeneratedCode, StringComparison.Ordinal);
    }
}
