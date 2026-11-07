using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing.Adapters;
using Xunit;

namespace CSharpToJava.Tests.Tupleprocessor;

/// <summary>
/// <c>typeof(ValueTuple&lt;...&gt;)</c> 运行时身份观测的项目作用域回归。
///
/// 合并编译单元里 json 测试项目的 <c>typeof(ValueTuple&lt;,&gt;)</c> 曾让整棵树所有
/// 二元组都保持原生 ValueTuple；regex 项目的 <c>(string CharClass, bool CaseInsensitive)</c>
/// 因此未降级，Java 侧映射成 io.vavr.Tuple2 后具名元素访问（.CharClass 等）编译失败。
/// 修复后观测只在所属项目内生效：同一项目仍按 arity 取并集（避免 CS0029），跨项目互不污染。
/// </summary>
public sealed class TupleRuntimeIdentityScopeTests
{
    [Fact]
    public async Task TwoProjects_ObservationInOneProject_DoesNotLeakIntoAnother()
    {
        using var fixture = await ScopeFixture.CreateTwoProjectAsync();

        var result = await fixture.RunAsync();

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var lib = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "ScopeLib", "Observed.cs"));
        var app = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "ScopeApp", "Use.cs"));

        // 观测项目自己：arity 2 保持原生 ValueTuple 语法。
        Assert.Contains("typeof(ValueTuple<,>)", lib);
        Assert.DoesNotContain("Cs2jTuples.TupleInt32Int32", lib);

        // 未观测项目：二元具名元组降级为生成 struct，具名访问换成 ItemN。
        Assert.Contains("Cs2jTuples.TupleStringBoolean", app);
        Assert.DoesNotContain(".Flag", app);

        // 生成文件只包含未观测项目的形状。
        Assert.False(string.IsNullOrEmpty(result.GeneratedStructFilePath));
        var structContent = await File.ReadAllTextAsync(result.GeneratedStructFilePath!);
        Assert.Contains("public struct TupleStringBoolean", structContent);
        Assert.DoesNotContain("TupleInt32Int32", structContent);
    }

    [Fact]
    public async Task SameProject_ObservationInAnotherFile_KeepsArityNativeEverywhere()
    {
        using var fixture = await ScopeFixture.CreateSingleProjectAsync();

        var result = await fixture.RunAsync();

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var observer = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "Single", "Observer.cs"));
        var user = await File.ReadAllTextAsync(
            Path.Combine(result.IntermediateRoot, "Single", "Pair.cs"));

        // 同一项目的其它文件也必须保持该 arity 原生（项目内 arity 级一致）。
        Assert.Contains("typeof(ValueTuple<,>)", observer);
        Assert.Contains("(string Name, bool Flag)", user);
        Assert.DoesNotContain("Cs2jTuples.TupleStringBoolean", user);

        // 没有任何可生成的形状。
        Assert.True(
            string.IsNullOrEmpty(result.GeneratedStructFilePath),
            $"unexpected struct file: {result.GeneratedStructFilePath}");
    }

    [Fact]
    public void MapFileArities_UsesProjectScope_SharedFilesTakeUnion_AndUnattributedUseNearestDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2j-scope-map");
        var projectA = Path.Combine(root, "A", "A.csproj");
        var projectB = Path.Combine(root, "B", "B.csproj");
        var shared = Path.Combine(root, "Shared", "Shared.cs");
        var fileA = Path.Combine(root, "A", "Observed.cs");
        var fileB = Path.Combine(root, "B", "Pair.cs");
        var unownedUnderB = Path.Combine(root, "B", "Generated.cs");
        var unrelated = Path.Combine(root, "Other", "Other.cs");

        var projects = new[]
        {
            new TupleProjectCompileScope(
                projectA,
                Path.GetDirectoryName(projectA)!,
                new[] { fileA, shared }),
            new TupleProjectCompileScope(
                projectB,
                Path.GetDirectoryName(projectB)!,
                new[] { fileB, shared }),
        };

        var observed = new Dictionary<string, IReadOnlyCollection<int>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [fileA] = new[] { 2 },
        };

        var mapped = TupleRuntimeIdentityScope.MapFileArities(
            projects,
            observed,
            new[] { unownedUnderB, unrelated });

        // 观测只落在项目 A。
        Assert.Equal(new[] { 2 }, mapped[fileA]);
        Assert.Empty(mapped[fileB]);

        // 共享源码只改写一次：必须同时满足所有编译它的项目 → 取并集。
        Assert.Equal(new[] { 2 }, mapped[shared]);

        // Compile 集合之外但位于项目 B 目录下 → 最近的 csproj 目录前缀。
        Assert.Empty(mapped[unownedUnderB]);

        // 完全无法归属 → 整树并集（保守）。
        Assert.Equal(new[] { 2 }, mapped[unrelated]);
    }

    private sealed class ScopeFixture : IDisposable
    {
        private ScopeFixture(string root, bool twoProjects)
        {
            Root = root;
            OutputRoot = Path.Combine(root, "out");
            TwoProjects = twoProjects;
        }

        private string Root { get; }
        private string OutputRoot { get; }
        private bool TwoProjects { get; }

        private string SolutionPath => Path.Combine(
            Root, TwoProjects ? "Scope.sln" : "Single.sln");

        public static async Task<ScopeFixture> CreateTwoProjectAsync()
        {
            var fixture = new ScopeFixture(
                Path.Combine(Path.GetTempPath(), "cs2j-tuple-scope-" + Guid.NewGuid().ToString("N")),
                twoProjects: true);
            Directory.CreateDirectory(fixture.Root);

            await fixture.WriteFileAsync("Scope.sln", Solution(
                ("ScopeLib", "ScopeLib\\ScopeLib.csproj", "{11111111-1111-1111-1111-111111111111}"),
                ("ScopeApp", "ScopeApp\\ScopeApp.csproj", "{22222222-2222-2222-2222-222222222222}")));

            await fixture.WriteProjectAsync("ScopeLib", reference: null);
            await fixture.WriteFileAsync(Path.Combine("ScopeLib", "Observed.cs"), """
                using System;

                namespace ScopeLib
                {
                    public sealed class Observed
                    {
                        private (int X, int Y) _pair;

                        public (int X, int Y) Pair
                        {
                            get => _pair;
                            set => _pair = value;
                        }

                        public static bool IsPair(Type type) =>
                            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTuple<,>);
                    }
                }
                """);

            await fixture.WriteProjectAsync("ScopeApp", reference: @"..\ScopeLib\ScopeLib.csproj");
            await fixture.WriteFileAsync(Path.Combine("ScopeApp", "Use.cs"), """
                namespace ScopeApp
                {
                    public sealed class Use
                    {
                        private (string Name, bool Flag) _field;

                        public bool Read() => _field.Flag;

                        public void Write() => _field = ("a", true);
                    }
                }
                """);

            return fixture;
        }

        public static async Task<ScopeFixture> CreateSingleProjectAsync()
        {
            var fixture = new ScopeFixture(
                Path.Combine(Path.GetTempPath(), "cs2j-tuple-scope-" + Guid.NewGuid().ToString("N")),
                twoProjects: false);
            Directory.CreateDirectory(fixture.Root);

            await fixture.WriteFileAsync("Single.sln", Solution(
                ("Single", "Single\\Single.csproj", "{33333333-3333-3333-3333-333333333333}")));

            await fixture.WriteProjectAsync("Single", reference: null);
            await fixture.WriteFileAsync(Path.Combine("Single", "Observer.cs"), """
                using System;

                namespace Single
                {
                    public sealed class Observer
                    {
                        public static readonly Type Observed = typeof(ValueTuple<,>);
                    }
                }
                """);
            await fixture.WriteFileAsync(Path.Combine("Single", "Pair.cs"), """
                namespace Single
                {
                    public sealed class Pair
                    {
                        public (string Name, bool Flag) Field;

                        public bool Read() => Field.Flag;
                    }
                }
                """);

            return fixture;
        }

        public Task<ProjectTupleToStructPreprocessResult> RunAsync()
            => ProjectTupleToStructPreprocessor.PreprocessAsync(
                new ProjectTupleToStructPreprocessRequest
                {
                    SourcePath = SolutionPath,
                    DestinationRoot = OutputRoot,
                    Verbose = false,
                });

        private static string Solution(params (string Name, string Path, string Guid)[] projects)
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            builder.AppendLine("# Visual Studio Version 17");
            foreach (var project in projects)
            {
                builder.AppendLine(
                    $"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = " +
                    $"\"{project.Name}\", \"{project.Path}\", \"{project.Guid}\"");
                builder.AppendLine("EndProject");
            }

            builder.AppendLine("Global");
            builder.AppendLine("    GlobalSection(SolutionConfigurationPlatforms) = preSolution");
            builder.AppendLine("        Release|Any CPU = Release|Any CPU");
            builder.AppendLine("    EndGlobalSection");
            builder.AppendLine("    GlobalSection(ProjectConfigurationPlatforms) = postSolution");
            foreach (var project in projects)
            {
                builder.AppendLine($"        {project.Guid}.Release|Any CPU.ActiveCfg = Release|Any CPU");
                builder.AppendLine($"        {project.Guid}.Release|Any CPU.Build.0 = Release|Any CPU");
            }

            builder.AppendLine("    EndGlobalSection");
            builder.AppendLine("EndGlobal");
            return builder.ToString();
        }

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
