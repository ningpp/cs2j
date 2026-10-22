using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regressions found while compiling the converted dotnet-uri SDK (iteration 3).
/// Each test converts a minimal repro through the project pipeline so the same
/// preprocessing passes used by the CLI run first.
/// </summary>
public class Iter3UriRegressionsTests
{
    private static async Task<string> ConvertAsync(string source)
    {
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[] { new SourceFile { FilePath = "Test.cs", Content = source } },
                projectName: "Demo");
        foreach (var failure in results.Where(r => !r.Success))
            throw new Xunit.Sdk.XunitException(
                string.Join("\n", failure.Diagnostics) + "\n---\n" + failure.GeneratedCode);
        return string.Join("\n", results.Select(r => r.GeneratedCode));
    }

    [Fact]
    public async Task AssemblyScopedResourceTypeReference_UsesRenamedClass()
    {
        var code = await ConvertAsync("""
            namespace dotnet.system
            {
                internal static class SR
                {
                    public static string Bad() => "bad";
                }

                public class User
                {
                    public string Get() => SR.Bad();
                }
            }
            """);

        Assert.Contains("class DemoSR", code);
        Assert.Contains("DemoSR.bad()", code);
        Assert.DoesNotContain("return SR.bad()", code);
    }

    [Fact]
    public async Task StringEmpty_RendersAsEmptyLiteral()
    {
        var code = await ConvertAsync("""
            public class Test
            {
                public string Text = string.Empty;
            }
            """);

        Assert.DoesNotContain("String.Empty", code);
        Assert.Contains("\"\"", code);
    }

    [Fact]
    public async Task NewTypeParameterArray_HasNoUndeclaredToken()
    {
        var code = await ConvertAsync("""
            public class Builder<T>
            {
                private T[] _items;

                public System.Type ElementType() => typeof(T);

                public void Ensure(int n)
                {
                    _items = new T[n];
                }
            }
            """);

        Assert.DoesNotContain("_cs2j_T", code);
        Assert.Contains("Object.class", code);
        Assert.Contains("(T[])", code);
    }

    [Fact]
    public async Task Struct_GetsPublicCloneAndCloneable()
    {
        var code = await ConvertAsync("""
            public struct Point
            {
                public int X;
                public int Y;

                public Point WithX(int x) => new Point { X = x, Y = Y };
            }
            """);

        Assert.Contains("implements Cloneable", code);
        Assert.Contains("public Point clone()", code);
        Assert.Contains("super.clone()", code);
    }

    [Fact]
    public async Task SerializableInterfaceAndMembers_AreSuppressed()
    {
        var code = await ConvertAsync("""
            using System.Runtime.Serialization;

            public class CustomException : System.Exception, ISerializable
            {
                public void GetObjectData(SerializationInfo info, StreamingContext context) { }
            }
            """);

        Assert.DoesNotContain("__suppress__", code);
        Assert.DoesNotContain("getObjectData", code);
    }

    [Fact]
    public async Task NestedClassAccessingOuterInstanceField_QualifiesWithThis()
    {
        var code = await ConvertAsync("""
            public class Outer
            {
                private int _value;

                private class Inner : Outer
                {
                    public void Set(int value)
                    {
                        _value = value;
                    }
                }
            }
            """);

        // The private field of the enclosing type is reached through a reference
        // typed as that enclosing type (nested classes may access its privates).
        Assert.Contains("((Outer) this)._value = value;", code);
    }

    [Fact]
    public async Task PrimitiveToStringWithFormatProvider_UsesWrapperToString()
    {
        var code = await ConvertAsync("""
            public class Test
            {
                public string Render(int value) =>
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            """);

        Assert.Contains("Integer.toString(value)", code);
        Assert.DoesNotContain("value.toString(", code);
    }

    [Fact]
    public async Task StringCreate_MapsToCompatCreateString()
    {
        var code = await ConvertAsync("""
            public class Test
            {
                private static void Write(System.Span<char> span, char state)
                {
                    span[0] = state;
                }

                public static string Make(char c) => string.Create(3, c, Write);
            }
            """);

        Assert.Contains("StringHelper.createString(", code);
    }

    [Fact]
    public async Task KeyValuePair_MapsToCompatType()
    {
        var code = await ConvertAsync("""
            using System.Collections.Generic;

            public class Test
            {
                public KeyValuePair<string, int> Pair() =>
                    new KeyValuePair<string, int>("a", 1);
            }
            """);

        Assert.Contains("CSharpKeyValuePair<String, Integer>", code);
        Assert.Contains("import io.github.ningpp.compat.CSharpKeyValuePair;", code);
    }

    [Fact]
    public async Task Dictionary_AndOutVariable_UseCompatDictionaryAndReadsValue()
    {
        var code = await ConvertAsync("""
            using System.Collections.Generic;

            public class Test
            {
                private static readonly Dictionary<string, string> s_resources =
                    new Dictionary<string, string> { { "a", "b" } };

                public static string Get(string name) =>
                    s_resources.TryGetValue(name, out string value) ? value : name;
            }
            """);

        Assert.Contains("CSharpDictionary<String, String>", code);
        Assert.Contains("import io.github.ningpp.compat.CSharpDictionary;", code);
        Assert.Contains("_valueHolder1.value", code);
    }

    [Fact]
    public async Task StringBuilderAppendFormat_MapsToCompatFormatter()
    {
        var code = await ConvertAsync("""
            using System.Globalization;
            using System.Text;

            public class Test
            {
                public static string Render(int value)
                {
                    var builder = new StringBuilder();
                    builder.AppendFormat(CultureInfo.InvariantCulture, "{0}", value);
                    return builder.ToString();
                }
            }
            """);

        Assert.Contains("StringHelper.formatCs(", code);
        Assert.DoesNotContain("appendFormat", code);
    }

    [Fact]
    public async Task StringBuilderAppendFormat_FormatFirstOverload_MapsToCompatFormatter()
    {
        var code = await ConvertAsync("""
            using System.Text;

            public class Test
            {
                public static string Render(int value)
                {
                    var builder = new StringBuilder();
                    builder.AppendFormat("{0:D6} ", value);
                    builder.AppendFormat("{0}{1}", value, value);
                    builder.AppendFormat("{0}");
                    return builder.ToString();
                }
            }
            """);

        Assert.Contains("StringHelper.formatCs(", code);
        Assert.DoesNotContain("appendFormat", code);
    }

    [Fact]
    public async Task ErasureCollidingOverloads_BothEmittedAndCallsBind()
    {
        var code = await ConvertAsync("""
            namespace Cs2jByRef
            {
                public struct ByRef<T>
                {
                    public T Value;
                }
            }

            public class Test
            {
                private static string Format(int start, int end, Cs2jByRef.ByRef<bool> a, Cs2jByRef.ByRef<bool> b) => null;

                private static string Format(int start, int end, Cs2jByRef.ByRef<bool> a, Cs2jByRef.ByRef<string> b) => null;

                public static string Run(Cs2jByRef.ByRef<bool> a, Cs2jByRef.ByRef<string> b) =>
                    Format(0, 1, a, b);
            }
            """);

        Assert.Contains("format_2(", code);
        Assert.Contains("format_2(0, 1, a, b)", code);
    }
}
