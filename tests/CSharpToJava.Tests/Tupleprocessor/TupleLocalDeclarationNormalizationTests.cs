using CSharpToJava.Core.TupleToStruct;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.Tupleprocessor;

/// <summary>
/// 局部声明元组类型归一化（NORM-TUPLE-LOCAL）：
/// tuple pass 为了保留 <c>typeof(ValueTuple&lt;...&gt;)</c> 观察到的运行时身份，会刻意
/// 保留原生 System.ValueTuple 语法。<c>(T1, T2) v = ...</c> 这种局部声明因此仍带着
/// <see cref="TupleTypeSyntax"/>，触发 Java 侧 "deconstruction local declaration" 门禁。
/// 后处理把它改写为 <c>var v = ...</c>（无初始化器时 <c>var v = default((T1, T2));</c>），
/// 推断出的类型仍是 ValueTuple，语义与反射身份不变。
/// </summary>
public sealed class TupleLocalDeclarationNormalizationTests
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

    private sealed record RunResult(
        string[] Outputs,
        string? StructFile,
        TupleToStructStatistics Statistics);

    private static RunResult Run(params string[] sources)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "tuple-local-normalization",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new TupleToStructPreprocessor();
        var statistics = new TupleToStructStatistics();
        var diagnostics = new List<TupleToStructDiagnostic>();

        // 与 CLI 适配器一致：先扫描整个编译单元的 typeof(ValueTuple<...>)，再逐文件改写。
        var runtimeObservedArities = new HashSet<int>();
        foreach (var tree in trees)
        {
            runtimeObservedArities.UnionWith(
                TupleToStructPreprocessor.FindRuntimeObservedTupleArities(tree.GetRoot()));
        }

        preprocessor.ObserveRuntimeValueTupleArities(runtimeObservedArities);

        var outputs = new string[trees.Length];
        for (var i = 0; i < trees.Length; i++)
        {
            var fileDiagnostics = new List<TupleToStructDiagnostic>();
            var result = preprocessor.RewriteFile(
                sources[i], compilation.GetSemanticModel(trees[i]), statistics, fileDiagnostics);
            Assert.NotEmpty(result.OutputCode);
            Assert.DoesNotContain(
                fileDiagnostics,
                d => d.Severity == TupleToStructSeverity.Error);
            outputs[i] = result.OutputCode;
        }

        return new RunResult(outputs, preprocessor.GenerateStructFile(), statistics);
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
            "tuple-local-normalization-verify",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            "Rewritten tuple code failed to compile:\n" + string.Join("\n", errors));
    }

    /// <summary>断言输出中不存在类型为 TupleTypeSyntax 的局部声明（NORM-TUPLE-LOCAL 残留为 0）。</summary>
    private static void AssertNoTupleTypedLocalDeclaration(string code)
    {
        var tupleLocals = CSharpSyntaxTree.ParseText(code)
            .GetRoot()
            .DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Where(local => local.Declaration.Type is TupleTypeSyntax)
            .ToList();
        Assert.Empty(tupleLocals);
    }

    [Fact]
    public void LocalWithoutInitializer_BecomesVarWithDefaultTuple()
    {
        // typeof(ValueTuple<,>) 让 arity 2 的形状保持原生 ValueTuple 语法；局部声明
        // `(T1, T2) v = default;` 必须变成 `var v = default((T1, T2));`。
        var source = """
            using System;

            class G<T1, T2>
            {
                static readonly Type Observed = typeof(System.ValueTuple<,>);

                void M()
                {
                    (T1, T2) v = default;
                    _ = v;
                }
            }
            """;

        var result = Run(source);

        Assert.Contains("var v = default((T1, T2))", result.Outputs[0]);
        Assert.DoesNotContain("(T1, T2) v", result.Outputs[0]);
        Assert.Equal(1, result.Statistics.TupleLocalsNormalizedToVar);
        AssertNoTupleTypedLocalDeclaration(result.Outputs[0]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void LocalWithInitializer_BecomesPlainVar()
    {
        var source = """
            using System;

            class C
            {
                static readonly Type Observed = typeof(System.ValueTuple<,>);

                (int, int) Get() => default;

                void M()
                {
                    (int, int) v = Get();
                    Console.WriteLine(v.Item1);
                }
            }
            """;

        var result = Run(source);

        Assert.Contains("var v = Get();", result.Outputs[0]);
        Assert.DoesNotContain("(int, int) v", result.Outputs[0]);
        Assert.Equal(1, result.Statistics.TupleLocalsNormalizedToVar);
        AssertNoTupleTypedLocalDeclaration(result.Outputs[0]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void NamedTupleElements_AreLeftUnchanged()
    {
        // default((T1 a, T2 b)) 不是合法表达式：带名元素的局部声明必须原样保留。
        var source = """
            using System;

            class G<T1, T2>
            {
                static readonly Type Observed = typeof(System.ValueTuple<,>);

                void M()
                {
                    (T1 a, T2 b) v = default;
                    _ = v;
                }
            }
            """;

        var result = Run(source);

        Assert.Equal(source, result.Outputs[0]);
        Assert.Equal(0, result.Statistics.TupleLocalsNormalizedToVar);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void MultipleDeclarators_AreLeftUnchanged()
    {
        // 拆分多变量声明会改变求值顺序/作用域，保持原样。
        var source = """
            using System;

            class C
            {
                static readonly Type Observed = typeof(System.ValueTuple<,>);

                void M()
                {
                    (int, int) a = (1, 2), b = (3, 4);
                    Console.WriteLine(a.Item1 + b.Item1);
                }
            }
            """;

        var result = Run(source);

        Assert.Equal(source, result.Outputs[0]);
        Assert.Equal(0, result.Statistics.TupleLocalsNormalizedToVar);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }

    [Fact]
    public void SecondRun_IsIdempotent()
    {
        var source = """
            using System;

            class G<T1, T2>
            {
                static readonly Type Observed = typeof(System.ValueTuple<,>);

                void M()
                {
                    (T1, T2) v = default;
                    _ = v;
                }
            }
            """;

        var first = Run(source);
        var second = Run(first.Outputs[0]);

        Assert.Equal(first.Outputs[0], second.Outputs[0]);
        Assert.Equal(0, second.Statistics.TupleLocalsNormalizedToVar);
        AssertNoTupleTypedLocalDeclaration(second.Outputs[0]);
        AssertOutputCompiles(second.Outputs, second.StructFile);
    }

    [Fact]
    public void OrdinaryVarAndCs2jTuplesDeclarations_AreNotAffected()
    {
        // 没有 runtime-identity 观察时，元组被正常降级为 Cs2jTuples struct；
        // `var` 局部声明与 struct 类型引用都不应被归一化步骤触碰。
        var source = """
            class C
            {
                (int A, int B) Field;

                void M()
                {
                    var v = (1, 2);
                    v.Item1 = 5;
                    global::Cs2jTuples.TupleInt32Int32 same = v;
                    System.Console.WriteLine(same.Item2);
                }
            }
            """;

        var result = Run(source);

        Assert.Contains("var v = new global::Cs2jTuples.TupleInt32Int32(", result.Outputs[0]);
        Assert.Contains("global::Cs2jTuples.TupleInt32Int32 same", result.Outputs[0]);
        Assert.Equal(0, result.Statistics.TupleLocalsNormalizedToVar);
        AssertNoTupleTypedLocalDeclaration(result.Outputs[0]);
        AssertOutputCompiles(result.Outputs, result.StructFile);
    }
}
