using System.Diagnostics;
using CSharpToJava.CLI;
using CSharpToJava.Core.JavaKeywords;

namespace CSharpToJava.ProjectTests;

public class JavaKeywordProjectTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "SampleJavaKeywordSolution");

    private static JavaKeywordOptions DefaultOptions() => new();

    [Fact]
    public async Task Solution_KeywordsRenamed_AndIntermediateBuilds()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleJavaKeywordSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-jk-sln");

        try
        {
            var result = await ProjectJavaReservedWordPreprocessor.PreprocessAsync(
                new ProjectJavaReservedWordPreprocessRequest
                {
                    SourcePath = solutionPath,
                    DestinationRoot = outputDir,
                    Options = DefaultOptions(),
                });

            Assert.True(result.Success,
                string.Join("\n", result.Diagnostics.Select(d => d.ToMsBuildString())));
            Assert.True(result.Statistics.SymbolsRenamed > 0,
                $"Expected at least one symbol renamed, got {result.Statistics.SymbolsRenamed}");

            // JK-1~JK-2: 方法/字段/属性改名
            var keywordSamples = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleJavaKeywordLib", "KeywordSamples.cs"));
            Assert.Contains("void TrueValue()", keywordSamples);
            Assert.Contains("int NullValue { get; set; }", keywordSamples);
            Assert.Contains("int assertValue = 42", keywordSamples);
            Assert.Contains("event System.Action? classValue", keywordSamples);
            // Note: @class in comments is not rewritten by the syntax rewriter;
            // only check the declaration and reference sites.
            Assert.Contains("classValue?.Invoke()", keywordSamples);

            // JK-2: 局部变量和参数改名
            Assert.Contains("int yieldValue = 1", keywordSamples);
            Assert.Contains("int TrueValue", keywordSamples);

            // JK-3: 类型改名
            Assert.Contains("class NullValue", keywordSamples);

            // JK-4a: nameof 字面量化
            Assert.Contains("\"True\"", keywordSamples);
            Assert.DoesNotContain("nameof(True)", keywordSamples);

            // JK-4b: 碰撞消歧——TrueValue 已存在，True 改为 TrueValue2
            Assert.Contains("void TrueValue2()", keywordSamples);

            // JK-4c: 引用识别——调用点改名
            Assert.Contains("TrueValue2()", keywordSamples);
            Assert.Contains("NullValue", keywordSamples);
            Assert.Contains("assertValue", keywordSamples);
            Assert.Contains("classValue", keywordSamples);

            // JK-5: operator true/false 声明降级
            var operatorSamples = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleJavaKeywordLib", "OperatorSamples.cs"));
            Assert.Contains("public static bool IsTrue(Truthiness x)", operatorSamples);
            Assert.Contains("public static bool IsFalse(Truthiness x)", operatorSamples);
            Assert.DoesNotContain("operator true", operatorSamples);
            Assert.DoesNotContain("operator false", operatorSamples);

            // JK-6a: if/while 条件使用点包裹
            Assert.Contains("if (Truthiness.IsTrue(t))", operatorSamples);
            Assert.Contains("while (Truthiness.IsTrue(t))", operatorSamples);

            // JK-6b: 三元表达式使用点包裹
            Assert.Contains("Truthiness.IsTrue(t) ? 1 : 2", operatorSamples);

            // 跨文件引用：App 中的引用已改名
            var appProgram = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleJavaKeywordApp", "Program.cs"));
            Assert.Contains("lib.TrueValue()", appProgram);
            Assert.Contains("lib.NullValue", appProgram);
            Assert.Contains("new NullValue()", appProgram);

            // 整树重编译成功
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
    public async Task CleanProject_NoKeywords_CopiedByteIdentical()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-jk-clean-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            const string sourceCode =
                "namespace Plain;\npublic class Thing\n{\n    public int Value => 42;\n}\n";
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "Plain.cs"), sourceCode);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Plain.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

            var result = await ProjectJavaReservedWordPreprocessor.PreprocessAsync(
                new ProjectJavaReservedWordPreprocessRequest
                {
                    SourcePath = sourceDir,
                    DestinationRoot = outputDir,
                    Options = DefaultOptions(),
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToMsBuildString())));
            Assert.Equal(0, result.Statistics.SymbolsRenamed);

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
        var startInfo = new ProcessStartInfo
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

        using var process = Process.Start(startInfo);
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
