using CSharpToJava.Core.TupleToStruct;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Tupleprocessor;

/// <summary>
/// 元组 struct 的身份一致性回归。
///
/// 生成 struct 的名字曾按"元素类型结构"派生：同一个元组在开放站点写成
/// <c>(TKey, TValue)</c>，在具体站点写成 <c>(string, int)</c>，于是分别生成
/// <c>TupleT0T1&lt;...&gt;</c> 与 <c>TupleStringInt32</c> 两个不同的 C# 类型；
/// 库侧签名（泛型定义被实例化后）与调用侧因此无法互相赋值，编译器报
/// CS1503/CS0266（语料 System.Collections.Tests 的 PriorityQueue 家族）。
/// 同一逻辑元数还必须只有一种形状：<c>(TElement, TElement)</c>（重复类型参数）
/// 也不能退化成独立的 <c>TupleT0T0</c>。
///
/// 修复后每个逻辑元数对应唯一一个泛型 struct（<c>TupleT0T1&lt;__P0, __P1&gt;</c>），
/// 所有站点按位置把实际元素类型作为类型实参传入，开放/具体形态解析到同一类型。
/// </summary>
public sealed class TupleShapeIdentityTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private sealed record RunResult(string[] Outputs, string? StructFile);

    private static RunResult Run(params string[] sources)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "tuple-shape-identity",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new TupleToStructPreprocessor();
        var statistics = new TupleToStructStatistics();
        var outputs = new string[trees.Length];
        for (var i = 0; i < trees.Length; i++)
        {
            var fileDiagnostics = new List<TupleToStructDiagnostic>();
            var result = preprocessor.RewriteFile(
                sources[i], compilation.GetSemanticModel(trees[i]), statistics, fileDiagnostics);
            Assert.DoesNotContain(fileDiagnostics, d => d.Severity == TupleToStructSeverity.Error);
            outputs[i] = result.OutputCode;
        }

        return new RunResult(outputs, preprocessor.GenerateStructFile());
    }

    private static void AssertOutputCompiles(IEnumerable<string> sources, string? structFile)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))
            .ToList();
        if (structFile != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(structFile, ParseOptions));
        }

        var compilation = CSharpCompilation.Create(
            "tuple-shape-identity-verify",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            "Rewritten tuple code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- struct file ---\n" + structFile);
    }

    [Fact]
    public void OpenGenericAndClosedTuple_ResolveToSameStructDefinition()
    {
        // 库侧以泛型定义声明 `(TKey, TValue)`；调用侧用具体 `(string, int)` 构造集合并
        // 传给库。修复前两侧分别是 TupleT0T1<string,int> 与 TupleStringInt32 → CS1503。
        const string library = """
            using System.Collections.Generic;

            public sealed class Repository<TKey, TValue>
            {
                public void AddRange(IEnumerable<(TKey Key, TValue Value)> items)
                {
                    foreach (var (key, value) in items)
                    {
                        System.Console.WriteLine(key + ":" + value);
                    }
                }

                public IEnumerable<(TKey Key, TValue Value)> Items => null!;
            }
            """;

        const string consumer = """
            using System.Collections.Generic;

            public static class Consumer
            {
                public static void Run(Repository<string, int> repository)
                {
                    var literal = new List<(string, int)> { ("a", 1), ("b", 2) };
                    repository.AddRange(literal);

                    var named = new List<(string Key, int Value)> { ("c", 3) };
                    repository.AddRange(named);

                    IEnumerable<(string Other, int Count)> renamed = repository.Items;
                    System.Console.WriteLine(renamed);
                }
            }
            """;

        var result = Run(library, consumer);

        Assert.Contains("Cs2jTuples.TupleT0T1", result.Outputs[1]);
        Assert.DoesNotContain("TupleStringInt32", result.Outputs[1]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void RepeatedTypeParameterTuple_UsesUniformArityShape()
    {
        // `(e, e)` 的两个元素是同一个类型参数：不能因此生成 TupleT0T0<T>，否则与
        // `(TElement, TPriority)`（TElement = TPriority = T）实例化出的类型不同。
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public sealed class Queue<TElement, TPriority>
            {
                public Queue(IEnumerable<(TElement Element, TPriority Priority)> items) { }

                public void EnqueueRange(IEnumerable<(TElement Element, TPriority Priority)> items) { }

                public IEnumerable<(TElement Element, TPriority Priority)> UnorderedItems => null!;
            }

            public static class Sorter<T>
            {
                public static void Run(IEnumerable<T> inputs)
                {
                    var queue = new Queue<T, T>(inputs.Select(e => (e, e)));
                    queue.EnqueueRange(inputs.Select(e => (e, e)));

                    foreach ((T element, T priority) in queue.UnorderedItems)
                    {
                        Console.WriteLine(element + " " + priority);
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.Contains("Cs2jTuples.TupleT0T1<T, T>", result.Outputs[0]);
        Assert.DoesNotContain("TupleT0T0", result.Outputs[0]);
        Assert.DoesNotContain("TupleT0T0", result.StructFile ?? string.Empty);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void GeneratedStructFile_DefinesOneStructPerLogicalArity()
    {
        // 同一元数只允许一个定义：消除"开放形状 / 具体形状 / 重复参数形状"三套 struct。
        const string source = """
            using System;
            using System.Collections.Generic;

            public sealed class Pair<TFirst, TSecond>
            {
                public (TFirst, TSecond) Both(TFirst a, TSecond b) => (a, b);
            }

            public static class Use
            {
                public static void Run(Pair<string, int> pair)
                {
                    var one = pair.Both("a", 1);
                    (string, int) two = one;
                    var three = new List<(int, int)> { (1, 2) };
                    Console.WriteLine(two + " " + three.Count);
                }
            }
            """;

        var result = Run(source);

        var structFile = result.StructFile!;
        Assert.Contains("public struct TupleT0T1<", structFile);
        Assert.DoesNotContain("TupleStringInt32", structFile);
        Assert.DoesNotContain("TupleInt32Int32", structFile);
        Assert.Equal(1, CountOccurrences(structFile, "public struct TupleT0T1"));
        AssertOutputCompiles(result.Outputs, structFile);
    }

    [Fact]
    public void UnresolvedElementTypes_InstantiateViaInferredFactory()
    {
        // pass 把整棵树合并成一个编译单元，名字可能因此无法解析（缺引用，或 dotnet.*
        // 与 BCL 同名命名空间产生歧义）：out var / lambda 参数绑定成 error type，
        // 元组字面量的元素类型也随之不可知。把 error type 物化成 object 会写出
        // `TupleT0T1<object, object>`，而真实编译里的同一定义是
        // `TupleT0T1<TElement, TPriority>`，两者是不同的 C# 类型（CS1503/CS0266）。
        // 修复后改为调用生成的泛型工厂，由真正编译该文件的编译器按实参推断元素类型。
        const string source = """
            public sealed class Recorder<TElement, TPriority>
            {
                private readonly MissingList<(TElement Element, TPriority Priority)> _items = new();

                public void Record(MissingQueue<TElement, TPriority> queue)
                {
                    queue.TryPeek(out var element, out var priority);
                    _items.Add((element, priority));
                }
            }
            """;

        // 真实工程里这些类型当然能解析；验证编译时补上定义，模拟"pass 的合并编译看不到、
        // 真正编译能看到"的情形。
        const string missingTypes = """
            public sealed class MissingList<T>
            {
                public void Add(T item) { }
            }

            public sealed class MissingQueue<TElement, TPriority>
            {
                public bool TryPeek(out TElement element, out TPriority priority)
                {
                    element = default!;
                    priority = default!;
                    return true;
                }
            }
            """;

        var result = Run(source);

        Assert.Contains("TupleT0T1Factory.Create(element, priority)", result.Outputs[0]);
        Assert.DoesNotContain(
            "TupleT0T1<global::System.Object, global::System.Object>",
            result.Outputs[0]);
        AssertOutputCompiles(new[] { result.Outputs[0], missingTypes }, result.StructFile);
    }

    [Fact]
    public void GeneratedStructFile_ProvidesInferredFactoryPerShape()
    {
        const string source = """
            using System;

            public static class Use
            {
                public static void Run()
                {
                    var one = (1, "a");
                    Console.WriteLine(one);
                }
            }
            """;

        var result = Run(source);

        var structFile = result.StructFile!;
        Assert.Contains("public static class TupleT0T1Factory", structFile);
        Assert.Contains(
            "public static TupleT0T1<__P0, __P1> Create<__P0, __P1>(__P0 item1, __P1 item2)",
            structFile);
        AssertOutputCompiles(result.Outputs, structFile);
    }

    [Fact]
    public void Factory_ForLongTuple_BuildsNestedRestChain()
    {
        // 8+ 元组按 ValueTuple 的 Rest 链布局生成，工厂方法必须把扁平实参重新分组。
        const string source = """
            public static class Use
            {
                public static (int, int, int, int, int, int, int, int) Run()
                    => (1, 2, 3, 4, 5, 6, 7, 8);
            }
            """;

        var result = Run(source);

        var structFile = result.StructFile!;
        Assert.Contains(
            "Create<__P0, __P1, __P2, __P3, __P4, __P5, __P6, __P7>",
            structFile);
        Assert.Contains(
            "new TupleT0T1T2T3T4T5T6T7<__P0, __P1, __P2, __P3, __P4, __P5, __P6, __P7>" +
            "(item1, item2, item3, item4, item5, item6, item7, new TupleT0<__P7>(item8))",
            structFile);
        AssertOutputCompiles(result.Outputs, structFile);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
