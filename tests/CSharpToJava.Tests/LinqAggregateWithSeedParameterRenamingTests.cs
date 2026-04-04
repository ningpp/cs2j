using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class LinqAggregateWithSeedParameterRenamingTests
{
    [Fact]
    public void AggregateWithSeed_RenamesBothLambdaParameters()
    {
        var csharp = @"
using System.Linq;
public class MyClass
{
    public string JoinItems(int[] items)
    {
        return items.Aggregate("""", (s, t) => string.Format(""{0},{1}"", s, t));
    }
}";
        var result = ConvertProcedural(csharp);
        var java = result.GeneratedCode;

        // The accumulator 's' should become '_acc', the element 't' should become '_linqitem'
        Assert.Contains("_acc", java);
        Assert.DoesNotContain(" t)", java); // 't' should not remain as-is
        // Should not have undefined variable references
        Assert.Contains("_linqitem", java);
    }

    [Fact]
    public void AggregateWithSeed_AssignmentExpression_RenamesBothParameters()
    {
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class LinkedPoint
{
    public Point Point;
    public LinkedPoint Next;
    public LinkedPoint(Point p) { Point = p; }
}

public class Point { }

public class MyClass
{
    public LinkedPoint Build(IEnumerable<Point> pathPoints)
    {
        var ret = new LinkedPoint(pathPoints.First());
        pathPoints.Skip(1).Aggregate(ret, (lp, p) => lp.Next = new LinkedPoint(p));
        return ret;
    }
}";
        var result = ConvertProcedural(csharp);
        var java = result.GeneratedCode;

        // 'lp' (accumulator) → _acc, 'p' (element) → _linqitem
        Assert.Contains("_acc", java);
        // The body should use _acc.Next and new LinkedPoint(_linqitem)
        Assert.Contains("_acc.Next", java);
        Assert.Contains("new LinkedPoint(_linqitem)", java);
    }

    private static ConversionResult ConvertProcedural(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                EmitCompatibilityHelpers = false,
                PreferStreamApi = false,
            },
        });
    }
}
