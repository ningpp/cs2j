using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Verifies that C# 'internal' constructors are mapped to 'public' in Java,
/// consistent with the project-wide policy that 'internal' → 'public'.
/// Previously ConstructorTransformer uniquely mapped 'internal' to package-private
/// (no modifier), which caused "not visible outside package" compile errors when
/// the enclosing class was public.
/// </summary>
public class InternalConstructorAccessTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Test 1 ──────────────────────────────────────────────────────────────────
    // An 'internal' constructor on an 'internal' class must become 'public' in Java.

    [Fact]
    public void InternalConstructor_MapsToPublic()
    {
        const string code = """
            using System;
            using System.Collections.Generic;

            namespace DataStructures {

                internal class RbTree<T> : IEnumerable<T> {

                    internal RbTree(IComparer<T> comparer) {
                        this.comparer = comparer;
                    }

                    private IComparer<T> comparer;

                    public IEnumerator<T> GetEnumerator() => null;
                    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Constructor must be public (not package-private)
        Assert.Contains("public RbTree(", result.GeneratedCode);
    }

    // ── Test 2 ──────────────────────────────────────────────────────────────────
    // Multiple 'internal' constructors (overloaded) must all become 'public'.

    [Fact]
    public void InternalConstructors_Overloaded_AllMapToPublic()
    {
        const string code = """
            using System;
            using System.Collections.Generic;

            namespace DataStructures {

                internal class RbTree<T> : IEnumerable<T> {

                    internal RbTree(Func<T, T, int> func) : this(null) {}

                    internal RbTree(IComparer<T> comparer) {
                        this.comparer = comparer;
                    }

                    private IComparer<T> comparer;

                    public IEnumerator<T> GetEnumerator() => null;
                    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => null;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Both constructors must be public
        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            result.GeneratedCode, @"public RbTree\(");
        Assert.True(occurrences.Count >= 2,
            $"Expected at least 2 public constructors, got {occurrences.Count}.\nCode:\n{result.GeneratedCode}");
    }
}
