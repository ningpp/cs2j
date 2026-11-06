using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.RefOutInPreprocessor;

namespace CSharpToJava.Tests;

/// <summary>
/// RT10 NU1201: the intermediate tree flattens the repository MSBuild files, so
/// <c>$(NetCoreAppCurrent)</c> evaluates to empty and a multi-target project
/// collapsed to netstandard2.0. These tests cover the copy-time TFM pinning
/// (through an injected evaluator, never a real msbuild process) and the
/// explicit skip of the net10.0-only Cs2jRefOutIn reference for older hosts.
/// </summary>
public sealed class ProjectPreprocessTargetFrameworkTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _sourceRoot;

    public ProjectPreprocessTargetFrameworkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-tfm-pin-" + Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(_sourceRoot);
    }

    public void Dispose()
    {
        MsBuildCompileEvaluator.EvaluationOverride = null;
        MsBuildCompileEvaluator.PackageReferenceEvaluationOverride = null;
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    private async Task<string> CopyAndReadAsync(string relativePath, string content)
    {
        var csproj = WriteFile(relativePath, content);
        var layout = new ProjectPreprocessSourceLayout(_sourceRoot, csproj, new[] { csproj });
        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, _sourceRoot, intermediateRoot, new SupportedProjectFiltering.ExclusionInfo());

        return await File.ReadAllTextAsync(Path.Combine(intermediateRoot, relativePath));
    }

    [Fact]
    public async Task CopyProjectFilesAsync_PinsMacroTargetFrameworksToEvaluatedTargetFramework()
    {
        MsBuildCompileEvaluator.EvaluationOverride = _ => new MsBuildCompileEvaluation
        {
            Files = Array.Empty<string>(),
            TargetFramework = "net10.0",
        };

        var copied = await CopyAndReadAsync(
            Path.Combine("src", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>$(NetCoreAppCurrent);netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", copied);
        Assert.DoesNotContain("TargetFrameworks", copied);
        Assert.DoesNotContain("netstandard2.0", copied);
    }

    [Fact]
    public async Task CopyProjectFilesAsync_UsesFirstEvaluatedTargetFrameworksItemWhenTargetFrameworkIsEmpty()
    {
        MsBuildCompileEvaluator.EvaluationOverride = _ => new MsBuildCompileEvaluation
        {
            Files = Array.Empty<string>(),
            TargetFrameworks = "net9.0;netstandard2.0",
        };

        var copied = await CopyAndReadAsync(
            Path.Combine("src", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>$(SomeMacro);netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        Assert.Contains("<TargetFramework>net9.0</TargetFramework>", copied);
        Assert.DoesNotContain("TargetFrameworks", copied);
    }

    [Fact]
    public async Task CopyProjectFilesAsync_KeepsOriginalWhenTargetFrameworkEvaluationFails()
    {
        MsBuildCompileEvaluator.EvaluationOverride = _ => null;

        var copied = await CopyAndReadAsync(
            Path.Combine("src", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>$(NetCoreAppCurrent);netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        Assert.Contains(
            "<TargetFrameworks>$(NetCoreAppCurrent);netstandard2.0</TargetFrameworks>",
            copied);
    }

    [Fact]
    public async Task CopyProjectFilesAsync_LeavesSingleLiteralTargetFrameworkUnchangedWithoutEvaluating()
    {
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

        var copied = await CopyAndReadAsync(
            Path.Combine("src", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", copied);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CopyProjectFilesAsync_CollapsesLiteralMultiTargetToFirstItemWithoutEvaluating()
    {
        var calls = 0;
        MsBuildCompileEvaluator.EvaluationOverride = _ =>
        {
            calls++;
            return null;
        };

        var copied = await CopyAndReadAsync(
            Path.Combine("src", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", copied);
        Assert.DoesNotContain("TargetFrameworks", copied);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void EnsureExtensionProjectReference_SkipsIncompatibleHostAndWarns()
    {
        var hostDirectory = Path.Combine(_tempRoot, "host");
        Directory.CreateDirectory(hostDirectory);
        var hostProject = Path.Combine(hostDirectory, "Host.csproj");
        File.WriteAllText(
            hostProject,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");
        var libProject = Path.Combine(
            intermediateRoot, "Cs2jRefOutIn", "Cs2jRefOutIn.csproj");
        var diagnostics = new List<RefOutInRewriterDiagnostic>();

        RefOutInPassAdapter.EnsureExtensionProjectReference(
            hostProject, libProject, intermediateRoot, diagnostics);

        var copied = File.ReadAllText(hostProject);
        Assert.DoesNotContain("Cs2jRefOutIn.csproj", copied);
        var warning = Assert.Single(diagnostics);
        Assert.Equal(RefOutInSeverity.Warning, warning.Severity);
        Assert.Contains("skipped", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CS0246", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureExtensionProjectReference_AddsReferenceForNet10Host()
    {
        var hostDirectory = Path.Combine(_tempRoot, "host");
        Directory.CreateDirectory(hostDirectory);
        var hostProject = Path.Combine(hostDirectory, "Host.csproj");
        File.WriteAllText(
            hostProject,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");
        var libProject = Path.Combine(
            intermediateRoot, "Cs2jRefOutIn", "Cs2jRefOutIn.csproj");
        var diagnostics = new List<RefOutInRewriterDiagnostic>();

        RefOutInPassAdapter.EnsureExtensionProjectReference(
            hostProject, libProject, intermediateRoot, diagnostics);

        var copied = File.ReadAllText(hostProject);
        Assert.Contains("Cs2jRefOutIn.csproj", copied);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void EnsureExtensionProjectReference_RemovesStaleReferenceFromIncompatibleHost()
    {
        var hostDirectory = Path.Combine(_tempRoot, "host");
        Directory.CreateDirectory(hostDirectory);
        var hostProject = Path.Combine(hostDirectory, "Host.csproj");
        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");
        var libProject = Path.Combine(
            intermediateRoot, "Cs2jRefOutIn", "Cs2jRefOutIn.csproj");
        File.WriteAllText(
            hostProject,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\intermediate\Cs2jRefOutIn\Cs2jRefOutIn.csproj" />
              </ItemGroup>
            </Project>
            """);

        var diagnostics = new List<RefOutInRewriterDiagnostic>();

        RefOutInPassAdapter.EnsureExtensionProjectReference(
            hostProject, libProject, intermediateRoot, diagnostics);

        var copied = File.ReadAllText(hostProject);
        Assert.DoesNotContain("Cs2jRefOutIn.csproj", copied);
        Assert.Single(diagnostics);
    }

    [Fact]
    public void SupportLibraries_PreludeAndUnsignedMath_MultiTargetNetstandard20()
    {
        Assert.Contains(
            "<TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>",
            PreludeSupportFiles.GetProjectFileContent());
        Assert.Contains(
            "<TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>",
            UnsignedMathSupportFiles.GetProjectFileContent());
    }

    [Fact]
    public void Cs2jRefOutInProject_OnlySupportsNet10Hosts()
    {
        Assert.True(Cs2jRefOutInProject.CanBeReferencedBy("net10.0"));
        Assert.False(Cs2jRefOutInProject.CanBeReferencedBy("netstandard2.0"));
        Assert.False(Cs2jRefOutInProject.CanBeReferencedBy(null));
    }
}
