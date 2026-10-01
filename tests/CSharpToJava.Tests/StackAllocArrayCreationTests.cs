using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Diagnostics;

namespace CSharpToJava.Tests;

public class StackAllocArrayCreationTests
{

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

    // ==================================================================
    // M2 迁移用例（设计 §7.1 R4 / §9 M2）：stackalloc 经 unsafe-ffm
    // 预处理归一为堆数组，纯 C# 断言，不依赖本机 javac（环境 JDK 不可用）。
    // ==================================================================

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "stackalloc-migration",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static void AssertOutputCompiles(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var compilation = CSharpCompilation.Create("stackalloc-verify",
            new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    [Fact]
    public void M2_ExplicitStackAlloc_PreprocessesToHeapArray_AndCompiles()
    {
        var src = """
            using System;
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc int[8];
                    Console.Write(x[0]);
                }
            }
            """;

        var model = ParseWithSemantics(src);
        var result = new UnsafeFfmPreprocessor(new UnsafeFfmOptions()).RewriteFile(src, model);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] x = new int[8];", result.OutputCode);
        Assert.DoesNotContain("stackalloc", result.OutputCode);
        Assert.Equal(1, result.Statistics.ExplicitStackAllocCollected);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_ExplicitStackAllocWithInitializer_PreprocessesToHeapArray_AndCompiles()
    {
        var src = """
            using System;
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc int[] { 1, 2, 3 };
                    Console.Write(x[2]);
                }
            }
            """;

        var model = ParseWithSemantics(src);
        var result = new UnsafeFfmPreprocessor(new UnsafeFfmOptions()).RewriteFile(src, model);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] x = new int[] { 1, 2, 3 };", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_ImplicitStackAlloc_PreprocessesToHeapArray_TriggerFaceEliminated()
    {
        var src = """
            using System;
            unsafe class C
            {
                static void M()
                {
                    Span<int> x = stackalloc[] { 1, 2 };
                    Console.Write(x[1]);
                }
            }
            """;

        var model = ParseWithSemantics(src);
        var result = new UnsafeFfmPreprocessor(new UnsafeFfmOptions()).RewriteFile(src, model);

        Assert.True(result.Changed);
        Assert.False(result.HasErrors);
        Assert.Contains("int[] x = new int[] { 1, 2 };", result.OutputCode);
        Assert.Equal(1, result.Statistics.ImplicitStackAllocCollected);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void M2_DirectConversion_ExplicitStackAlloc_StillFailsFast_SafetyNetNotWeakened()
    {
        var src = """
            unsafe class C
            {
                static void M()
                {
                    System.Span<int> x = stackalloc int[8];
                    System.Console.Write(x[0]);
                }
            }
            """;

        var result = Convert(src);

        // 直连转换器安全网保留：显式 stackalloc 必须阻断转换（fail-fast），
        // 不允许静默产出。
        Assert.False(result.Success);
        var allText = result.GeneratedCode + string.Join("\n", result.Diagnostics.Select(d => d.Message));
        Assert.Contains("stackalloc", allText);
    }

    [Fact]
    public void M2_DirectConversion_ImplicitStackAlloc_CurrentSilentHeapBehaviorDocumented()
    {
        // 现网行为锁定（设计 §10 R-静默）：未跑预处理时，隐式 stackalloc
        // 被转换器静默堆化。M2 后启用 pass 的输入不再产生该构造；此处断言
        // 现有输出不弱化（仍显式发成堆数组，而非原样透传）。
        var src = """
            unsafe class C
            {
                static void M()
                {
                    System.Span<int> x = stackalloc[] { 1, 2, 3 };
                    System.Console.Write(x[0]);
                }
            }
            """;

        var result = Convert(src);

        // 现状：成功，文本为 compat Span 持有堆数组（静默堆化，且依赖 compat
        // 包；待 M4 评估退役）。断言的关键事实：未原样透传 stackalloc，
        // 确实发成了堆数组。
        Assert.True(result.Success,
            "implicit stackalloc direct conversion is currently a silent heap allocation; " +
            "if it now fails, update the expectation explicitly rather than leaving the behavior ambiguous.");
        Assert.Contains("new int[]", result.GeneratedCode);
        Assert.Contains("1, 2, 3", result.GeneratedCode);
        Assert.DoesNotContain("stackalloc", result.GeneratedCode);
    }

    // ==================================================================
    private static void CompileJavaWithCompat(string code)
    {
        using var temp = new TempDir();
        var samplePath = Path.Combine(temp.Path, "C.java");
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(samplePath, code);

        var compatDir = Path.Combine(FindRepoRoot(), "java", "csharptojava-compat", "src", "main", "java", "io", "github", "ningpp", "compat");
        var compatSources = new[]
        {
            "ReadOnlySpan.java",
            "Span.java",
        }.Select(name => Path.Combine(compatDir, name)).ToArray();

        foreach (var source in compatSources)
            Assert.True(File.Exists(source), $"Missing compat source: {source}");

        var sources = string.Join(" ", compatSources.Append(samplePath).Select(path => $"\"{path}\""));
        var javac = RunProcess("javac", $"-d \"{outDir}\" {sources}");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx"))
                && Directory.Exists(Path.Combine(dir, "java", "csharptojava-compat")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
