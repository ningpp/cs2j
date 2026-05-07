using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests;

public class Phase2PassPipelineTests
{
    [Fact]
    public void Cs2jPassExecutor_RecordsPassOrderAndDiagnostics()
    {
        var context = new ConversionContext(new ConversionOptions(), new TypeMappingRegistry(new TypeMappingConfig()));
        var state = new List<string>();
        var metrics = new List<Cs2jPassMetric>();

        Cs2jPassExecutor.Execute(
            state,
            context,
            new ICs2jPass<List<string>>[]
            {
                new TestListPass("DesugarListPass", Cs2jPassStage.Desugar, items => items.Add("desugar")),
                new TestListPass("CheckListPass", Cs2jPassStage.Check, items =>
                {
                    items.Add("check");
                    context.Diagnostics.Warning("check warning");
                }),
                new TestListPass("EmitListPass", Cs2jPassStage.Emit, items => items.Add("emit")),
            },
            metrics);

        Assert.Equal(new[] { "desugar", "check", "emit" }, state);
        Assert.Collection(
            metrics,
            metric =>
            {
                Assert.Equal("DesugarListPass", metric.Name);
                Assert.Equal(Cs2jPassStage.Desugar, metric.Stage);
                Assert.Equal(0, metric.DiagnosticDelta);
                Assert.Equal(0, metric.RewriteCount);
                Assert.True(metric.ManagedMemoryBytesBefore >= 0);
                Assert.True(metric.ManagedMemoryBytesAfter >= 0);
                Assert.Equal(metric.ManagedMemoryBytesAfter - metric.ManagedMemoryBytesBefore, metric.ManagedMemoryDelta);
            },
            metric =>
            {
                Assert.Equal("CheckListPass", metric.Name);
                Assert.Equal(Cs2jPassStage.Check, metric.Stage);
                Assert.Equal(1, metric.DiagnosticDelta);
                Assert.Equal(0, metric.RewriteCount);
                Assert.True(metric.ManagedMemoryBytesBefore >= 0);
                Assert.True(metric.ManagedMemoryBytesAfter >= 0);
                Assert.Equal(metric.ManagedMemoryBytesAfter - metric.ManagedMemoryBytesBefore, metric.ManagedMemoryDelta);
            },
            metric =>
            {
                Assert.Equal("EmitListPass", metric.Name);
                Assert.Equal(Cs2jPassStage.Emit, metric.Stage);
                Assert.Equal(0, metric.DiagnosticDelta);
                Assert.Equal(0, metric.RewriteCount);
                Assert.True(metric.ManagedMemoryBytesBefore >= 0);
                Assert.True(metric.ManagedMemoryBytesAfter >= 0);
                Assert.Equal(metric.ManagedMemoryBytesAfter - metric.ManagedMemoryBytesBefore, metric.ManagedMemoryDelta);
            });
    }

    [Fact]
    public void ConversionPipeline_ExposesSingleFilePassMetrics()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = "class Sample { }",
            FileName = "Sample.cs",
            Options = CreateOptions(),
        });

        Assert.True(result.Success);
        Assert.Equal(
            new[]
            {
                "SingleFileLinqDesugarPass",
                "SingleFileCompilationCheckPass",
                "SingleFileUnsupportedDomainCheckPass",
                "SingleFilePlatformBoundaryCheckPass",
                "SingleFileNativeInteropCheckPass",
                "SingleFileContextNormalizationPass",
                "SingleFileJavaEmitPass",
            },
            result.PassMetrics.Select(metric => metric.Name).ToArray());
    }

    [Fact]
    public void ConversionPipeline_FailsUnsupportedDomainCheck_ForWinFormsInput()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = "using System.Windows.Forms; class Sample : Form { }",
            FileName = "Sample.cs",
            Options = CreateOptions(),
        });

        Assert.False(result.Success);
        Assert.Empty(result.GeneratedCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CS2J3001" && diagnostic.Category == "unsupported-domain");
        Assert.Contains("SingleFileUnsupportedDomainCheckPass", result.PassMetrics.Select(metric => metric.Name));
    }

    [Fact]
    public void ConversionPipeline_FailsPlatformBoundaryCheck_ForOperatingSystemProbe()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = "using System; class Sample { bool IsWindows() { return OperatingSystem.IsWindows(); } }",
            FileName = "Sample.cs",
            Options = CreateOptions(),
        });

        Assert.False(result.Success);
        Assert.Empty(result.GeneratedCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CS2J3102" && diagnostic.Category == "platform-boundary");
        Assert.Contains("SingleFilePlatformBoundaryCheckPass", result.PassMetrics.Select(metric => metric.Name));
    }

    [Fact]
    public void ConversionPipeline_ReportsRewriteCount_ForSingleFileLinqDesugarPass()
    {
        var pipeline = new ConversionPipeline();
        var options = CreateOptions();
        options.PreferStreamApi = false;

        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = "using System.Collections.Generic; using System.Linq; class Sample { bool HasPositive(List<int> values) { return values.Any(v => v > 0); } }",
            FileName = "Sample.cs",
            Options = options,
        });

        Assert.True(result.Success);
        var metric = Assert.Single(result.PassMetrics, item => item.Name == "SingleFileLinqDesugarPass");
        Assert.True(metric.RewriteCount > 0);
    }

    [Fact]
    public async Task ProjectConversionPipeline_ExposesProjectPassMetrics()
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = "class Sample { }",
            }
        });

        var primaryResult = Assert.Single(results, result => result.FileName == "Sample.java");
        Assert.Equal(
            new[]
            {
                "ProjectLinqDesugarPass",
                "ProjectCompilationCheckPass",
                "ProjectUnsupportedDomainCheckPass",
                "ProjectPlatformBoundaryCheckPass",
                "ProjectNativeInteropCheckPass",
                "ProjectExtensionMethodCheckPass",
                "ProjectPartialTypeNormalizationPass",
                "ProjectTypeEmitPass",
                "ProjectCompatibilityEmitPass",
                "ProjectCrossPackageImportEmitPass",
                "ProjectJavaModuleDependencyPass",
            },
            primaryResult.PassMetrics.Select(metric => metric.Name).ToArray());
    }

    [Fact]
    public async Task ProjectConversionPipeline_RewritesLinqBeforeEmit_WhenStreamApiDisabled()
    {
        var options = CreateOptions();
        options.PreferStreamApi = false;

        var pipeline = new ProjectConversionPipeline(options);
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = "using System.Collections.Generic; using System.Linq; class Sample { bool HasPositive(List<int> values) { return values.Any(v => v > 0); } }",
            }
        });

        var primaryResult = Assert.Single(results, result => result.FileName == "Sample.java");
        Assert.True(primaryResult.Success);
        Assert.Contains("ProjectLinqDesugarPass", primaryResult.PassMetrics.Select(metric => metric.Name));
        Assert.NotEmpty(pipeline.LastPassMetrics);
        Assert.True(Assert.Single(primaryResult.PassMetrics, metric => metric.Name == "ProjectLinqDesugarPass").RewriteCount > 0);
        Assert.DoesNotContain(".stream()", primaryResult.GeneratedCode);
    }

    [Fact]
    public async Task ProjectConversionPipeline_BlocksUnsupportedDomainFiles_WithFailureResult()
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Unsupported.cs",
                Content = "using System.Windows.Forms; class Unsupported : Form { }",
            }
        });

        var failureResult = Assert.Single(results);
        Assert.False(failureResult.Success);
        Assert.Empty(failureResult.GeneratedCode);
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Code == "CS2J3001" && diagnostic.Category == "unsupported-domain");
        Assert.Contains("ProjectUnsupportedDomainCheckPass", failureResult.PassMetrics.Select(metric => metric.Name));
    }

    [Fact]
    public async Task ProjectConversionPipeline_BlocksPlatformBoundaryFiles_WithFailureResult()
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Platform.cs",
                Content = "using System; class Sample { bool IsWindows() { return OperatingSystem.IsWindows(); } }",
            }
        });

        var failureResult = Assert.Single(results);
        Assert.False(failureResult.Success);
        Assert.Empty(failureResult.GeneratedCode);
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Code == "CS2J3102" && diagnostic.Category == "platform-boundary");
        Assert.Contains("ProjectPlatformBoundaryCheckPass", failureResult.PassMetrics.Select(metric => metric.Name));
    }

    [Fact]
    public async Task ProjectConversionPipeline_BlocksNativeInteropFiles_WithFailureResult()
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Interop.cs",
                Content = "using System.Runtime.InteropServices; class NativeMethods { [DllImport(\"kernel32.dll\")] private static extern bool Beep(uint frequency, uint duration); }",
            }
        });

        var failureResult = Assert.Single(results);
        Assert.False(failureResult.Success);
        Assert.Empty(failureResult.GeneratedCode);
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Code == "CS2J3201" && diagnostic.Category == "native-interop");
        Assert.Contains("ProjectNativeInteropCheckPass", failureResult.PassMetrics.Select(metric => metric.Name));
    }

    [Fact]
    public async Task ProjectConversionPipeline_MergesBlockingDiagnosticsAcrossBoundaryPasses()
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Mixed.cs",
                Content = "using System; using System.Runtime.InteropServices; class Sample { [DllImport(\"kernel32.dll\")] private static extern bool Beep(uint frequency, uint duration); bool IsWindows() { return OperatingSystem.IsWindows(); } }",
            }
        });

        var failureResult = Assert.Single(results);
        Assert.False(failureResult.Success);
        Assert.Empty(failureResult.GeneratedCode);
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Code == "CS2J3102");
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Code == "CS2J3201");
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Code == "CS2J3203");
    }

    [Fact]
    public async Task ProjectConversionPipeline_ParallelTreeLocalPasses_PreserveSequentialResults()
    {
        var sequentialOptions = CreateOptions();
        sequentialOptions.PreferStreamApi = false;
        sequentialOptions.EnableParallelProjectPasses = false;

        var parallelOptions = CreateOptions();
        parallelOptions.PreferStreamApi = false;
        parallelOptions.EnableParallelProjectPasses = true;

        var sourceFiles = new[]
        {
            new SourceFile
            {
                FilePath = "Linq.cs",
                Content = "using System.Collections.Generic; using System.Linq; class Sample { bool HasPositive(List<int> values) { return values.Any(v => v > 0); } }",
            },
            new SourceFile
            {
                FilePath = "Mixed.cs",
                Content = "using System; using System.Runtime.InteropServices; class Sample { [DllImport(\"kernel32.dll\")] private static extern bool Beep(uint frequency, uint duration); bool IsWindows() { return OperatingSystem.IsWindows(); } }",
            }
        };

        var sequentialResults = await new ProjectConversionPipeline(sequentialOptions).ConvertProjectAsync(sourceFiles);
        var parallelPipeline = new ProjectConversionPipeline(parallelOptions);
        var parallelResults = await parallelPipeline.ConvertProjectAsync(sourceFiles);

        var sequentialSummary = sequentialResults
            .OrderBy(result => result.FileName, StringComparer.Ordinal)
            .Select(result => new
            {
                result.FileName,
                result.Success,
                result.GeneratedCode,
                Diagnostics = result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}|{diagnostic.Category}|{diagnostic.Message}").ToArray(),
            })
            .ToArray();

        var parallelSummary = parallelResults
            .OrderBy(result => result.FileName, StringComparer.Ordinal)
            .Select(result => new
            {
                result.FileName,
                result.Success,
                result.GeneratedCode,
                Diagnostics = result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}|{diagnostic.Category}|{diagnostic.Message}").ToArray(),
            })
            .ToArray();

        Assert.Equal(sequentialSummary.Length, parallelSummary.Length);

        for (var index = 0; index < sequentialSummary.Length; index++)
        {
            Assert.Equal(sequentialSummary[index].FileName, parallelSummary[index].FileName);
            Assert.Equal(sequentialSummary[index].Success, parallelSummary[index].Success);
            Assert.Equal(sequentialSummary[index].GeneratedCode, parallelSummary[index].GeneratedCode);
            Assert.Equal(sequentialSummary[index].Diagnostics, parallelSummary[index].Diagnostics);
        }

        var linqMetric = Assert.Single(parallelPipeline.LastPassMetrics, metric => metric.Name == "ProjectLinqDesugarPass");
        Assert.True(linqMetric.RewriteCount > 0);
    }

    private static ConversionOptions CreateOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
    }

    private sealed class TestListPass : ICs2jPass<List<string>>
    {
        private readonly Action<List<string>> _action;

        public TestListPass(string name, Cs2jPassStage stage, Action<List<string>> action)
        {
            Name = name;
            Stage = stage;
            _action = action;
        }

        public string Name { get; }
        public Cs2jPassStage Stage { get; }

        public void Execute(List<string> state)
        {
            _action(state);
        }
    }
}