using System.Diagnostics;
using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the readonly-struct preprocessor verification step.
/// The GraphLayout (MSAGL) source contains a flaky Debug.Assert that fires on a
/// background routing thread under the full test suite in Debug builds, crashing
/// the test host even though no test fails. Verification must therefore run tests
/// in a configuration where Debug.Assert is compiled out (Release), otherwise the
/// whole conversion aborts on source code that is not broken.
/// </summary>
public class ReadOnlyStructPreprocessorVerifyTests
{
    [Fact]
    public async Task PreprocessAsync_VerifyTests_PassesWhenSourceContainsFailingDebugAssert()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var projectDir = Path.Combine(root, "FlakyLib");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(
                Path.Combine(projectDir, "FlakyLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    <PackageReference Include="xunit" Version="2.9.3" />
                    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(projectDir, "FlakyTest.cs"),
                """
                using System.Diagnostics;
                using Xunit;

                namespace FlakyLib;

                public class FlakyTest
                {
                    [Fact]
                    public void DebugAssert_Fires()
                    {
                        // Mirrors MSAGL FlipSwitcher: a Debug.Assert that can fire
                        // (and crash the test host) even though no test is broken.
                        Debug.Assert(false, "flaky assertion");
                    }
                }
                """);

            var destinationRoot = Path.Combine(root, "out");
            var result = await ProjectReadOnlyStructPreprocessor.PreprocessAsync(new ProjectReadOnlyPreprocessRequest
            {
                SourcePath = Path.Combine(projectDir, "FlakyLib.csproj"),
                DestinationRoot = destinationRoot,
                Force = true,
                VerifyBuild = true,
                VerifyTests = true,
            });

            Assert.True(
                result.Success,
                "Verification should not fail on a source-only Debug.Assert crash. Diagnostics:\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Reason}")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsDotNetAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-readonly-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
