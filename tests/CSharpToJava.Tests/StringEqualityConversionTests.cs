using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StringEqualityConversionTests
{
    private static ConversionResult Convert(string code)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = code });
    }

    [Fact]
    public void StringEqualityOperator_UsesObjectsEquals()
    {
        const string code = """
            class C
            {
                bool Same(string? x, string? y)
                {
                    return x == y;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return Objects.equals(x, y);", result.GeneratedCode);
        Assert.DoesNotContain("x.equals(y)", result.GeneratedCode);
    }

    [Fact]
    public void StringInequalityOperator_UsesNegatedObjectsEquals()
    {
        const string code = """
            class C
            {
                bool Different(string? x, string? y)
                {
                    return x != y;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return !Objects.equals(x, y);", result.GeneratedCode);
    }

    [Fact]
    public void StringLiteralEquality_IsNullSafeRegardlessOfOperandOrder()
    {
        const string code = """
            class C
            {
                bool Left(string? x)
                {
                    return x == "a";
                }

                bool Right(string? x)
                {
                    return "a" == x;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return Objects.equals(x, \"a\");", result.GeneratedCode);
        Assert.Contains("return Objects.equals(\"a\", x);", result.GeneratedCode);
    }

    [Fact]
    public void StringEqualsInvocation_UsesObjectsEquals()
    {
        const string code = """
            class C
            {
                bool Same(string? x, string? y)
                {
                    return x.Equals(y);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return Objects.equals(x, y);", result.GeneratedCode);
        Assert.DoesNotContain("x.equals(y)", result.GeneratedCode);
    }

    [Fact]
    public void StaticStringEquals_UsesObjectsEquals()
    {
        const string code = """
            using System;

            class C
            {
                bool Same(string? x, string? y)
                {
                    return String.Equals(x, y);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return Objects.equals(x, y);", result.GeneratedCode);
    }

    [Fact]
    public void StringEquals_WithStringComparison_UsesStringHelper()
    {
        const string code = """
            using System;

            class C
            {
                bool A(string x, string y)
                {
                    return x.Equals(y, StringComparison.OrdinalIgnoreCase);
                }

                bool B(string x, string y)
                {
                    return String.Equals(x, y, StringComparison.Ordinal);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return StringHelper.equals(x, y, true);", result.GeneratedCode);
        Assert.Contains("return StringHelper.equals(x, y, false);", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.OrdinalIgnoreCase", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.Ordinal", result.GeneratedCode);
    }

    [Fact]
    public void StartsWithAndEndsWith_WithStringComparison_UseStringHelper()
    {
        const string code = """
            using System;

            class C
            {
                bool A(string x)
                {
                    return x.StartsWith("ab", StringComparison.OrdinalIgnoreCase);
                }

                bool B(string x)
                {
                    return x.EndsWith("ab", StringComparison.Ordinal);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return StringHelper.startsWith(x, \"ab\", true);", result.GeneratedCode);
        Assert.Contains("return StringHelper.endsWith(x, \"ab\", false);", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.OrdinalIgnoreCase", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.Ordinal", result.GeneratedCode);
    }

    [Fact]
    public void StringCompare_WithStringComparison_UsesStringHelperBooleanOverload()
    {
        const string code = """
            using System;

            class C
            {
                int CompareA(string x, string y)
                {
                    return String.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                }

                int CompareB(string x, string y)
                {
                    return String.Compare(x, y, StringComparison.Ordinal);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return StringHelper.compare(x, y, true);", result.GeneratedCode);
        Assert.Contains("return StringHelper.compare(x, y, false);", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.OrdinalIgnoreCase", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.Ordinal", result.GeneratedCode);
    }

    [Fact]
    public void StringContains_WithStringComparison_UsesStringHelper()
    {
        const string code = """
            using System;

            class C
            {
                bool A(string x)
                {
                    return x.Contains("ab", StringComparison.OrdinalIgnoreCase);
                }

                bool B(string x)
                {
                    return x.Contains("ab", StringComparison.Ordinal);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return StringHelper.contains(x, \"ab\", true);", result.GeneratedCode);
        Assert.Contains("return StringHelper.contains(x, \"ab\", false);", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.OrdinalIgnoreCase", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.Ordinal", result.GeneratedCode);
    }

    [Fact]
    public void StringIndexOfAndLastIndexOf_WithStringComparison_UseStringHelper()
    {
        const string code = """
            using System;

            class C
            {
                int A(string x)
                {
                    return x.IndexOf("ab", StringComparison.OrdinalIgnoreCase);
                }

                int B(string x)
                {
                    return x.IndexOf("ab", 2, StringComparison.Ordinal);
                }

                int C(string x)
                {
                    return x.LastIndexOf("ab", StringComparison.OrdinalIgnoreCase);
                }

                int D(string x)
                {
                    return x.LastIndexOf("ab", 5, StringComparison.Ordinal);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return StringHelper.indexOf(x, \"ab\", true);", result.GeneratedCode);
        Assert.Contains("return StringHelper.indexOf(x, \"ab\", 2, false);", result.GeneratedCode);
        Assert.Contains("return StringHelper.lastIndexOf(x, \"ab\", true);", result.GeneratedCode);
        Assert.Contains("return StringHelper.lastIndexOf(x, \"ab\", 5, false);", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.OrdinalIgnoreCase", result.GeneratedCode);
        Assert.DoesNotContain("StringComparison.Ordinal", result.GeneratedCode);
    }

    [Fact]
    public void StringNullComparisons_RemainDirectNullChecks()
    {
        const string code = """
            class C
            {
                bool IsNull(string? x)
                {
                    return x == null;
                }

                bool IsNotNull(string? x)
                {
                    return null != x;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return x == null;", result.GeneratedCode);
        Assert.Contains("return null != x;", result.GeneratedCode);
        Assert.DoesNotContain("Objects.equals(x, null)", result.GeneratedCode);
    }
}