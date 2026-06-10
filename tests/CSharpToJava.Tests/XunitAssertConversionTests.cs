using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

public class XunitAssertConversionTests
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
    public void Xunit_Assert_True_converts_to_csharp_xunit_Assert_true_()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test1()
                {
                    Assert.True(true);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("csharp.xunit.Assert", result.GeneratedCode);
        Assert.Contains("true_(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_False_converts_to_csharp_xunit_Assert_false_()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test2()
                {
                    Assert.False(false);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("false_(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Null_converts_to_csharp_xunit_Assert_null_()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test3()
                {
                    Assert.Null(null);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("null_(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_NotNull_converts_to_csharp_xunit_Assert_notNull()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test4()
                {
                    Assert.NotNull("hello");
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("notNull(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Equal_converts_to_csharp_xunit_Assert_equal()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test5()
                {
                    Assert.Equal(1, 1);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("equal(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_NotEqual_converts_to_csharp_xunit_Assert_notEqual()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test6()
                {
                    Assert.NotEqual(1, 2);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("notEqual(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Same_converts_to_csharp_xunit_Assert_same()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test7()
                {
                    var obj = new object();
                    Assert.Same(obj, obj);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("same(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Contains_string_converts_to_csharp_xunit_Assert_contains()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test8()
                {
                    Assert.Contains("hello", "hello world");
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("contains(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Empty_converts_to_csharp_xunit_Assert_empty()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test9()
                {
                    Assert.Empty(new int[0]);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("empty(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Throws_converts_to_csharp_xunit_Assert_throws_()
    {
        var result = Convert("""
            using Xunit;
            using System;

            class MyTests
            {
                [Fact]
                public void Test10()
                {
                    Assert.Throws<ArgumentException>(() => { });
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("throws_(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Throws_includes_type_arg_as_class_literal()
    {
        var result = Convert("""
            using Xunit;
            using System;

            class MyTests
            {
                [Fact]
                public void TestThrowsTypeArg()
                {
                    Assert.Throws<ArgumentException>(() => { });
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The generic type argument <ArgumentException> must be converted to
        // ArgumentException.class (mapped) as the first argument of throws_().
        Assert.Contains("ArgumentException.class", result.GeneratedCode);
        Assert.Contains("throws_(ArgumentException.class", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Throws_ArgumentNullException_maps_to_compat_class()
    {
        var result = Convert("""
            using Xunit;
            using System;

            class MyTests
            {
                [Fact]
                public void TestThrowsArgumentNullException()
                {
                    Assert.Throws<ArgumentNullException>(() => { });
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // ArgumentNullException should map to compat class, not NullPointerException,
        // so it satisfies the <T extends ArgumentException> constraint in throws_().
        Assert.Contains("ArgumentNullException.class", result.GeneratedCode);
        Assert.Contains("throws_(ArgumentNullException.class", result.GeneratedCode);
        Assert.DoesNotContain("NullPointerException.class", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_ThrowsAny_includes_type_arg_as_class_literal()
    {
        var result = Convert("""
            using Xunit;
            using System;

            class MyTests
            {
                [Fact]
                public void TestThrowsAnyTypeArg()
                {
                    Assert.ThrowsAny<Exception>(() => { });
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("RuntimeException.class", result.GeneratedCode);
        Assert.Contains("throwsAny(RuntimeException.class", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_IsType_converts_to_csharp_xunit_Assert_isType()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test11()
                {
                    Assert.IsType<string>("hello");
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("isType(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_InRange_converts_to_csharp_xunit_Assert_inRange()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test12()
                {
                    Assert.InRange(5, 1, 10);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("inRange(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_StartsWith_converts_to_csharp_xunit_Assert_startsWith()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test13()
                {
                    Assert.StartsWith("hello", "hello world");
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("startsWith(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Fail_converts_to_csharp_xunit_Assert_fail()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test14()
                {
                    Assert.Fail("something went wrong");
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("fail(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Single_converts_to_csharp_xunit_Assert_single()
    {
        var result = Convert("""
            using Xunit;
            using System.Collections.Generic;

            class MyTests
            {
                [Fact]
                public void Test15()
                {
                    Assert.Single(new List<int> { 42 });
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("single(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_Assert_Distinct_converts_to_csharp_xunit_Assert_distinct()
    {
        var result = Convert("""
            using Xunit;
            using System.Collections.Generic;

            class MyTests
            {
                [Fact]
                public void Test16()
                {
                    Assert.Distinct(new List<int> { 1, 2, 3 });
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("distinct(", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_import_is_csharp_xunit_Assert()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test17()
                {
                    Assert.True(true);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import csharp.xunit.Assert;", result.GeneratedCode);
    }

    [Fact]
    public void Xunit_FactAttribute_converts_to_JUnit5_Test()
    {
        var result = Convert("""
            using Xunit;

            class MyTests
            {
                [Fact]
                public void Test18()
                {
                    Assert.True(true);
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // [Fact] should be converted to @Test (JUnit 5)
        Assert.Contains("@Test", result.GeneratedCode);
        Assert.Contains("org.junit.jupiter.api.Test", result.GeneratedCode);
    }
}
