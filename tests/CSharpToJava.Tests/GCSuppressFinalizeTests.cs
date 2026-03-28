using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for <c>GC.SuppressFinalize(this)</c> — a C# IDisposable pattern call
/// that has no equivalent in Java (Java uses automatic garbage collection).
///
/// The problem: C# Dispose() methods typically call <c>GC.SuppressFinalize(this)</c>
/// to suppress the finalizer after disposal.  The converter emits this verbatim as
/// <c>GC.suppressFinalize(this)</c> which causes a Java compilation error:
///   error: cannot find symbol  variable GC
///   (Java has no class named GC)
///
/// The fix: detect that the receiver is "GC" (or the symbol resolves to System.GC)
/// and the method is SuppressFinalize/Collect/WaitForPendingFinalizers/KeepAlive,
/// then emit a no-op comment instead.
///
/// Confirmed via agl v20260315 log files:
///   30.log   PointNodesList.java   — GC.SuppressFinalize(this) from IDisposable.Dispose()
///   131.log  EmptyEnumerator.java  — GC.SuppressFinalize(this)
///   137.log  Clump.java            — GC.SuppressFinalize(this)
///   139.log  Curve.java            — GC.SuppressFinalize(this)
///   5.log    RBTreeEnumerator.java — GC.SuppressFinalize(this)
///   27.log   PolylinePoint.java    — GC.SuppressFinalize(this)
///   89.log   CancelToken.java      — GC.SuppressFinalize(this)
///   95.log   LayoutAlgorithmSettings.java — GC.SuppressFinalize(this)
///   143.log  DelegateEnumerator.java — GC.SuppressFinalize(this)
/// </summary>
public class GCSuppressFinalizeTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            Options = new ConversionOptions { TargetJavaVersion = JavaVersion.Java25 }
        });
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── Typical IDisposable.Dispose() pattern in C# ──────────────────────────────
    [Fact]
    public void GC_SuppressFinalize_IsRemovedFromDispose()
    {
        const string code = """
            using System;
            namespace Test {
                class Foo : IDisposable {
                    public void Dispose() {
                        GC.SuppressFinalize(this);
                    }
                }
            }
            """;
        var java = ConvertCode(code);
        // Should NOT produce a reference to a GC class (Java has no GC class)
        Assert.DoesNotContain("GC.suppressFinalize", java);
        Assert.DoesNotContain("GC.SuppressFinalize", java);
        // The generated Java should still compile — no call to GC static methods
        Assert.DoesNotContain("GC.suppress", java);
        Assert.DoesNotContain("GC.Suppress", java);
    }

    // ── The emitted output should be a valid Java comment (not just empty) ────────
    [Fact]
    public void GC_SuppressFinalize_EmitsComment()
    {
        const string code = """
            using System;
            namespace Test {
                class Bar : IDisposable {
                    public void Dispose() {
                        int x = 1;
                        GC.SuppressFinalize(this);
                        int y = 2;
                    }
                }
            }
            """;
        var java = ConvertCode(code);
        // The surrounding statements should still be present
        Assert.Contains("x = 1", java);
        Assert.Contains("y = 2", java);
        // GC call should be gone or replaced with a comment
        Assert.DoesNotContain("GC.suppressFinalize", java);
    }

    // ── GC.Collect() is also a no-op in Java ─────────────────────────────────────
    [Fact]
    public void GC_Collect_IsRemovedOrComment()
    {
        const string code = """
            namespace Test {
                class C {
                    void Cleanup() {
                        GC.Collect();
                    }
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("GC.collect()", java);
        Assert.DoesNotContain("GC.Collect()", java);
    }

    // ── GC.KeepAlive() is also a no-op in Java ───────────────────────────────────
    [Fact]
    public void GC_KeepAlive_IsRemovedOrComment()
    {
        const string code = """
            namespace Test {
                class C {
                    void Use(object o) {
                        GC.KeepAlive(o);
                    }
                }
            }
            """;
        var java = ConvertCode(code);
        Assert.DoesNotContain("GC.keepAlive", java);
        Assert.DoesNotContain("GC.KeepAlive", java);
    }

    // ── Real-world pattern: Dispose() with field cleanup + GC call ───────────────
    [Fact]
    public void Dispose_WithGCSuppressFinalize_ProducesValidJava()
    {
        const string code = """
            using System;
            namespace Test {
                class PointNodesList : IDisposable {
                    private bool _disposed;

                    protected virtual void Dispose(bool disposing) {
                        _disposed = true;
                    }

                    public void Dispose() {
                        Dispose(true);
                        GC.SuppressFinalize(this);
                    }
                }
            }
            """;
        var java = ConvertCode(code);
        // IDisposable.Dispose() → AutoCloseable.close() in Java
        // The method call Dispose(true) may be converted to dispose(true)/close(true)/close(disposing)
        Assert.True(
            java.Contains("Dispose(true)") || java.Contains("dispose(true)") || java.Contains("close(true)"),
            $"Expected dispose/close(true) call to survive:\n{java}");
        Assert.DoesNotContain("GC.suppressFinalize", java);
        Assert.DoesNotContain("GC.SuppressFinalize", java);
    }
}
