using System.Collections;
using System.Reflection;
using CSharpToJava.Core.IteratorLowering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.IteratorLowering;

/// <summary>
/// 回归：迭代器状态机展平后，`continue` 必须回到正确的循环继续点。
/// `for` 的继续点是自增段（不是条件），`do-while` 的继续点是条件判断（不是循环体头部）；
/// 跳错位置会让自增/条件被永久跳过，MoveNext 每次返回 true 且 Current 不变（死循环）。
/// GraphLayout 画像：RefinerBetweenTwoLayers.RightFromTheNode（for + continue + yield）。
/// </summary>
public sealed class IteratorControlFlowRuntimeTests
{
    private const int TimeoutMs = 10_000;

    [Fact]
    public void ForLoop_Continue_ExecutesIncrementors()
    {
        // 死循环画像：i == 0 时 continue 直接跳回条件，i 永不增加。
        var code = @"
using System.Collections.Generic;
namespace IteratorContinueFor {
class C {
    public IEnumerable<int> SkipEvens(int n) {
        for (int i = 0; i < n; i++) {
            if (i % 2 == 0) continue;
            yield return i;
        }
    }
}
}";
        var values = RunIterator(code, "SkipEvens", 4);
        Assert.Equal(new object[] { 1, 3 }, values);
    }

    [Fact]
    public void DoWhileLoop_Continue_EvaluatesConditionBeforeBody()
    {
        // do-while 的 continue 先判条件（此处为 false）再决定是否重跑循环体：
        // 跳回循环体头部会多执行一次循环体（产出 2）。
        var code = @"
using System.Collections.Generic;
namespace IteratorContinueDo {
class C {
    public IEnumerable<int> DoWhileContinue(bool again) {
        int i = 0;
        do {
            i++;
            if (i < 2) continue;
            yield return i;
        } while (again);
    }
}
}";
        var values = RunIterator(code, "DoWhileContinue", false);
        Assert.Empty(values);
    }

    [Fact]
    public void WhileLoop_Continue_StillSkipsToCondition()
    {
        var code = @"
using System.Collections.Generic;
namespace IteratorContinueWhile {
class C {
    public IEnumerable<int> WhileContinue(int n) {
        int i = 0;
        while (i < n) {
            i++;
            if (i == 2) continue;
            yield return i;
        }
    }
}
}";
        var values = RunIterator(code, "WhileContinue", 3);
        Assert.Equal(new object[] { 1, 3 }, values);
    }

    private static object?[] RunIterator(string source, string methodName, params object?[] args)
    {
        var assembly = LoadLowered(source);
        var type = assembly.GetType(assembly.GetTypes()
            .Single(t => t.Name == "C").FullName!, throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        var method = type.GetMethod(methodName)!;

        return RunWithTimeout(() =>
        {
            var enumerable = (IEnumerable)method.Invoke(instance, args)!;
            var values = new List<object?>();
            foreach (var value in enumerable)
            {
                values.Add(value);
            }
            return values.ToArray();
        });
    }

    private static object?[] RunWithTimeout(Func<object?[]> action)
    {
        object?[]? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        if (!thread.Join(TimeoutMs))
        {
            Assert.Fail(
                $"iterator enumeration did not terminate within {TimeoutMs} ms; " +
                "the generated state machine is looping in MoveNext.");
        }

        if (error != null)
        {
            throw new InvalidOperationException("iterator enumeration failed.", error);
        }

        return result!;
    }

    private static Assembly LoadLowered(string source)
    {
        var model = ParseWithSemantics(source);
        var preprocessor = new IteratorLoweringPreprocessor();
        var result = preprocessor.Preprocess(source, model, new IteratorLoweringOptions());
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == IteratorLoweringSeverity.Error);

        var tree = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions);
        var parseErrors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(parseErrors.Count == 0,
            "lowered output has parse errors: " + string.Join("; ", parseErrors));

        var compilation = CSharpCompilation.Create(
            "iterator-control-flow-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success, "emit failed: " + string.Join("; ", emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())));
        return Assembly.Load(pe.ToArray());
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static SemanticModel ParseWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "iterator-control-flow-input-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetSemanticModel(tree);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
