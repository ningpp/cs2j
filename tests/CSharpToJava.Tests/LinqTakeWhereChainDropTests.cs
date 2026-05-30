using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the Take+Where chain translation bug found in z5's denseClusteringNoEdges test.
/// Root cause: When LinqRewriter fails to process a Take().Where() chain (e.g. due to
/// closure capture in the Where lambda), the entire chain is skipped, and the inner Take
/// is rewritten independently via the fallback path, while the Where clause is silently
/// dropped. This results in incorrect Java code that only performs Take without the
/// Where filter.
/// </summary>
public class LinqTakeWhereChainDropTests
{
    [Fact]
    public void Take_Where_WithClosureCapture_ProducesProceduralWithFilter()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

public class Node {}
public class Cluster {
    public List<Node> Nodes { get; } = new();
}

class C {
    List<Node> M(List<Node> nodes, Cluster innerCluster) {
        return nodes.Take(4).Where(x => !innerCluster.Nodes.Contains(x)).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("innerCluster", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_Where_WithClosureCapture_GeneratedCodeContainsFilterLogic()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

public class Node {}
public class Cluster {
    public List<Node> Nodes { get; } = new();
}

class C {
    List<Node> M(List<Node> nodes, Cluster innerCluster) {
        return nodes.Take(4).Where(x => !innerCluster.Nodes.Contains(x)).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractProceduralLinqMethod(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("!") || proceduralMethod.Contains("contains"),
            $"The ProceduralLinq method should contain the Where filter logic (negation or contains check). " +
            $"Got:\n{proceduralMethod}");
    }

    [Fact]
    public void Take_Where_WithClosureCapture_WhereNotDroppedFromStatistics()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

public class Node {}
public class Cluster {
    public List<Node> Nodes { get; } = new();
}

class C {
    List<Node> M(List<Node> nodes, Cluster innerCluster) {
        return nodes.Take(4).Where(x => !innerCluster.Nodes.Contains(x)).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        var whereOps = result.LinqStatistics.EncounteredOperators
            .Where(o => o.MethodFullName.Contains("Where")).ToList();
        Assert.True(whereOps.Count > 0, "Where should be tracked in EncounteredOperators");
        Assert.True(whereOps.Any(o => o.WasRewritten),
            $"Where should be marked as rewritten in the Take+Where chain. " +
            $"EncounteredOperators: {string.Join(", ", result.LinqStatistics.EncounteredOperators.Select(o => $"{o.MethodFullName}(rewritten={o.WasRewritten})"))}");
    }

    [Fact]
    public void Take_Where_SimplePredicate_ProducesProceduralWithFilter()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    List<int> M(List<int> items) {
        return items.Take(4).Where(x => x > 2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractProceduralLinqMethod(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains(">"),
            $"The ProceduralLinq method should contain the Where filter logic (>). " +
            $"Got:\n{proceduralMethod}");
    }

    [Fact]
    public void Take_Where_WithHashSetContainsClosure_ProducesProceduralWithFilter()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    List<int> M(List<int> items, HashSet<int> excluded) {
        return items.Take(4).Where(x => !excluded.Contains(x)).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("excluded", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractProceduralLinqMethod(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("!") || proceduralMethod.Contains("contains"),
            $"The ProceduralLinq method should contain the Where filter logic. " +
            $"Got:\n{proceduralMethod}");
    }

    [Fact]
    public void Where_Take_OrderPreserved_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    List<int> M(List<int> items) {
        return items.Where(x => x > 0).Take(4).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_Where_WithClosureCapture_NoSkippedChains()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

public class Node {}
public class Cluster {
    public List<Node> Nodes { get; } = new();
}

class C {
    List<Node> M(List<Node> nodes, Cluster innerCluster) {
        return nodes.Take(4).Where(x => !innerCluster.Nodes.Contains(x)).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        var ruleExpansionSkips = result.LinqStatistics.SkippedChains
            .Where(s => s.Reason == LinqSkipReason.RuleExpansionFailed).ToList();
        Assert.Empty(ruleExpansionSkips);
    }

    private static string ExtractProceduralLinqMethod(string code)
    {
        var startMarker = "ProceduralLinq";
        var idx = code.IndexOf(startMarker, StringComparison.Ordinal);
        if (idx < 0) return "";
        var braceStart = code.IndexOf('{', idx);
        if (braceStart < 0) return "";
        var depth = 1;
        var end = braceStart + 1;
        while (end < code.Length && depth > 0)
        {
            if (code[end] == '{') depth++;
            else if (code[end] == '}') depth--;
            end++;
        }
        return code[idx..end];
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
                PreferStreamApi = false,
            },
        });
    }
}
