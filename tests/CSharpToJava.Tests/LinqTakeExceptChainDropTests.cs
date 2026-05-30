using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the Take+Except chain translation bug found in z5's denseClusteringNoEdges test.
/// 
/// Original C# (from E:\agl-master\GraphLayout\Test\MSAGLTests\InitialLayoutTests.cs):
///   Cluster outerCluster = CreateCluster(graph.Nodes.Take(4).Except(innerCluster.Nodes), Margin);
/// 
/// z5 buggy output:
///   Cluster outerCluster = createCluster(denseClusteringNoEdges_ProceduralLinq2(graph.getNodes(), innerCluster, graph, 4), Margin);
///   // innerCluster parameter is passed but NEVER USED — Except clause is silently dropped!
/// 
/// Root cause: When LinqRewriter processes a Take().Except() chain, the Except operation
/// is dropped, producing Java code that only performs Take without the set difference.
/// </summary>
public class LinqTakeExceptChainDropTests
{
    [Fact]
    public void Take_Except_ToList_ProducesProceduralWithExceptLogic()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    List<int> M(List<int> items, List<int> excluded) {
        return items.Take(4).Except(excluded).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractAllProceduralLinqMethods(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("HashSet") || proceduralMethod.Contains("contains") || proceduralMethod.Contains("!"),
            $"The ProceduralLinq method should contain the Except set-difference logic (HashSet/contains/negation). " +
            $"Got:\n{proceduralMethod}");
    }

    [Fact]
    public void Take_Except_WithPropertyAccess_ToList_ProducesProceduralWithExceptLogic()
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
        return nodes.Take(4).Except(innerCluster.Nodes).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("innerCluster", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractAllProceduralLinqMethods(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("HashSet") || proceduralMethod.Contains("contains") || proceduralMethod.Contains("!"),
            $"The ProceduralLinq method should contain the Except set-difference logic. " +
            $"Got:\n{proceduralMethod}");
    }

    [Fact]
    public void Take_Except_WithPropertyAccess_ExceptNotDroppedFromStatistics()
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
        return nodes.Take(4).Except(innerCluster.Nodes).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        var exceptOps = result.LinqStatistics.EncounteredOperators
            .Where(o => o.MethodFullName.Contains("Except")).ToList();
        Assert.True(exceptOps.Count > 0,
            $"Except should be tracked in EncounteredOperators. " +
            $"All operators: {string.Join(", ", result.LinqStatistics.EncounteredOperators.Select(o => $"{o.MethodFullName}(rewritten={o.WasRewritten})"))}");
        Assert.True(exceptOps.Any(o => o.WasRewritten),
            $"Except should be marked as rewritten in the Take+Except chain. " +
            $"EncounteredOperators: {string.Join(", ", result.LinqStatistics.EncounteredOperators.Select(o => $"{o.MethodFullName}(rewritten={o.WasRewritten})"))}");
    }

    [Fact]
    public void Take_Except_WithPropertyAccess_NoRuleExpansionSkips()
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
        return nodes.Take(4).Except(innerCluster.Nodes).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        var ruleExpansionSkips = result.LinqStatistics.SkippedChains
            .Where(s => s.Reason == LinqSkipReason.RuleExpansionFailed).ToList();
        Assert.Empty(ruleExpansionSkips);
    }

    [Fact]
    public void Take_Except_Standalone_ProducesProceduralWithExceptLogic()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    IEnumerable<int> M(List<int> items, List<int> excluded) {
        return items.Take(4).Except(excluded);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractAllProceduralLinqMethods(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("HashSet") || proceduralMethod.Contains("contains") || proceduralMethod.Contains("!"),
            $"The ProceduralLinq method should contain the Except set-difference logic. " +
            $"Got:\n{proceduralMethod}");
    }

    [Fact]
    public void Take_Except_AsTerminal_ProducesProceduralWithConcatSecondParam()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    IEnumerable<int> M(List<int> items, List<int> excluded) {
        return items.Take(4).Except(excluded);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractAllProceduralLinqMethods(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("HashSet") || proceduralMethod.Contains("contains") || proceduralMethod.Contains("!"),
            $"The ProceduralLinq method should contain the Except set-difference logic. " +
            $"Got:\n{proceduralMethod}");
        Assert.Contains("_concatSecond_0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_Intersect_AsTerminal_ProducesProceduralWithConcatSecondParam()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    IEnumerable<int> M(List<int> items, List<int> other) {
        return items.Take(4).Intersect(other);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractAllProceduralLinqMethods(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("HashSet") || proceduralMethod.Contains("contains") || proceduralMethod.Contains("Remove"),
            $"The ProceduralLinq method should contain the Intersect set-intersection logic. " +
            $"Got:\n{proceduralMethod}");
        Assert.Contains("_concatSecond_0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_Except_WithPropertyAccess_AsTerminal_ProducesProceduralWithExceptLogic()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

public class Node {}
public class Cluster {
    public List<Node> Nodes { get; } = new();
}

class C {
    IEnumerable<Node> M(List<Node> nodes, Cluster innerCluster) {
        return nodes.Take(4).Except(innerCluster.Nodes);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("innerCluster", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractAllProceduralLinqMethods(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("HashSet") || proceduralMethod.Contains("contains") || proceduralMethod.Contains("!"),
            $"The ProceduralLinq method should contain the Except set-difference logic. " +
            $"Got:\n{proceduralMethod}");
    }

    [Fact]
    public void Except_Take_OrderPreserved_ProducesProcedural()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

class C {
    List<int> M(List<int> items, List<int> excluded) {
        return items.Except(excluded).Take(4).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("ProceduralLinq", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_Except_PassedToConstructor_ProducesProceduralWithExceptLogic()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;

public class Node {}
public class Cluster {
    public Cluster(IEnumerable<Node> nodes) {}
    public List<Node> Nodes { get; } = new();
}

class C {
    Cluster M(List<Node> nodes, Cluster innerCluster) {
        return new Cluster(nodes.Take(4).Except(innerCluster.Nodes));
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("innerCluster", result.GeneratedCode, StringComparison.Ordinal);
        var proceduralMethod = ExtractAllProceduralLinqMethods(result.GeneratedCode);
        Assert.True(proceduralMethod.Contains("HashSet") || proceduralMethod.Contains("contains") || proceduralMethod.Contains("!"),
            $"The ProceduralLinq method should contain the Except set-difference logic. " +
            $"Got:\n{proceduralMethod}");
    }

    private static string ExtractAllProceduralLinqMethods(string code)
    {
        var result = new System.Text.StringBuilder();
        int searchStart = 0;
        while (searchStart < code.Length)
        {
            var startMarker = "ProceduralLinq";
            var idx = code.IndexOf(startMarker, searchStart, StringComparison.Ordinal);
            if (idx < 0) break;
            var braceStart = code.IndexOf('{', idx);
            if (braceStart < 0) break;
            var depth = 1;
            var end = braceStart + 1;
            while (end < code.Length && depth > 0)
            {
                if (code[end] == '{') depth++;
                else if (code[end] == '}') depth--;
                end++;
            }
            result.AppendLine(code[idx..end]);
            searchStart = end;
        }
        return result.ToString();
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
