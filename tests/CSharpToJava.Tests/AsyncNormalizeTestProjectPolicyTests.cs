using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.AsyncNormalize;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json async test files (Stream/Write/JsonDocument tests):
/// test projects are .NET-side behaviour oracles, not Java conversion inputs,
/// so async void members and residual async constructs must not abort the
/// C#→C# preprocess chain. The adapter downgrades per-file Errors to warnings
/// for files inside test projects (production projects keep the Error gate).
/// </summary>
public class AsyncNormalizeTestProjectPolicyTests
{
    [Fact]
    public async Task AsyncVoid_InTestProject_IsWarning_AndPassSucceeds()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cs2j-async-test-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outputDir = Path.Combine(root, "out");
        try
        {
            var projectDir = Path.Combine(root, "AsyncTests");
            Directory.CreateDirectory(projectDir);
            var projectPath = Path.Combine(projectDir, "AsyncTests.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(projectDir, "C.cs"), """
                namespace AsyncTests;

                public class C
                {
                    public async void FireAndForget()
                    {
                        await System.Threading.Tasks.Task.Delay(1);
                    }
                }
                """);

            var adapter = new AsyncNormalizePassAdapter(
                new AsyncNormalizeOptions(),
                verbose: false,
                filterUnsupportedProjects: false,
                compatNamespacePrefixes: null,
                verifyBuild: false,
                verifyTests: false,
                strict: true);

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = projectPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success);
            Assert.False(adapter.HasErrors);
            Assert.Contains(adapter.Diagnostics, d =>
                d.Code == AsyncNormalizeDiagnosticCode.CS2J1803
                && d.Severity == AsyncNormalizeSeverity.Warning);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
