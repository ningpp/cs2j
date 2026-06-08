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
    public void GCHandle_Alloc_ConvertsToStaticHelper()
    {
        var result = Convert("""
            using System.Runtime.InteropServices;
            class Test {
                void M() {
                    char[] arr = new char[10];
                    object handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("GCHandle.alloc(arr)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.GCHandle;", result.GeneratedCode, StringComparison.Ordinal);
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
    public void GCHandle_FullPattern_AllocAddrFree()
    {
        var result = Convert("""
            using System.Runtime.InteropServices;
            class Test {
                unsafe void M() {
                    char[] dest = new char[10];
                    GCHandle destHandle = GCHandle.Alloc(dest, GCHandleType.Pinned);
                    char* pDest = (char*)destHandle.AddrOfPinnedObject();
                    if (destHandle.IsAllocated)
                        destHandle.Free();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("GCHandle.alloc(dest)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("GCHandle.addrOfPinnedObject(destHandle)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("GCHandle.isAllocated(destHandle)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("GCHandle.free(destHandle)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getIsAllocated", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GCHandle_AddrOfPinnedObject_InstanceCall_ConvertsToStaticHelper()
    {
        var result = Convert("""
            using System.Runtime.InteropServices;
            class Test {
                unsafe void M() {
                    GCHandle handle = GCHandle.Alloc(new char[10], GCHandleType.Pinned);
                    char* p = (char*)handle.AddrOfPinnedObject();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("GCHandle.addrOfPinnedObject(handle)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.GCHandle;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GCHandle_Free_InstanceCall_ConvertsToStaticHelper()
    {
        var result = Convert("""
            using System.Runtime.InteropServices;
            class Test {
                void M() {
                    GCHandle handle = GCHandle.Alloc(new char[10], GCHandleType.Pinned);
                    handle.Free();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("GCHandle.free(handle)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.GCHandle;", result.GeneratedCode, StringComparison.Ordinal);
    }
}
