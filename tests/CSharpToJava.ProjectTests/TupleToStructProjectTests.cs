using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class TupleToStructProjectTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "SampleTupleSolution");

    [Fact]
    public async Task LibProject_AllTupleForms_RewrittenWithNoResidualTupleSyntax()
    {
        var libDir = Path.Combine(FixtureDir, "SampleTupleLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(libDir, "..", ".test-output");

        try
        {
            var result = await ProjectTupleToStructPreprocessor.PreprocessAsync(
                new ProjectTupleToStructPreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed > 0);
            Assert.True(result.Statistics.StructsGenerated > 0);

            var intermediateDir = result.IntermediateRoot;
            Assert.True(Directory.Exists(intermediateDir));

            // 生成的 struct 文件
            var structFile = Path.Combine(intermediateDir, "_Cs2jTupleTypes.cs");
            Assert.True(File.Exists(structFile), "Generated tuple struct file was not written");
            var structContent = await File.ReadAllTextAsync(structFile);
            Assert.Contains("namespace Cs2jTuples", structContent);
            Assert.Contains("public readonly struct TupleInt32String", structContent);
            Assert.Contains("public readonly struct TupleInt32Int32", structContent);
            Assert.Contains("Rest", structContent);

            // 每个被改写的文件都不得残留任何元组语法（注释中的文字不算）。
            foreach (var file in Directory.EnumerateFiles(intermediateDir, "*.cs", SearchOption.AllDirectories))
            {
                var content = await File.ReadAllTextAsync(file);
                AssertResidualTupleFree(content, Path.GetFileName(file));
            }

            // TupleElementNames 特性必须被整体移除。
            foreach (var file in Directory.EnumerateFiles(intermediateDir, "*.cs", SearchOption.AllDirectories))
            {
                var content = await File.ReadAllTextAsync(file);
                Assert.DoesNotContain("TupleElementNames", content);
            }

            var shapes = await File.ReadAllTextAsync(Path.Combine(intermediateDir, "TupleShapes.cs"));
            Assert.Contains("global::Cs2jTuples.TupleInt32String", shapes);
            Assert.Contains(".Item1", shapes);
            Assert.Contains(".Item2", shapes);

            var deconstruction = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "TupleDeconstruction.cs"));
            Assert.DoesNotContain("var (", deconstruction);
            Assert.DoesNotContain("is var (", deconstruction);

            var longTuples = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "TupleLongAndGeneric.cs"));
            Assert.Contains(".Rest.Item1", longTuples);
            Assert.Contains("List<global::Cs2jTuples.TupleInt32String>", longTuples);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Solution_CrossProjectTuples_RewrittenAndIntermediateBuilds()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleTupleSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-sln");

        try
        {
            var result = await ProjectTupleToStructPreprocessor.PreprocessAsync(
                new ProjectTupleToStructPreprocessRequest
                {
                    SourcePath = solutionPath,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // 跨项目共享的 public 形状由库项目唯一定义，应用经 ProjectReference
            // 复用；两个程序集各自生成一份同名 struct 会让元组集合跨程序集传递
            // 时报 CS1503。
            var libProject = Path.Combine(
                result.IntermediateRoot, "SampleTupleLib", "SampleTupleLib.csproj");
            Assert.True(File.Exists(libProject), $"Missing intermediate project: {libProject}");
            var libContent = await File.ReadAllTextAsync(libProject);
            Assert.Contains("_Cs2jTupleTypes.cs", libContent);

            var appProject = Path.Combine(
                result.IntermediateRoot, "SampleTupleApp", "SampleTupleApp.csproj");
            Assert.True(File.Exists(appProject), $"Missing intermediate project: {appProject}");
            var appContent = await File.ReadAllTextAsync(appProject);
            Assert.DoesNotContain("_Cs2jTupleTypes", appContent);

            var structFile = Path.Combine(result.IntermediateRoot, "_Cs2jTupleTypes.cs");
            Assert.True(File.Exists(structFile), $"Missing generated struct file: {structFile}");
            var structContent = await File.ReadAllTextAsync(structFile);
            Assert.Contains("public struct TupleInt32String", structContent);
            Assert.Contains("public struct TupleInt32Int32", structContent);

            var appProgram = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleTupleApp", "Program.cs"));
            Assert.DoesNotContain("var (", appProgram);

            // 生成的中间源码树自身必须能编译（这是"输出仍是合法 C#"的端到端证据）。
            var buildExitCode = await RunDotNetBuildAsync(result.PreprocessedSourcePath);
            Assert.True(buildExitCode == 0, $"dotnet build of the intermediate tree failed ({buildExitCode})");
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanProject_NoTuples_CopiedByteIdenticalAndNoStructFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-tuple-clean-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            var sourceFile = Path.Combine(sourceDir, "Plain.cs");
            const string sourceCode = "namespace Plain;\npublic class Thing\n{\n    public int Value => 42;\n}\n";
            await File.WriteAllTextAsync(sourceFile, sourceCode);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Plain.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

            var result = await ProjectTupleToStructPreprocessor.PreprocessAsync(
                new ProjectTupleToStructPreprocessRequest
                {
                    SourcePath = sourceDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(0, result.Statistics.FilesTransformed);
            Assert.Equal(1, result.Statistics.FilesSkippedClean);
            Assert.Null(result.GeneratedStructFilePath);
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jTupleTypes.cs")));

            var copied = await File.ReadAllTextAsync(Path.Combine(result.IntermediateRoot, "Plain.cs"));
            Assert.Equal(sourceCode, copied);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void AssertResidualTupleFree(string content, string fileName)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content);
        var residual = tree.GetRoot().DescendantNodes()
            .Where(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.TupleTypeSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.TupleExpressionSyntax
                || (n is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax generic
                    && generic.Identifier.ValueText == "ValueTuple"))
            .Select(n => n.ToString())
            .ToList();
        Assert.True(residual.Count == 0,
            $"{fileName} still contains tuple syntax: {string.Join(" | ", residual)}");
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
