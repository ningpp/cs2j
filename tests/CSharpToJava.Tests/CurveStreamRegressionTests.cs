using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CurveStreamRegressionTests
{
    private static ConversionResult Convert(string code)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = code });
    }

    [Fact]
    public void StringSplit_RemoveEmptyEntries_DoesNotEmitStringSplitOptions()
    {
        const string code = """
            using System;

            class C
            {
                string[] M(string data)
                {
                    return data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Arrays.stream(data.split(", result.GeneratedCode);
        Assert.Contains(".filter(s -> !s.isEmpty())", result.GeneratedCode);
        Assert.DoesNotContain("StringSplitOptions", result.GeneratedCode);
        Assert.DoesNotContain("new char[]", result.GeneratedCode);
    }

    [Fact]
    public void DoubleTryParse_DoesNotQualifyMathHelperWithDouble()
    {
        const string code = """
            using System;

            class C
            {
                bool M(string s)
                {
                    double value;
                    return Double.TryParse(s, out value);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("MathHelper.tryParseDouble", result.GeneratedCode);
        Assert.DoesNotContain("Double.MathHelper.tryParseDouble", result.GeneratedCode);
    }

    [Fact]
    public void JsonDeserializeGeneric_EmitsClassTokenForTypedResult()
    {
        const string code = """
            using System.Text.Json;

            class P { public int X { get; set; } }

            class C
            {
                P M(string json)
                {
                    return JsonSerializer.Deserialize<P>(json);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("JsonSerializer.deserialize(json, P.class)", result.GeneratedCode);
        Assert.DoesNotContain("JsonSerializer.deserialize(json)", result.GeneratedCode);
    }

    [Fact]
    public void ListCtor_WithArrayArgument_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            class Item { }

            class Holder
            {
                public Item[] ItemsArray;
            }

            class C
            {
                List<Item> M(Holder holder)
                {
                    return new List<Item>(holder.ItemsArray);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new ArrayList<", result.GeneratedCode);
        Assert.Contains("Arrays.asList(holder.ItemsArray)", result.GeneratedCode);
        Assert.DoesNotContain("new ArrayList<>(holder.ItemsArray)", result.GeneratedCode);
    }
}
