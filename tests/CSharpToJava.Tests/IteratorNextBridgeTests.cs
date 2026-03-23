using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for Iterator.next() bridge injection in PostProcessing.
/// Verifies that ApplyCompatibilityRewrites injects exactly ONE next() method
/// for IEnumerator&lt;T&gt; classes, without duplicates.
/// Also covers the CRLF line-ending normalization fix.
/// </summary>
public class IteratorNextBridgeTests
{
    private static ProjectConversionPipeline CreatePipeline() =>
        new(new ConversionOptions { TargetJavaVersion = JavaVersion.Java17 });

    private static async Task<string> ConvertSingleAsync(string csCode, string? contentFilter = null)
    {
        var pipeline = CreatePipeline();
        var files = new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = csCode }
        };
        var results = await pipeline.ConvertProjectAsync(files);
        // Filter to find the target class file (not auto-generated helpers)
        var main = contentFilter != null
            ? results.FirstOrDefault(r => r.GeneratedCode?.Contains(contentFilter) == true)
            : results.FirstOrDefault(r => r.FileName != null
                && !r.FileName.EndsWith("Helper.java") && !r.FileName.EndsWith("Holder.java")
                && !r.FileName.EndsWith("Exception.java") && !r.FileName.Contains("TextReader")
                && !r.FileName.Contains("ThreadHelper") && !string.IsNullOrEmpty(r.GeneratedCode));
        return main?.GeneratedCode ?? string.Empty;
    }

    /// <summary>
    /// A class implementing IEnumerator&lt;T&gt; via explicit interface Current property
    /// should produce exactly one next() method — not zero, not two.
    /// This is the core regression for the duplicate-injection bug.
    /// </summary>
    [Fact]
    public async Task ExplicitCurrentProperty_GeneratesExactlyOneNextMethod()
    {
        const string code = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            namespace N {
                public class Edge {}

                internal class IncEdgeEnumerator : IEnumerator<Edge> {
                    IEnumerator outEdges;
                    IEnumerator inEdges;
                    bool outIsActive;
                    bool inIsActive;

                    public void Dispose() {}

                    void IEnumerator.Reset() {
                        outEdges.Reset();
                        inEdges.Reset();
                    }

                    public bool MoveNext() {
                        outIsActive = outEdges.MoveNext();
                        if (!outIsActive)
                            inIsActive = inEdges.MoveNext();
                        return outIsActive || inIsActive;
                    }

                    Edge IEnumerator<Edge>.Current {
                        get {
                            if (outIsActive)
                                return outEdges.Current as Edge;
                            if (inIsActive)
                                return inEdges.Current as Edge;
                            throw new InvalidOperationException();
                        }
                    }

                    object IEnumerator.Current {
                        get {
                            if (outIsActive)
                                return outEdges.Current as Edge;
                            return null;
                        }
                    }
                }
            }
            """;

        var generated = await ConvertSingleAsync(code, "getCurrent");

        Assert.False(string.IsNullOrEmpty(generated), "Conversion produced no output");

        // At least one next() method (no missing injection)
        var nextDeclCount = System.Text.RegularExpressions.Regex.Matches(generated, @"public\s+\w+\s+next\s*\(\)").Count;
        Assert.True(nextDeclCount >= 1, $"Expected at least one next() method, found 0. Generated:\n{generated}");

        // At most one next() method declaration (no duplicates — the core regression)
        Assert.True(nextDeclCount <= 1, $"Expected at most one next() method, found {nextDeclCount}. Generated:\n{generated}");

        // next() must not appear inside getCurrent() body
        AssertNextIsOutsideGetCurrent(generated, "getCurrent", "next");

        // Current/as conversion must not evaluate nested iterators twice.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(generated, @"outEdges\.next\(\)").Cast<System.Text.RegularExpressions.Match>());
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(generated, @"inEdges\.next\(\)").Cast<System.Text.RegularExpressions.Match>());
    }

    /// <summary>
    /// A standard public Current property (EmptyEnumerator-style returning int)
    /// should produce exactly one Integer next() method.
    /// </summary>
    [Fact]
    public async Task PublicCurrentProperty_IntReturn_GeneratesExactlyOneNextMethod()
    {
        const string code = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            namespace N {
                public class EmptyEnumerator : IEnumerator<int> {
                    public void Dispose() {}
                    public void Reset() {}
                    public bool MoveNext() { return false; }
                    public int Current { get { return 0; } }
                    object IEnumerator.Current { get { return Current; } }
                }
            }
            """;

        var generated = await ConvertSingleAsync(code, "EmptyEnumerator");

        Assert.False(string.IsNullOrEmpty(generated), "Conversion produced no output");

        // Must have exactly one next() method
        var nextCount = System.Text.RegularExpressions.Regex.Matches(generated, @"public\s+\w+\s+next\s*\(\)").Count;
        Assert.Equal(1, nextCount);
    }

    /// <summary>
    /// Verifies that the CRLF normalization fix allows string-based replacements
    /// to correctly inject next() even when generated code had CRLF endings.
    /// </summary>
    [Fact]
    public async Task NormalizationFix_NoStrayNextInsideGetCurrentBody()
    {
        const string code = """
            using System;
            using System.Collections;
            using System.Collections.Generic;

            namespace N {
                public class SimpleEnumerator : IEnumerator<int> {
                    int[] _items;
                    int _index = -1;
                    public void Dispose() {}
                    public void Reset() { _index = -1; }
                    public bool MoveNext() { return ++_index < _items.Length; }
                    public int Current { get { return _items[_index]; } }
                    object IEnumerator.Current { get { return Current; } }
                }
            }
            """;

        var generated = await ConvertSingleAsync(code, "SimpleEnumerator");

        Assert.False(string.IsNullOrEmpty(generated), "Conversion produced no output");

        // next() must not appear INSIDE getCurrent() body
        var getterIdx = generated.IndexOf("getCurrent()", StringComparison.Ordinal);
        if (getterIdx >= 0)
        {
            // Find the closing brace of getCurrent()
            int braceDepth = 0;
            int bodyStart = generated.IndexOf('{', getterIdx);
            int bodyEnd = -1;
            for (int i = bodyStart; i < generated.Length; i++)
            {
                if (generated[i] == '{') braceDepth++;
                else if (generated[i] == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0) { bodyEnd = i; break; }
                }
            }

            if (bodyEnd >= 0)
            {
                var insideBody = generated.Substring(bodyStart, bodyEnd - bodyStart);
                Assert.DoesNotContain("public Integer next()", insideBody);
                Assert.DoesNotContain("public int next()", insideBody);
            }
        }
    }

    [Fact]
    public async Task IEnumeratorMoveNext_Declaration_RemainsMoveNext_WithIteratorBridge()
    {
        const string code = """
            using System.Collections;
            using System.Collections.Generic;

            namespace N {
                public class PolylineIterator : IEnumerator<int> {
                    int[] _items = new[] { 1, 2 };
                    int _index = -1;

                    public int Current => _items[_index];
                    object IEnumerator.Current => Current;
                    public void Dispose() {}
                    public bool MoveNext() {
                        _index++;
                        return _index < _items.Length;
                    }
                    public void Reset() { _index = -1; }
                }
            }
            """;

        var generated = await ConvertSingleAsync(code, "PolylineIterator");

        Assert.Contains("public boolean moveNext()", generated);
        Assert.Contains("public boolean hasNext()", generated);
        Assert.Contains("_iteratorHasNext = moveNext()", generated);
        Assert.DoesNotContain("public boolean hasNext() {\n        _index++;", generated);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private static void AssertNextIsOutsideGetCurrent(string code, string getterName, string nextName)
    {
        // Match method DECLARATIONS (not calls like outEdges.next())
        var getterDeclMatch = System.Text.RegularExpressions.Regex.Match(
            code, $@"public\s+\S+\s+{getterName}\s*\(\)");
        var nextDeclMatch = System.Text.RegularExpressions.Regex.Match(
            code, $@"public\s+\S+\s+{nextName}\s*\(\)");

        Assert.True(getterDeclMatch.Success, $"'{getterName}()' method declaration not found");
        Assert.True(nextDeclMatch.Success, $"'{nextName}()' method declaration not found");

        int getterIdx = getterDeclMatch.Index;
        int nextIdx = nextDeclMatch.Index;

        // Find the closing brace of getCurrent() by counting braces
        int braceDepth = 0;
        int methodBodyStart = code.IndexOf('{', getterIdx);
        int methodBodyEnd = -1;
        for (int i = methodBodyStart; i < code.Length; i++)
        {
            if (code[i] == '{') braceDepth++;
            else if (code[i] == '}')
            {
                braceDepth--;
                if (braceDepth == 0) { methodBodyEnd = i; break; }
            }
        }

        Assert.True(methodBodyEnd >= 0, "Could not find closing brace of getCurrent()");
        Assert.True(nextIdx < methodBodyStart || nextIdx > methodBodyEnd,
            $"next() at index {nextIdx} appears INSIDE getCurrent() body ({methodBodyStart}-{methodBodyEnd})");
    }
}