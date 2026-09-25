using CSharpToJava.CLI;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

public class RecordReadOnlyProjectTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "SampleRecordSolution");

    private static RecordReadOnlyOptions ConvertStructOptions() => new()
    {
        ConvertRecordClasses = false,
    };

    private static RecordReadOnlyOptions ConvertAllOptions() => new()
    {
        ConvertRecordClasses = true,
    };

    [Fact]
    public async Task LibProject_RecordStructs_RewrittenAndIntermediateBuilds()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRecordLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(libDir, "..", ".test-output-rr");

        try
        {
            var result = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
                new ProjectRecordReadOnlyPreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Options = ConvertStructOptions(),
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.RecordStructsConverted > 0);
            Assert.True(result.Statistics.RecordsSkipped > 0); // WithEvent 等 FB-1 跳过

            var intermediateDir = result.IntermediateRoot;
            Assert.True(Directory.Exists(intermediateDir));

            // RS-1：readonly 修饰符已加。
            var structs = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "RecordStructSamples.cs"));
            Assert.Contains("readonly record struct Point", structs);
            Assert.Contains("public int Z { get; init; }", structs);

            // RS-18：已是 readonly 不重复加。
            Assert.Contains("public readonly record struct AlreadyImmutable", structs);

            // RS-10：事件 record 原样保留。
            Assert.Contains("public event System.Action? Changed", structs);

            // 整树重编译成功。
            var buildExitCode = await RunDotNetBuildAsync(result.PreprocessedSourcePath);
            Assert.True(buildExitCode == 0,
                $"dotnet build of the intermediate tree failed ({buildExitCode})");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Solution_RecordClasses_RewrittenAndIntermediateBuilds()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleRecordSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-rr-sln");

        try
        {
            var result = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
                new ProjectRecordReadOnlyPreprocessRequest
                {
                    SourcePath = solutionPath,
                    DestinationRoot = outputDir,
                    Options = ConvertAllOptions(),
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.RecordClassesConverted > 0);
            Assert.True(result.Statistics.RecordStructsConverted > 0);

            // RC-2：record class set → init。
            var classes = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleRecordLib", "RecordClassSamples.cs"));
            Assert.Contains("public int Age { get; init; }", classes);

            // RC-8：层次不一致 → 原样保留。
            Assert.Contains("public event System.Action? Ev", classes);
            Assert.Contains("public int Y { get; set; }", classes);

            // App 的用点已改写（MS-1）。
            var appProgram = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleRecordApp", "Program.cs"));
            Assert.Contains("with {", appProgram);

            var buildExitCode = await RunDotNetBuildAsync(result.PreprocessedSourcePath);
            Assert.True(buildExitCode == 0,
                $"dotnet build of the intermediate tree failed ({buildExitCode})");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task CleanProject_NoRecords_CopiedByteIdentical()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-clean-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            var sourceFile = Path.Combine(sourceDir, "Plain.cs");
            const string sourceCode =
                "namespace Plain;\npublic class Thing\n{\n    public int Value => 42;\n}\n";
            await File.WriteAllTextAsync(sourceFile, sourceCode);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Plain.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

            var result = await ProjectRecordReadOnlyPreprocessor.PreprocessAsync(
                new ProjectRecordReadOnlyPreprocessRequest
                {
                    SourcePath = sourceDir,
                    DestinationRoot = outputDir,
                    Options = ConvertStructOptions(),
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(0, result.Statistics.FilesTransformed);
            Assert.Equal(1, result.Statistics.FilesSkippedClean);

            var copied = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "Plain.cs"));
            Assert.Equal(sourceCode, copied);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<int> RunDotNetBuildAsync(string targetPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{targetPath}\" -v q --nologo -p:NoWarn=NU1605",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            Console.WriteLine(output);
            Console.Error.WriteLine(error);
        }

        return process.ExitCode;
    }
}
