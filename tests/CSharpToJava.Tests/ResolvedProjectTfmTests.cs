using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

public sealed class ResolvedProjectTfmTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-resolved-tfm-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MacroTargetFrameworks_UsesEvaluatedTargetFramework_NotLiteralFallback()
    {
        var project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>$(NetCoreAppCurrent);netstandard2.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);
        var calls = 0;
        MsBuildCompileEvaluator.EvaluationOverride = _ =>
        {
            calls++;
            return new MsBuildCompileEvaluation
            {
                Files = Array.Empty<string>(),
                TargetFramework = "net8.0",
            };
        };

        try
        {
            var resolved = ResolvedProjectTfm.Resolve(project);

            Assert.Equal("net8.0", resolved);
            Assert.Equal(1, calls);
        }
        finally
        {
            MsBuildCompileEvaluator.EvaluationOverride = null;
            ResolvedProjectTfm.ClearCache();
        }
    }

    [Fact]
    public void LiteralTargetFramework_DoesNotInvokeEvaluator()
    {
        var project = WriteProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var calls = 0;
        MsBuildCompileEvaluator.EvaluationOverride = _ =>
        {
            calls++;
            return new MsBuildCompileEvaluation
            {
                Files = Array.Empty<string>(),
                TargetFramework = "net8.0",
            };
        };

        try
        {
            var resolved = ResolvedProjectTfm.Resolve(project);

            Assert.Equal("net10.0", resolved);
            Assert.Equal(0, calls);
        }
        finally
        {
            MsBuildCompileEvaluator.EvaluationOverride = null;
            ResolvedProjectTfm.ClearCache();
        }
    }

    private string WriteProject(string content)
    {
        var directory = Path.Combine(_root, "App");
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, content);
        return project;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
