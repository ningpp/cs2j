using System.Diagnostics;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Tests;

public class ProjectPreprocessorsConsistentTests
{
    private static readonly IReadOnlyList<string> WindowsCompatNamespacePrefixes =
    [
        "System.Windows.Forms",
        "System.Windows",
    ];

    [Fact]
    public async Task AllPreprocessors_UseSameProjectSelection_AndExcludeWindowsProjects()
    {
        if (!IsDotNetAvailable())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var sourceRoot = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceRoot);

            var libProject = CreateProject(sourceRoot, "Lib", isTest: false);
            var wpfProject = CreateProject(
                sourceRoot,
                "WpfApp",
                isTest: false,
                sdk: "Microsoft.NET.Sdk.WindowsDesktop",
                targetFramework: "net8.0-windows",
                useWpf: true);
            // A Windows/WPF project that is NOT part of the solution must also be
            // excluded from every intermediate tree by the same directory scan.
            CreateProject(
                sourceRoot,
                "OrphanWpf",
                isTest: false,
                sdk: "Microsoft.NET.Sdk.WindowsDesktop",
                targetFramework: "net8.0-windows",
                useWpf: true);
            var solutionPath = CreateSolution(sourceRoot, "Solution.sln", [libProject, wpfProject]);

            var destinationRoot = Path.Combine(root, "out");

            var unsigned = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = solutionPath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = true,
                    CompatNamespacePrefixes = WindowsCompatNamespacePrefixes,
                });
            Assert.True(unsigned.Success, string.Join("\n", unsigned.Diagnostics.Select(d => d.Message)));

            var readonlyResult = await ProjectReadOnlyStructPreprocessor.PreprocessAsync(
                new ProjectReadOnlyPreprocessRequest
                {
                    SourcePath = unsigned.PreprocessedSourcePath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = true,
                    CompatNamespacePrefixes = WindowsCompatNamespacePrefixes,
                    VerifyBuild = false,
                    VerifyTests = false,
                });
            Assert.True(
                readonlyResult.Success,
                string.Join("\n", readonlyResult.Diagnostics.Select(d => $"[{d.Severity}] {d.Reason}")));
            var gotoResult = await ProjectGotoPreprocessor.PreprocessAsync(
                new ProjectGotoPreprocessRequest
                {
                    SourcePath = readonlyResult.PreprocessedSourcePath,
                    DestinationRoot = destinationRoot,
                    Force = true,
                    FilterUnsupportedProjects = true,
                    CompatNamespacePrefixes = WindowsCompatNamespacePrefixes,
                });
            Assert.True(gotoResult.Success, string.Join("\n", gotoResult.Diagnostics.Select(d => d.Message)));

            var intermediateRoots = new[]
            {
                unsigned.IntermediateRoot,
                readonlyResult.IntermediateRoot,
                gotoResult.IntermediateRoot,
            };

            var projectPathSets = intermediateRoots
                .Select(CollectProjectFilePaths)
                .ToArray();

            foreach (var paths in projectPathSets)
            {
                Assert.DoesNotContain(paths, p => p.Contains("wpfapp", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(paths, p => p.Contains("orphanwpf", StringComparison.OrdinalIgnoreCase));
            }

            Assert.Equal(projectPathSets[0], projectPathSets[1]);
            Assert.Equal(projectPathSets[0], projectPathSets[2]);

            // record-readonly 驱动器存在且与既有预处理器同源（ProjectPreprocessSource）。
            // 默认关（ConvertProjectOptions.RecordReadOnly = false），故 convert-project
            // 不会自动触发；独立 verb 通过 --convert-record-classes 显式开启。
            // 这里仅确认类型存在与签名兼容，不实际执行（避免 fixture 依赖）。
            var recordReadonlyType = typeof(ProjectRecordReadOnlyPreprocessor);
            Assert.NotNull(recordReadonlyType);
            Assert.Equal(".cs2j-record-readonly-src",
                ProjectRecordReadOnlyPreprocessor.IntermediateDirectoryName);

            // java-keywords 驱动器存在且与既有预处理器同源（ProjectPreprocessSource）。
            // 默认关（ConvertProjectOptions.JavaKeywords = false），故 convert-project
            // 不会自动触发；独立 verb 通过 java-keywords 显式开启。
            var javaKeywordsType = typeof(ProjectJavaReservedWordPreprocessor);
            Assert.NotNull(javaKeywordsType);
            Assert.Equal(".cs2j-java-keywords-src",
                ProjectJavaReservedWordPreprocessor.IntermediateDirectoryName);

            // enum preprocessor — verify type exists and intermediate name is correct
            var enumPreprocessorType = typeof(ProjectEnumPreprocessor);
            Assert.NotNull(enumPreprocessorType);
            Assert.Equal(".cs2j-enum-src",
                ProjectEnumPreprocessor.IntermediateDirectoryName);

            // enum-declaration (C05)：无门面类（设计决定），目录常量挂在注册表描述符上；
            // 默认开，可经 --disable-pass enum-declaration 关闭。
            var enumDeclarationDescriptor = PassCatalog.ImplementedById(PassIds.EnumDeclaration);
            Assert.Equal(".cs2j-enum-declaration-src",
                enumDeclarationDescriptor.IntermediateDirectoryName);
            Assert.True(enumDeclarationDescriptor.DefaultEnabled);
            Assert.Equal("C05", enumDeclarationDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.EnumDeclarationPassAdapter));

            // lexical-normalize (C28): no facade; descriptor carries the directory;
            // 默认开（all-on policy），可经 --disable-pass lexical-normalize 关闭。
            var lexicalDescriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.LexicalNormalize);
            Assert.Equal(".cs2j-lexical-normalize-src",
                lexicalDescriptor.IntermediateDirectoryName);
            Assert.True(lexicalDescriptor.DefaultEnabled);
            Assert.Equal("C28, C43", lexicalDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.LexicalNormalizePassAdapter));

            // generic-deepen (C02)：无门面类（设计决定），目录常量挂在 CLI 适配器上；
            // 默认关，经 --enable-pass generic-deepen / --pass generic-deepen 开启。
            Assert.Equal(".cs2j-generic-deepen-src",
                PassCatalog.ImplementedById(PassIds.GenericDeepen).IntermediateDirectoryName);

            // member-desugar (C11)：无门面类（设计决定），目录常量挂在注册表描述符上；
            // 默认关，经 --member-desugar / --enable-pass member-desugar / --pass member-desugar 开启。
            Assert.Equal(".cs2j-member-desugar-src",
                PassCatalog.ImplementedById(PassIds.MemberDesugar).IntermediateDirectoryName);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.MemberDesugarPassAdapter));

            // extension-methods (C19)：无门面类，目录常量挂在 CLI 适配器上；
            // 默认开（all-on policy），可经 --disable-pass extension-methods 关闭。
            Assert.Equal(".cs2j-extension-methods-src",
                CSharpToJava.CLI.Preprocessing.Adapters.ExtensionMethodResolvePassAdapter.IntermediateDirectoryName);
            Assert.Equal(".cs2j-extension-methods-src",
                PassCatalog.ImplementedById(PassIds.ExtensionMethods).IntermediateDirectoryName);
            Assert.True(PassCatalog.ImplementedById(PassIds.ExtensionMethods).DefaultEnabled);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.ExtensionMethodResolvePassAdapter));

            // struct-value-semantics (C07)：无门面类（设计决定），目录常量挂在注册表描述符上；
            // 默认开，可经 --disable-pass struct-value-semantics 关闭。
            Assert.Equal(".cs2j-struct-value-semantics-src",
                PassCatalog.ImplementedById(PassIds.StructValueSemantics).IntermediateDirectoryName);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.StructValueSemanticsPassAdapter));

            // unsafe-ffm (C06)：无门面类（设计决定），目录常量挂在注册表描述符上；
            // 默认开，可经 --disable-pass unsafe-ffm 关闭；
            // 无支撑库（PassSupportLibrary.None）。
            var unsafeFfmDescriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.UnsafeFfm);
            Assert.Equal(".cs2j-unsafe-ffm-src",
                unsafeFfmDescriptor.IntermediateDirectoryName);
            Assert.True(unsafeFfmDescriptor.DefaultEnabled);
            Assert.Equal(PassStatus.Implemented, unsafeFfmDescriptor.Status);
            Assert.Equal(PassStage.Abi, unsafeFfmDescriptor.Stage);
            Assert.Equal(PassSupportLibrary.None, unsafeFfmDescriptor.SupportLibrary);
            Assert.True(unsafeFfmDescriptor.NeedsSemanticModel);
            Assert.True(unsafeFfmDescriptor.VerifyBuildByDefault);
            Assert.True(unsafeFfmDescriptor.VerifyTestsByDefault);
            Assert.Equal("C06", unsafeFfmDescriptor.CatalogItems);
            Assert.Equal(
                new[] { PassIds.StructValueSemantics },
                unsafeFfmDescriptor.RunAfterIds);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.UnsafeFfmPassAdapter));

            // index-range (C31)：无门面类（设计决定），目录常量挂在 CLI 适配器上；
            // 默认开（defaultEnabled: true），经 --pass index-range 隔离/ --enable-pass 追加。
            Assert.Equal(".cs2j-index-range-src",
                PassCatalog.ImplementedById(PassIds.IndexRange).IntermediateDirectoryName);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.IndexRangePassAdapter));

            // numeric-boundary (C15)：无门面类（设计决定），目录常量挂注册表描述符；
            // 默认关，经 --enable-pass numeric-boundary / --pass numeric-boundary 开启。
            Assert.Equal(".cs2j-numeric-boundary-src",
                PassCatalog.ImplementedById(PassStubCatalog.Ids.NumericBoundary).IntermediateDirectoryName);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.NumericBoundaryPassAdapter));

            // foreach-source (C16)：无门面类（设计决定），目录常量挂注册表描述符；
            // 默认开，可经 --disable-pass foreach-source 关闭。
            var foreachSourceDescriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.ForeachSource);
            Assert.Equal(".cs2j-foreach-source-src",
                foreachSourceDescriptor.IntermediateDirectoryName);
            Assert.True(foreachSourceDescriptor.DefaultEnabled);
            // C16 锚点描述符；C40 鸭子枚举器规则在同一 pass 内注册。
            Assert.Equal("C16, C40", foreachSourceDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.ForeachSourcePassAdapter));

            // variance-audit (C33)：无门面类（设计决定），目录常量挂注册表描述符；
            // 只读审计，默认开（all-on policy），可经 --disable-pass variance-audit 关闭；
            // 从不 verify build/tests（输出树与输入字节一致），无支撑库。
            var varianceAuditDescriptor = PassCatalog.ImplementedById(PassIds.VarianceAudit);
            Assert.Equal(".cs2j-variance-audit-src",
                varianceAuditDescriptor.IntermediateDirectoryName);
            Assert.Equal(PassStatus.Implemented, varianceAuditDescriptor.Status);
            Assert.Equal(PassStage.Declarative, varianceAuditDescriptor.Stage);
            Assert.True(varianceAuditDescriptor.DefaultEnabled);
            Assert.False(varianceAuditDescriptor.VerifyBuildByDefault);
            Assert.False(varianceAuditDescriptor.VerifyTestsByDefault);
            Assert.Equal(PassSupportLibrary.None, varianceAuditDescriptor.SupportLibrary);
            Assert.Equal("C33", varianceAuditDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.VarianceAuditPassAdapter));

            // anonymous-type-lift (C21)：默认开（all-preprocessors-default-on
            // policy），可经 --disable-pass anonymous-type-lift 关闭；拓扑槽位
            // 锁在 tuple 之后（RunAfterIds）；声明式改写、需要语义模型；每次
            // 落树都 verify build + tests；无支撑库。
            var anonymousTypeLiftDescriptor = PassCatalog.ImplementedById(PassIds.AnonymousTypeLift);
            Assert.Equal(".cs2j-anonymous-type-lift-src",
                anonymousTypeLiftDescriptor.IntermediateDirectoryName);
            Assert.Equal(PassStatus.Implemented, anonymousTypeLiftDescriptor.Status);
            Assert.Equal(PassStage.Declarative, anonymousTypeLiftDescriptor.Stage);
            Assert.True(anonymousTypeLiftDescriptor.DefaultEnabled);
            Assert.Equal(new[] { PassIds.Tuple }, anonymousTypeLiftDescriptor.RunAfterIds);
            Assert.Equal(PassSupportLibrary.None, anonymousTypeLiftDescriptor.SupportLibrary);
            Assert.True(anonymousTypeLiftDescriptor.NeedsSemanticModel);
            Assert.True(anonymousTypeLiftDescriptor.VerifyBuildByDefault);
            Assert.True(anonymousTypeLiftDescriptor.VerifyTestsByDefault);
            Assert.Equal("C21", anonymousTypeLiftDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.AnonymousTypeLiftPassAdapter));

            // dynamic-desugar (C25)：无门面类（设计决定），目录常量挂注册表
            // 描述符；M1 纯审计（dynamic-audit-report.json + 具名 DS0xx 诊断，
            // 零改写），AUDIT 于 2026-10-06 通过 §8 语料门后翻为默认开
            // （--disable-pass dynamic-desugar 可摘除），REWRITE 仍长期 opt-in
            // （--dynamic-mode Rewrite，门 6-9 未满足）；链尾 ABI 槽位
            // RunAfter=eliminate-goto（先于 java-keywords）；需要语义模型；
            // 无支撑库；描述符 verify 双开为 Rewrite 预留，Audit 适配器内关 verify。
            var dynamicDescriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.DynamicDesugar);
            Assert.Equal(".cs2j-dynamic-desugar-src",
                dynamicDescriptor.IntermediateDirectoryName);
            Assert.Equal(PassStatus.Implemented, dynamicDescriptor.Status);
            Assert.True(dynamicDescriptor.DefaultEnabled);
            Assert.Equal(PassStage.Abi, dynamicDescriptor.Stage);
            Assert.Equal(new[] { PassIds.EliminateGoto }, dynamicDescriptor.RunAfterIds);
            Assert.Equal(PassSupportLibrary.None, dynamicDescriptor.SupportLibrary);
            Assert.True(dynamicDescriptor.NeedsSemanticModel);
            Assert.True(dynamicDescriptor.VerifyBuildByDefault);
            Assert.True(dynamicDescriptor.VerifyTestsByDefault);
            Assert.Equal("C25", dynamicDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.DynamicDesugarPassAdapter));

            // linq-channel (C26 M4，2026-10-06 翻默认开)：无门面类（设计决定），
            // 目录常量挂 CLI 适配器；Implemented 默认开（M4 §8 门后），
            // --no-linq-rewrite / --disable-pass linq-channel 摘除；锚点为
            // extension-methods + anonymous-type-lift + eliminate-goto；
            // 控制流派生、需要语义模型；每次落树 verify build + tests；
            // 无支撑库。
            var linqChannelDescriptor = PassCatalog.ImplementedById(PassStubCatalog.Ids.LinqChannel);
            Assert.Equal(
                CSharpToJava.CLI.Preprocessing.Adapters.LinqChannelPassAdapter.IntermediateDirectoryName,
                linqChannelDescriptor.IntermediateDirectoryName);
            Assert.Equal(".cs2j-linq-channel-src",
                linqChannelDescriptor.IntermediateDirectoryName);
            Assert.Equal(PassStatus.Implemented, linqChannelDescriptor.Status);
            Assert.Equal(PassStage.ControlFlow, linqChannelDescriptor.Stage);
            Assert.True(linqChannelDescriptor.DefaultEnabled);
            Assert.Equal(
                new[]
                {
                    PassIds.ExtensionMethods,
                    PassIds.AnonymousTypeLift,
                    PassIds.EliminateGoto,
                },
                linqChannelDescriptor.RunAfterIds);
            Assert.Equal(PassSupportLibrary.None, linqChannelDescriptor.SupportLibrary);
            Assert.True(linqChannelDescriptor.NeedsSemanticModel);
            Assert.True(linqChannelDescriptor.VerifyBuildByDefault);
            Assert.True(linqChannelDescriptor.VerifyTestsByDefault);
            Assert.Equal("C26", linqChannelDescriptor.CatalogItems);
            Assert.NotNull(typeof(CSharpToJava.CLI.Preprocessing.Adapters.LinqChannelPassAdapter));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyList<string> CollectProjectFilePaths(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            .Where(path => !HasArtifactSegment(path))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/').ToLowerInvariant())
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasArtifactSegment(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateProject(
        string root,
        string name,
        bool isTest,
        string sdk = "Microsoft.NET.Sdk",
        string targetFramework = "net10.0",
        bool useWpf = false,
        bool useWindowsForms = false)
    {
        var projectDir = Path.Combine(root, name);
        Directory.CreateDirectory(projectDir);

        var useWpfProperty = useWpf ? "    <UseWPF>true</UseWPF>" + Environment.NewLine : string.Empty;
        var useWindowsFormsProperty = useWindowsForms ? "    <UseWindowsForms>true</UseWindowsForms>" + Environment.NewLine : string.Empty;
        var isTestProperty = isTest ? "    <IsTestProject>true</IsTestProject>" + Environment.NewLine : string.Empty;

        var projectPath = Path.Combine(projectDir, name + ".csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"" + sdk + "\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>" + targetFramework + "</TargetFramework>\n" +
            isTestProperty + useWpfProperty + useWindowsFormsProperty +
            "  </PropertyGroup>\n" +
            "</Project>\n");
        File.WriteAllText(
            Path.Combine(projectDir, "Class1.cs"),
            "namespace " + name + ";\n\n" +
            "public class Class1\n" +
            "{\n" +
            "    public int Value { get; set; }\n" +
            "}\n");
        return projectPath;
    }

    private static string CreateSolution(string root, string fileName, IReadOnlyList<string> projectPaths)
    {
        var lines = new List<string>
        {
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
        };

        foreach (var projectPath in projectPaths)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var relativePath = Path.GetRelativePath(root, projectPath).Replace(Path.DirectorySeparatorChar, '\\');
            lines.Add($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{relativePath}\", \"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}\"");
            lines.Add("EndProject");
        }

        var solutionPath = Path.Combine(root, fileName);
        File.WriteAllLines(solutionPath, lines);
        return solutionPath;
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
        var path = Path.Combine(Path.GetTempPath(), "cs2j-preprocess-consistent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
