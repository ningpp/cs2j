using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Issue #3: Generic constraint and variance diagnostic reporting.
/// Verifies that unsupported constraints emit CS2J1002 and variance emits CS2J1003.
/// </summary>
public class GenericConstraintDiagnosticsTests
{
    [Fact]
    public void WhereT_New_EmitsCS2J1002()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Factory<T> where T : new()
{
    T Create() { return new T(); }
}",
            FileName = "Factory.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "CS2J1002" && d.Category == "GenericConstraint"
            && d.Message.Contains("new()"));
    }

    [Fact]
    public void WhereT_Struct_EmitsCS2J1002()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Wrapper<T> where T : struct
{
    T value;
}",
            FileName = "Wrapper.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "CS2J1002" && d.Category == "GenericConstraint"
            && d.Message.Contains("struct"));
    }

    [Fact]
    public void WhereT_TypeBound_NoCS2J1002()
    {
        // Type bounds like `where T : IComparable` should be mapped, not flagged
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System;

class Sorter<T> where T : IComparable<T>
{
    T value;
}",
            FileName = "Sorter.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1002");
        // T should have extends bound
        Assert.Contains("extends", result.GeneratedCode);
    }

    [Fact]
    public void CovariantInterface_EmitsCS2J1003()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
interface IProducer<out T>
{
    T Produce();
}",
            FileName = "IProducer.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "CS2J1003" && d.Category == "GenericVariance"
            && d.Message.Contains("out T"));
    }

    [Fact]
    public void ContravariantInterface_EmitsCS2J1003()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
interface IConsumer<in T>
{
    void Consume(T item);
}",
            FileName = "IConsumer.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "CS2J1003" && d.Category == "GenericVariance"
            && d.Message.Contains("in T"));
    }

    [Fact]
    public void NonVariantInterface_NoCS2J1003()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
interface IMapper<T>
{
    T Map(T input);
}",
            FileName = "IMapper.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS2J1003");
    }

    [Fact]
    public void DelegateWithVariance_EmitsCS2J1003()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
delegate TResult Converter<in TInput, out TResult>(TInput input);
",
            FileName = "Converter.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
        var varianceDiags = result.Diagnostics
            .Where(d => d.Code == "CS2J1003" && d.Category == "GenericVariance")
            .ToList();
        Assert.True(varianceDiags.Count >= 2,
            $"Expected >=2 CS2J1003 diagnostics for in+out, got {varianceDiags.Count}");
    }
}
