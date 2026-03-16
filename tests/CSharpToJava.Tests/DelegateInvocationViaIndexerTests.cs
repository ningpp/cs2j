using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for delegate invocation via dictionary/indexer lookup, method group references,
/// and Array.ForEach conversion.
/// </summary>
public class DelegateInvocationViaIndexerTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Bug 1: Delegate invocation via dictionary indexer ───────────────────
    // dict[key](args) must emit dict.get(key).accept(args) / .apply(args) / .run()

    [Fact]
    public void ActionDelegate_InvokedViaDictionary_EmitsAccept()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            class Demo
            {
                void Run()
                {
                    var invokers = new Dictionary<bool, Action<string>>();
                    invokers[true]("hello");
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains(".accept(", result.GeneratedCode);
        Assert.DoesNotContain(".get(true)(", result.GeneratedCode);
    }

    [Fact]
    public void FuncDelegate_InvokedViaDictionary_EmitsApply()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            class Demo
            {
                void Run()
                {
                    var solvers = new Dictionary<string, Func<int, double>>();
                    double result = solvers["cg"](42);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains(".apply(", result.GeneratedCode);
        Assert.DoesNotContain(".get(\"cg\")(", result.GeneratedCode);
    }

    [Fact]
    public void ActionNoArgs_InvokedViaDictionary_EmitsRun()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            class Demo
            {
                void Run()
                {
                    var actions = new Dictionary<int, Action>();
                    actions[0]();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains(".run()", result.GeneratedCode);
        Assert.DoesNotContain(".get(0)()", result.GeneratedCode);
    }

    // ── Bug 2: Method group references ─────────────────────────────────────
    // C# method group used as a value should emit Java method reference (::)

    [Fact]
    public void MethodGroup_AsArgument_EmitsMethodReference()
    {
        const string code = """
            using System;
            class Demo
            {
                static void Process(int x) { }
                void Run()
                {
                    Action<int> a = Process;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Method group should become a method reference, not a bare identifier
        Assert.DoesNotContain("= Process;", result.GeneratedCode);
    }

    // ── Bug 3: Array.ForEach ───────────────────────────────────────────────
    // Array.ForEach(arr, action) must NOT produce Object.forEach(...)

    [Fact]
    public void ArrayForEach_DoesNotEmitObjectForEach()
    {
        const string code = """
            using System;
            class Demo
            {
                void Run()
                {
                    Action[] actions = new Action[0];
                    Array.ForEach(actions, a => a());
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("Object.forEach", result.GeneratedCode);
    }
}
