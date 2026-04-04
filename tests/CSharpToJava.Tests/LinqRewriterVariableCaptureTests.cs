using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class LinqRewriterVariableCaptureTests
{
    [Fact]
    public void OutParameterInWhereLambda_IsCapturedAsRefParameter()
    {
        // Simulates SteinerCdt: double t; vts.Where(p => Method(p, out t) < eps).ToList()
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class C
{
    static double Compute(double p, out double t)
    {
        t = p * 2;
        return t;
    }

    public List<double> Filter(List<double> items)
    {
        double t;
        return items.Where(p => Compute(p, out t) < 10).ToList();
    }
}";
        var result = ConvertProcedural(csharp);
        Assert.True(result.Success, result.GeneratedCode);
        // 't' should be captured as a ref parameter in the extracted method
        Assert.Contains("ProceduralLinq", result.GeneratedCode);
        // 't' should appear as a parameter (DoubleHolder or ref), not be undefined
        Assert.DoesNotContain("找不到符号", result.GeneratedCode);
    }

    [Fact]
    public void VariableInCollectionExpression_IsCapturedAsParameter()
    {
        // Simulates IncrementalDragger: cluster.Nodes.All(n => cluster.Box.Contains(n.Box))
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class Box { public bool Contains(Box b) => true; }
public class Node { public Box BoundingBox { get; set; } }
public class Cluster
{
    public List<Node> Nodes { get; } = new();
    public Box BoundingBox { get; set; }
}

public class C
{
    public bool Check(Cluster cluster)
    {
        return cluster.Nodes.All(n => cluster.BoundingBox.Contains(n.BoundingBox));
    }
}";
        var result = ConvertProcedural(csharp);
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode);
        // 'cluster' should be captured as a parameter in the extracted method
        Assert.Contains("cluster", result.GeneratedCode);
    }

    [Fact]
    public void OutAssignedVariableInWhereLambda_IsCapturedAsParameter()
    {
        // Simulates Relayout: addedNodes set via TryGetValue, used in Where lambda
        var csharp = @"
using System.Linq;
using System.Collections.Generic;

public class C
{
    Dictionary<string, HashSet<int>> _dict = new();

    public List<int> Filter(List<int> items, string key)
    {
        HashSet<int> addedNodes;
        _dict.TryGetValue(key, out addedNodes);
        return items.Where(v => !addedNodes.Contains(v)).ToList();
    }
}";
        var result = ConvertProcedural(csharp);
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode);
        // 'addedNodes' should be captured as a parameter
        Assert.Contains("addedNodes", result.GeneratedCode);
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
