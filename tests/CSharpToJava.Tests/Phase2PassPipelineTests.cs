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
            },
            metric =>
            {
                Assert.Equal("CheckListPass", metric.Name);
                Assert.Equal(Cs2jPassStage.Check, metric.Stage);
                Assert.Equal(1, metric.DiagnosticDelta);
            },
            metric =>
            {
                Assert.Equal("EmitListPass", metric.Name);
                Assert.Equal(Cs2jPassStage.Emit, metric.Stage);
                Assert.Equal(0, metric.DiagnosticDelta);
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
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("System.Windows.Forms", StringComparison.Ordinal));
        Assert.Contains("SingleFileUnsupportedDomainCheckPass", result.PassMetrics.Select(metric => metric.Name));
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
                "ProjectPartialTypeNormalizationPass",
                "ProjectTypeEmitPass",
                "ProjectCompatibilityEmitPass",
                "ProjectCrossPackageImportEmitPass",
                "ProjectPostGenerationRewriteEmitPass",
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
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Message.Contains("System.Windows.Forms", StringComparison.Ordinal));
        Assert.Contains("ProjectUnsupportedDomainCheckPass", failureResult.PassMetrics.Select(metric => metric.Name));
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
        Assert.Contains(failureResult.Diagnostics, diagnostic => diagnostic.Message.Contains("DllImport", StringComparison.Ordinal));
    }

    private static ConversionOptions CreateOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            EmitCompatibilityHelpers = false,
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