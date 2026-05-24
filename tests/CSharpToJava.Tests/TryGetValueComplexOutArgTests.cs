using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using System;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for TryGetValue with non-simple out arguments (array elements, member access, etc.).
/// Root cause: InvocationExpressionTransformer's TryGetValue handler required IsSimpleIdentifier
/// for the out argument.  When the out arg was e.g. Result[i], it fell through to the generic
/// invocation path which passed the out holder as a second argument to Java Map.get(), producing
/// invalid code like d.get(v, _outArgHolder2).
/// </summary>
public class TryGetValueComplexOutArgTests
{
    [Fact]
    public void TryGetValue_ArrayElement_NegatedIf_UsesContainsKey()
    {
        // C#: if (!d.TryGetValue(v, out Result[i])) Result[i] = double.PositiveInfinity;
        // Java should use containsKey pattern that avoids null auto-unboxing NPE
        var result = Convert(@"
using System.Collections.Generic;
class Node { }
class Sample {
    double[] Result = new double[10];
    void M(Dictionary<Node, double> d) {
        int i = 0;
        foreach (Node v in d.Keys) {
            if (!d.TryGetValue(v, out Result[i]))
                Result[i] = double.PositiveInfinity;
            i++;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Must NOT have invalid get(v, holder) with two args
        Assert.DoesNotContain("get(v, _", result.GeneratedCode, StringComparison.Ordinal);
        // Should use containsKey pattern
        Assert.Contains("containsKey(", result.GeneratedCode, StringComparison.Ordinal);
        // Should have the else branch assigning from get
        Assert.Contains(".get(v)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValue_PredeclaredVar_PositiveIf_VariableAssignedBeforeUse()
    {
        // Regression test for issue #31: when TryGetValue with a pre-declared local
        // variable appears as a positive (non-negated) if condition, and the variable
        // is used after the if block, the generated Java must assign the variable before
        // the if so it is definitely assigned at the return statement.
        var result = Convert(@"
using System.Collections.Generic;
class ObstaclePort { public string Id; }
class Port { }
class Sample {
    private Dictionary<Port, ObstaclePort> obstaclePortMap;
    public ObstaclePort FindObstaclePort(Port port) {
        ObstaclePort oport;
        if (obstaclePortMap.TryGetValue(port, out oport)) {
            oport.Id = ""pp"";
        }
        return oport;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // The get() call must happen BEFORE the if to guarantee definite assignment
        var code = result.GeneratedCode;
        Assert.Contains("= obstaclePortMap.get(port)", code, StringComparison.Ordinal);
        // The containsKey check must remain to preserve semantics when map stores null values
        Assert.Contains("containsKey(port)", code, StringComparison.Ordinal);
        // The assignment must appear before the if statement
        var getIdx = code.IndexOf("= obstaclePortMap.get(port)", StringComparison.Ordinal);
        var ifIdx = code.IndexOf("if (obstaclePortMap.containsKey(port))", StringComparison.Ordinal);
        Assert.True(getIdx < ifIdx, $"get() must come before the if. get at {getIdx}, if at {ifIdx}.\nCode:\n{code}");
    }

    [Fact]
    public void TryGetValue_PredeclaredVar_PositiveIf_WithElse()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    private Dictionary<string, string> dict;
    public string M(string key, string fallback) {
        string value;
        if (dict.TryGetValue(key, out value)) {
            return value.ToUpper();
        } else {
            return fallback;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        // get() before if, with containsKey check
        Assert.Contains("= dict.get(key)", code, StringComparison.Ordinal);
        Assert.Contains("containsKey(key)", code, StringComparison.Ordinal);
        // else branch must be preserved
        Assert.Contains("else", code, StringComparison.Ordinal);
        Assert.Contains("return fallback", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValue_SimpleVar_StillWorks()
    {
        // Ensure existing behavior for simple out variables is not broken
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    void M(Dictionary<string, string> d, string key) {
        if (!d.TryGetValue(key, out var value))
            value = ""default"";
        System.Console.WriteLine(value);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Should use the var = get(key) pattern
        Assert.Contains("= d.get(key)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValue_OutIntParam_PositiveIf_GetInsideIfBlock()
    {
        // Regression test: when TryGetValue with an out int parameter (primitive holder)
        // appears as a positive if condition, the get() must go INSIDE the if block
        // so containsKey guards it. Putting get() before the if would auto-unbox
        // null Integer to int, causing NPE for missing keys.
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    public static bool TryGet(Dictionary<int, int> d, int key, out int value) {
        if (d.TryGetValue(key, out value))
            return true;
        value = -1;
        return false;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        // get() must appear inside the if block (after containsKey), not before it
        Assert.Contains("containsKey(", code, StringComparison.Ordinal);
        Assert.Contains(".get(", code, StringComparison.Ordinal);
        // The get() assignment must NOT precede the if
        var getIdx = code.IndexOf(".get(", StringComparison.Ordinal);
        var ifIdx = code.IndexOf("if (", code.IndexOf("containsKey(", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(ifIdx < getIdx,
            $"get() must be after if. if at {ifIdx}, get at {getIdx}.\nCode:\n{code}");
        // The assignment must target .value (holder field)
        Assert.Contains(".value =", code, StringComparison.Ordinal);
        // Must compile to valid Java: no get() before containsKey
        var firstGet = code.IndexOf(".get(", StringComparison.Ordinal);
        var firstContains = code.IndexOf("containsKey(", StringComparison.Ordinal);
        Assert.True(firstContains < firstGet,
            $"containsKey must precede get. containsKey at {firstContains}, get at {firstGet}.\nCode:\n{code}");
    }

    [Fact]
    public void TryGetValue_OutLongParam_PositiveIf_GetInsideIfBlock()
    {
        // Same as above but for long (another primitive that would NPE on unboxing null)
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    public static bool TryGet(Dictionary<string, long> d, string key, out long value) {
        if (d.TryGetValue(key, out value))
            return true;
        value = -1L;
        return false;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        var firstGet = code.IndexOf(".get(", StringComparison.Ordinal);
        var firstContains = code.IndexOf("containsKey(", StringComparison.Ordinal);
        Assert.True(firstContains < firstGet,
            $"containsKey must precede get. containsKey at {firstContains}, get at {firstGet}.\nCode:\n{code}");
    }

    [Fact]
    public void TryGetValue_OutReferenceType_PositiveIf_GetBeforeIfForDefiniteAssignment()
    {
        // Reference types keep the old behavior: get() before if for Java definite assignment.
        // get() returns null safely for reference types (no auto-unboxing).
        var result = Convert(@"
using System.Collections.Generic;
class ObstaclePort { public string Id; }
class Port { }
class Sample {
    public ObstaclePort Find(Dictionary<Port, ObstaclePort> map, Port key) {
        ObstaclePort oport;
        if (map.TryGetValue(key, out oport)) {
            oport.Id = ""found"";
        }
        return oport;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        // get() before if for definite assignment of reference types
        var getIdx = code.IndexOf("= map.get(", StringComparison.Ordinal);
        var ifIdx = code.IndexOf("if (map.containsKey(", StringComparison.Ordinal);
        Assert.True(getIdx < ifIdx,
            $"For reference types, get() must precede if for definite assignment. get at {getIdx}, if at {ifIdx}.\nCode:\n{code}");
        Assert.Contains("containsKey(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValue_ExpressionContext_UsesContainsKeyFirst()
    {
        // TryGetValue used as an expression (not in if condition) should use
        // containsKey && get() order to avoid NPE for primitive holders.
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    int M(Dictionary<string, int> d) {
        int value;
        bool found = d.TryGetValue(""a"", out value);
        return found ? value : 0;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        // Must use containsKey in the expression, not get() != null || containsKey()
        Assert.Contains("containsKey(", code, StringComparison.Ordinal);
        // Must NOT use the old pattern: get() != null || containsKey()
        Assert.DoesNotContain("!= null ||", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValue_NegatedOutIntParam_UsesContainsKeyGuard()
    {
        // Negated TryGetValue with out int PARAMETER (not local) should use the
        // containsKey-guarded pattern since the param is a primitive holder.
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    public static bool TryInit(Dictionary<string, int> d, string key, out int value) {
        if (!d.TryGetValue(key, out value)) {
            value = 42;
            return false;
        }
        return true;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        Assert.Contains("containsKey(", code, StringComparison.Ordinal);
        Assert.Contains(".get(", code, StringComparison.Ordinal);
        // The get() must happen inside the else block (key exists), not before the if
        var negIf = code.IndexOf("if (!", StringComparison.Ordinal);
        var getAfterNegIf = code.IndexOf(".get(", negIf, StringComparison.Ordinal);
        Assert.True(getAfterNegIf > negIf,
            $"get() should not precede the negated if. if at {negIf}, get at {getAfterNegIf}.\nCode:\n{code}");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
