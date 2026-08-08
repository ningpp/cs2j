using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// Compat surface for runtime-environment probes used by TestUtilities
/// PlatformDetection / SkipOnTargetFrameworkAttribute:
/// Environment.Version, RuntimeInformation, OSPlatform, Architecture.
/// The generated Java must call into io.github.ningpp.compat equivalents.
/// </summary>
public class RuntimeEnvironmentCompatTests
{
    [Fact]
    public async Task EnvironmentVersionAndPlatformProbes_MapsToCompatApi()
    {
        var filePath = Path.GetFullPath(Path.Combine("tests", "Common", "System", "PlatformDetection.cs"));
        var syntaxTree = CSharpSyntaxTree.ParseText(
            """
            using System.Runtime.InteropServices;

            namespace System
            {
                public static class PlatformDetectionProbe
                {
                    public static bool IsNetCore => Environment.Version.Major >= 5;
                    public static bool IsBrowser => RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER"));
                    public static bool IsArmProcess => RuntimeInformation.ProcessArchitecture == Architecture.Arm;
                    public static bool IsArm64Process => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
                }
            }
            """,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            filePath);

        var references = ProjectCompilationBuilder.GetMetadataReferences()
            .Append(MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.RuntimeInformation).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.OSPlatform).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(System.Runtime.InteropServices.Architecture).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "System.Private.Uri.Unit.Tests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(CreateOptions());
        var results = await pipeline.ConvertProjectAsync(
            compilation,
            new HashSet<string>([filePath], StringComparer.OrdinalIgnoreCase),
            projectName: "System.Private.Uri.Unit.Tests",
            projectFilePath: Path.GetFullPath("System.Private.Uri.Unit.Tests.csproj"),
            isTestProject: true);

        var result = Assert.Single(results);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Environment.getVersion().getMajor() >= 5", code);
        Assert.Contains("RuntimeInformation.isOSPlatform(OSPlatform.create(\"BROWSER\"))", code);
        Assert.Contains("RuntimeInformation.getProcessArchitecture() == Architecture.Arm", code);
        Assert.Contains("RuntimeInformation.getProcessArchitecture() == Architecture.Arm64", code);
        Assert.DoesNotContain("System.Runtime.InteropServices.Architecture", code);
        Assert.Contains("import io.github.ningpp.compat.Architecture;", code);
    }

    private static ConversionOptions CreateOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
    }
}
