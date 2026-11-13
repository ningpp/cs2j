using System.Diagnostics;
using CSharpToJava.CLI;

namespace CSharpToJava.Tests.Tupleprocessor;

/// <summary>
/// 条件编译分支里的元组语法回归。
///
/// tuple pass 用固定符号集（NETCOREAPP）解析整棵树，`#if DEBUG` 等分支不在该
/// 符号集里，Roslyn 把它们表示为 DisabledText：其中的元组语法无法参与语义改写，
/// 只能原样保留。若同一 arity 的声明在活动区域被降级成生成 struct，编译该分支
/// 的工程就会在"原生 ValueTuple ↔ 生成 struct"之间报 CS0030/CS0029
/// （语料 System.Text.RegularExpressions 的 CharSetSolver.PrettyPrint：
/// `foreach ((uint, uint) range in BDDRangeConverter.ToRanges(set))`，
/// 而 ToRanges 的返回类型已被降级）。
///
/// 因此 DisabledText 中出现的元组 arity 必须在整个项目内保持原生。
/// </summary>
public sealed class TupleDisabledRegionArityTests
{
    [Fact]
    public async Task TupleSyntaxInDisabledConditionalBranch_KeepsThatArityNative()
    {
        using var fixture = await Fixture.CreateAsync();

        var result = await fixture.RunTuplePassAsync();
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // 活动区域里的二元组声明必须保持原生 (uint, uint)[]：DisabledText 中的
        // foreach 解构仍是原生元组，降级声明会让定义 DEBUG 的工程报 CS0030。
        var converter = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "Converter.cs"));
        Assert.Contains("(uint, uint)[]", converter);
        Assert.DoesNotContain("Cs2jTuples.TupleUInt32UInt32", converter);

        // 未被条件分支引用的三元组仍应正常降级，证明修复不是"整棵树都不改写"。
        Assert.Contains("Cs2jTuples.TupleInt32Int32Int32", converter);

        // DisabledText 原样保留（本 pass 无法改写该分支）。
        var solver = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "Solver.cs"));
        Assert.Contains("(uint, uint) range", solver);

        // 真实构建（csproj 无条件定义 DEBUG）必须通过：修复前这里报 CS0030。
        var exitCode = await fixture.RunBuildAsync(result.IntermediateRoot);
        Assert.True(exitCode == 0, $"dotnet build failed with exit code {exitCode}");
    }

    private sealed class Fixture : IDisposable
    {
        private const string ProjectFile = "Repro/Repro.csproj";

        private Fixture(string root) => Root = root;

        private string Root { get; }

        private string OutputRoot => Path.Combine(Root, "out");

        private string ProjectPath => Path.Combine(Root, ProjectFile);

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "cs2j-tuple-disabled-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture(root);
            Directory.CreateDirectory(root);

            await fixture.WriteFileAsync(ProjectFile, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <DefineConstants>$(DefineConstants);DEBUG</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);

            await fixture.WriteFileAsync("Repro/Converter.cs", """
                namespace Repro;

                public static class Ranges
                {
                    public static (uint, uint)[] ToRanges(int set) => new (uint, uint)[0];

                    public static (int, int, int) Triple() => (1, 2, 3);
                }
                """);

            await fixture.WriteFileAsync("Repro/Solver.cs", """
                namespace Repro;

                public static class Solver
                {
                    public static void Visit(int set)
                    {
                        uint total = 0;
                #if DEBUG
                        foreach ((uint, uint) range in Ranges.ToRanges(set))
                        {
                            total += range.Item2;
                        }
                #endif
                        System.Console.WriteLine(total);
                    }
                }
                """);

            return fixture;
        }

        public Task<ProjectTupleToStructPreprocessResult> RunTuplePassAsync()
            => ProjectTupleToStructPreprocessor.PreprocessAsync(
                new ProjectTupleToStructPreprocessRequest
                {
                    SourcePath = ProjectPath,
                    DestinationRoot = OutputRoot,
                    Verbose = false,
                });

        public async Task<int> RunBuildAsync(string intermediateRoot)
        {
            var projectFile = Directory
                .GetFiles(intermediateRoot, "Repro.csproj", SearchOption.AllDirectories)
                .Single();
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectFile}\" -c Release -v q --nologo",
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
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                Console.WriteLine(output);
                Console.Error.WriteLine(error);
            }

            return process.ExitCode;
        }

        private Task WriteFileAsync(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return File.WriteAllTextAsync(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
