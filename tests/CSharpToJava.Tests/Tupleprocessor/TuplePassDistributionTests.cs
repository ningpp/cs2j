using System.Diagnostics;
using CSharpToJava.CLI;

namespace CSharpToJava.Tests.Tupleprocessor;

/// <summary>
/// tuple-to-struct 跨程序集形状分配回归。
///
/// tuple 是 delegate 之后紧接着执行的一站，输入树里已经带着前序 pass 改写过的
/// csproj；它按"每个编译单元只生成自己必须定义的形状"分配生成文件。public 形状
/// 必须由"所有其他使用方都（传递）引用"的那个项目唯一定义，下游项目经
/// ProjectReference 复用；否则同名 struct 会分别进入两个程序集，元组集合跨程序集
/// 传递时报 CS1503（语料 GraphLayout：AglNingppTests 把
/// List&lt;Cs2jTuples.TupleInt32Int32Double&gt; 传给 AglNingpp 的
/// IEnumerable&lt;Cs2jTuples.TupleInt32Int32Double&gt;）。
/// </summary>
public sealed class TuplePassDistributionTests
{
    [Fact]
    public async Task PublicCrossProjectShape_IsDefinedOnlyByTheReferencedProject()
    {
        using var fixture = await TupleFixture.CreateAsync();

        var result = await fixture.RunTuplePassAsync();

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // 共享形状归库项目所有。
        var libStruct = Path.Combine(result.IntermediateRoot, "_Cs2jTupleTypes.ReproLib.cs");
        Assert.True(File.Exists(libStruct), $"Missing library struct file: {libStruct}");
        Assert.Contains(
            "public struct TupleInt32Int32Double",
            await File.ReadAllTextAsync(libStruct));

        // 消费者项目只定义自己独占的形状，共享形状必须通过 ProjectReference 复用，
        // 不得再生成一份（两份同名 public struct 分属两个程序集 → CS1503）。
        var appStruct = Path.Combine(result.IntermediateRoot, "_Cs2jTupleTypes.ReproApp.cs");
        Assert.True(File.Exists(appStruct), $"Missing app struct file: {appStruct}");
        var appContent = await File.ReadAllTextAsync(appStruct);
        Assert.DoesNotContain("TupleInt32Int32Double", appContent);
        Assert.Contains("public struct TupleInt32String", appContent);

        // 引用关系可解析，不应再落进"互不引用 → 各自复制一份"的兜底分支。
        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Message.Contains("do not reference each other", StringComparison.Ordinal));

        // 生成文件仍需挂进各自项目：库显式挂接根目录生成文件，应用挂接自己那份。
        var libProject = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "ReproLib", "ReproLib.csproj"));
        Assert.Contains("_Cs2jTupleTypes.ReproLib.cs", libProject);
        var appProject = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "ReproApp", "ReproApp.csproj"));
        Assert.Contains("_Cs2jTupleTypes.ReproApp.cs", appProject);
    }

    [Fact]
    public async Task IntermediateTree_Builds_WhenTupleCollectionFlowsAcrossProjects()
    {
        using var fixture = await TupleFixture.CreateAsync();

        var result = await fixture.RunTuplePassAsync();
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // 端到端证据：改写后的中间树自身必须能编译（修复前此处报 CS1503）。
        var exitCode = await RunDotNetBuildAsync(result.PreprocessedSourcePath);
        Assert.True(
            exitCode == 0,
            $"dotnet build of the intermediate tree failed ({exitCode}). " +
            $"Shared struct must be defined once in ReproLib, not copied into ReproApp.");
    }

    private static async Task<int> RunDotNetBuildAsync(string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{targetPath}\" -c Release -v q --nologo",
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

    /// <summary>
    /// 两项目解决方案：ReproLib 声明并使用 (int,int,double)；ReproApp 引用 lib，
    /// 构造 List&lt;(int,int,double)&gt; 传给 lib 的 IEnumerable 形参——正是
    /// GraphLayout 里 AglNingpp/AglNingppTests 的形态。
    /// </summary>
    private sealed class TupleFixture : IDisposable
    {
        private TupleFixture(string root)
        {
            Root = root;
            OutputRoot = Path.Combine(root, "out");
        }

        private string Root { get; }
        private string OutputRoot { get; }
        private string SolutionPath => Path.Combine(Root, "Repro.sln");

        public static async Task<TupleFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "cs2j-tuple-dist-" + Guid.NewGuid().ToString("N"));
            var fixture = new TupleFixture(root);
            Directory.CreateDirectory(root);

            await fixture.WriteFileAsync("Repro.sln", """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "ReproLib", "ReproLib\ReproLib.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "ReproApp", "ReproApp\ReproApp.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                    GlobalSection(SolutionConfigurationPlatforms) = preSolution
                        Release|Any CPU = Release|Any CPU
                    EndGlobalSection
                    GlobalSection(ProjectConfigurationPlatforms) = postSolution
                        {11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
                        {11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
                        {22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
                        {22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
                    EndGlobalSection
                EndGlobal
                """);

            await fixture.WriteProjectAsync("ReproLib", reference: null);
            await fixture.WriteFileAsync(Path.Combine("ReproLib", "Sparse.cs"), """
                using System;
                using System.Collections.Generic;

                namespace ReproLib
                {
                    public sealed class Sparse
                    {
                        public Sparse(int n, IEnumerable<(int source, int target, double weight)> edges, bool normalized)
                        {
                            Console.WriteLine(n + normalized.ToString().Length);
                            foreach (var (s, t, w) in edges)
                            {
                                Console.WriteLine(s + t + w);
                            }
                        }

                        public static void SelfCheck()
                        {
                            var edges = new List<(int, int, double)> { (0, 1, 1.0) };
                            _ = new Sparse(3, edges, false);
                        }
                    }
                }
                """);

            await fixture.WriteProjectAsync("ReproApp", reference: @"..\ReproLib\ReproLib.csproj");
            await fixture.WriteFileAsync(Path.Combine("ReproApp", "Use.cs"), """
                using System;
                using System.Collections.Generic;
                using ReproLib;

                namespace ReproApp
                {
                    public static class Use
                    {
                        public static void Run()
                        {
                            var empty = new Sparse(5, new List<(int, int, double)>(), false);
                            var edges = new List<(int, int, double)> { (0, 1, 1.0), (0, 2, 2.0) };
                            var lap = new Sparse(3, edges, false);
                            Console.WriteLine(lap is object ? empty is object : false);

                            // 应用独占的形状：仍必须在应用自己的生成文件里定义。
                            var names = new List<(int Id, string Label)> { (1, "a") };
                            foreach (var (id, label) in names)
                            {
                                Console.WriteLine(id + label);
                            }
                        }
                    }
                }
                """);

            return fixture;
        }

        public Task<ProjectTupleToStructPreprocessResult> RunTuplePassAsync()
            => ProjectTupleToStructPreprocessor.PreprocessAsync(
                new ProjectTupleToStructPreprocessRequest
                {
                    SourcePath = SolutionPath,
                    DestinationRoot = OutputRoot,
                    Verbose = false,
                });

        private Task WriteProjectAsync(string name, string? reference)
        {
            var projectReference = reference == null
                ? string.Empty
                : $"""

                      <ItemGroup>
                        <ProjectReference Include="{reference}" />
                      </ItemGroup>
                  """;

            return WriteFileAsync(Path.Combine(name, name + ".csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>{projectReference}
                </Project>
                """);
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
