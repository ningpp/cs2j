using System.Text.RegularExpressions;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for the C21 anonymous-type-lift pass. The
/// SampleAnonymousTypeLiftSolution fixture (Lib + App + xUnit Tests) exercises
/// cross-project same-shape independent internal copies, LINQ method/query
/// mixes, transparent-identifier exemption, implicit arrays, generic/nested/
/// dynamic shapes and the lifted-class runtime behavior verified by
/// dotnet test on the intermediate tree.
/// </summary>
public class AnonymousTypeLiftProjectTests
{
    private const string PassId = "anonymous-type-lift";
    private const string IntermediateDirName = ".cs2j-anonymous-type-lift-src";
    private const string GeneratedPrefix = "_Cs2jAnonymousTypes";

    private static readonly string[] FixtureProjects =
        { "SampleAnonLib", "SampleAnonApp", "SampleAnonTests" };

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleAnonymousTypeLiftSolution");

    private static Task<CliChainResult> RunChainAsync(
        string sourcePath,
        string outputRoot,
        IReadOnlyCollection<string>? enablePassIds,
        bool verifyBuild = true,
        bool verifyTests = true)
    {
        var registry = PassCatalog.BuildRegistry();
        return CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () =>
                new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = enablePassIds,
            VerifyBuild = verifyBuild,
            VerifyTests = verifyTests,
        });
    }

    [Fact]
    public async Task EnabledChain_LiftsAcrossProjects_VerifiesBuildAndTests_AndIsIdempotent()
    {
        var outputRoot1 = Path.Combine(FixtureDir, ".test-output-anon-1");
        var outputRoot2 = Path.Combine(FixtureDir, ".test-output-anon-2");
        // CliChainResult 不暴露逐 pass 诊断；adapter 的 warning 经 Console.Error 输出。
        // 捕获链期间 stderr，钉死 §2.4 AL020 不得对表达式上下文发误报。
        var errorCapture = new StringWriter();
        var originalError = Console.Error;
        try
        {
            Console.SetError(errorCapture);
            var result = await RunChainAsync(
                Path.Combine(FixtureDir, "SampleAnonymousTypeLiftSolution.sln"),
                outputRoot1,
                new[] { PassId });

            Assert.True(
                result.Success,
                $"anonymous-type-lift chain failed; executed directories: " +
                $"{string.Join(", ", result.ExecutedIntermediateDirectoryNames)}");
            Assert.Contains(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot1, IntermediateDirName);
            Assert.True(Directory.Exists(intermediate), $"missing intermediate tree: {intermediate}");

            // ---- 多项目分发：根目录生成文件 + csproj Compile Include/Link ----
            var crossProjectShapeName = await AssertDistributedFilesAndReadShapeName(intermediate);

            // ---- §7.4 residual 闸门：业务文件零匿名创建语法 ----
            AssertZeroResidualAnonymousCreations(intermediate);

            // ---- 跨工程独立副本：同形 Name+Length 类名跨文件一致 ----
            var libGenerated = await File.ReadAllTextAsync(
                Path.Combine(intermediate, $"{GeneratedPrefix}.SampleAnonLib.cs"));
            var appGenerated = await File.ReadAllTextAsync(
                Path.Combine(intermediate, $"{GeneratedPrefix}.SampleAnonApp.cs"));
            Assert.Contains(crossProjectShapeName, libGenerated);
            Assert.Contains(crossProjectShapeName, appGenerated);

            // ---- 幂等：以第一次中间树为源再跑一次（独立输出根，避免 Force 清除源树）----
            var secondResult = await RunChainAsync(result.FinalSourcePath, outputRoot2, new[] { PassId });
            Assert.True(
                secondResult.Success,
                $"idempotent re-run failed; executed directories: " +
                $"{string.Join(", ", secondResult.ExecutedIntermediateDirectoryNames)}");
            Assert.Contains(IntermediateDirName, secondResult.ExecutedIntermediateDirectoryNames);

            var secondIntermediate = Path.Combine(outputRoot2, IntermediateDirName);
            foreach (var project in FixtureProjects)
            {
                var fileName = $"{GeneratedPrefix}.{project}.cs";

                // 每个项目的生成文件仍恰好一份（不出现 _2 叠加副本）。
                var copies = Directory.EnumerateFiles(secondIntermediate, fileName, SearchOption.AllDirectories)
                    .Where(path => !IsBuildArtifactPath(secondIntermediate, path))
                    .ToList();
                Assert.True(
                    copies.Count == 1,
                    $"expected exactly one {fileName} after re-run, got {copies.Count}: " +
                    $"{string.Join("; ", copies)}");

                // csproj 中 Compile Include/Link 各恰好一条（EnsureCompileItem 先去重再追加）。
                var csprojText = await File.ReadAllTextAsync(
                    Path.Combine(secondIntermediate, project, $"{project}.csproj"));
                var expectedInclude = Path.Combine("..", fileName);
                Assert.True(
                    CountOccurrences(csprojText, $"Include=\"{expectedInclude}\"") == 1,
                    $"duplicate Compile Include for {fileName} after re-run");
                Assert.True(
                    CountOccurrences(csprojText, $"Link=\"{fileName}\"") == 1,
                    $"duplicate Compile Link for {fileName} after re-run");
            }

            AssertZeroResidualAnonymousCreations(secondIntermediate);

            // §2.4 闸门零噪声：整条链（含幂等二次运行）的 stderr 中不得出现
            // AL020「explicit type position(s)」聚合 warning（表达式上下文误报）。
            var capturedErrors = errorCapture.ToString();
            Assert.DoesNotContain("explicit type position(s)", capturedErrors);
        }
        finally
        {
            Console.SetError(originalError);
            foreach (var outputRoot in new[] { outputRoot1, outputRoot2 })
            {
                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(outputRoot, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task DefaultOffChain_DoesNotTouchTree_AndReturnsInputPath()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-anon-off");
        var solutionPath = Path.GetFullPath(Path.Combine(FixtureDir, "SampleAnonymousTypeLiftSolution.sln"));
        try
        {
            // 全量默认关，不设任何 EnablePassIds：空计划不落树。
            var result = await RunChainAsync(solutionPath, outputRoot, enablePassIds: null);

            Assert.True(
                result.Success,
                $"default-off chain failed; executed directories: " +
                $"{string.Join(", ", result.ExecutedIntermediateDirectoryNames)}");
            Assert.DoesNotContain(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);
            Assert.Equal(solutionPath, Path.GetFullPath(result.FinalSourcePath));
            Assert.False(
                Directory.Exists(Path.Combine(outputRoot, IntermediateDirName)),
                "default-off run must not materialize the c21 intermediate tree");
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TupleThenAnonymousChain_ComposesInLockedTopologicalOrder_AndFinalTreeBuilds()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-c21-chain-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "CrossShapes.cs"),
                """
                namespace Cross;

                public static class Uses
                {
                    // 元组作匿名属性：tuple 先把 (1, "x") 变 struct，c21 见到的属性类型已是具名 struct。
                    public static object TupleAsAnonymousProperty()
                        => new[] { 1 }.Select(x => new { T = (1, "x"), N = 2 }).First();

                    // 匿名投影作元组元素：元组 struct 的类型实参随后由 c21 绑定到提升类。
                    public static object AnonymousAsTupleElement()
                        => new[] { 1, 2 }.Select(x => (new { X = x }, x + 1)).ToArray();
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Cross.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
                "    <TargetFramework>net10.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");

            // 故意反序传 id：执行顺序由注册表拓扑锁定（tuple → anonymous-type-lift）。
            var result = await RunChainAsync(
                Path.Combine(sourceDir, "Cross.csproj"),
                outputDir,
                new[] { PassIds.AnonymousTypeLift, PassIds.Tuple });

            Assert.True(
                result.Success,
                $"tuple+c21 chain failed; executed directories: " +
                $"{string.Join(", ", result.ExecutedIntermediateDirectoryNames)}");
            Assert.Contains(".cs2j-tuple-src", result.ExecutedIntermediateDirectoryNames);
            Assert.Contains(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);

            var finalRoot = Path.Combine(outputDir, IntermediateDirName);
            Assert.True(File.Exists(Path.Combine(finalRoot, "_Cs2jTupleTypes.cs")),
                "tuple generated struct file missing from the final intermediate tree");
            Assert.True(File.Exists(Path.Combine(finalRoot, "_Cs2jAnonymousTypes.cs")),
                "anonymous generated class file missing from the final intermediate tree");

            // 业务文件中两种语法残余均为 0（生成文件自身不在扫描范围）。
            foreach (var file in Directory.EnumerateFiles(finalRoot, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !IsBuildArtifactPath(finalRoot, path))
                         .Where(path => !IsGeneratedHelperFile(path)))
            {
                var text = await File.ReadAllTextAsync(file);
                var root = CSharpSyntaxTree.ParseText(text).GetRoot();
                var anonymousCreations = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousObjectCreationExpressionSyntax>()
                    .Count();
                var tupleNodes = root.DescendantNodes()
                    .Count(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.TupleTypeSyntax
                        or Microsoft.CodeAnalysis.CSharp.Syntax.TupleExpressionSyntax);
                Assert.True(anonymousCreations == 0,
                    $"{Path.GetFileName(file)} still contains {anonymousCreations} anonymous creation(s)");
                Assert.True(tupleNodes == 0,
                    $"{Path.GetFileName(file)} still contains {tupleNodes} tuple node(s)");
            }

            // 对最终中间树根目标再跑一次 dotnet build（仿 TupleToStructProjectTests）。
            var buildExitCode = await RunDotNetBuildAsync(result.FinalSourcePath);
            Assert.Equal(0, buildExitCode);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// 校验三工程的生成文件落盘与 csproj Include/Link 文本，并返回跨工程同形
    /// （Name+Length）类名供跨文件一致性断言。
    /// </summary>
    private static async Task<string> AssertDistributedFilesAndReadShapeName(string intermediate)
    {
        string? crossProjectShapeName = null;
        foreach (var project in FixtureProjects)
        {
            var fileName = $"{GeneratedPrefix}.{project}.cs";

            // 落盘位置：中间树根目录（与 TuplePassAdapter.DistributeStructFiles 同款）。
            var generatedFile = Path.Combine(intermediate, fileName);
            Assert.True(File.Exists(generatedFile), $"missing generated file: {generatedFile}");

            var content = await File.ReadAllTextAsync(generatedFile);
            Assert.Contains("internal sealed class", content);
            Assert.DoesNotContain("public sealed class", content);
            Assert.DoesNotContain("record", content);
            Assert.Contains("Equals", content);
            Assert.Contains("GetHashCode", content);
            Assert.Contains("ToString", content);

            var csprojPath = Path.Combine(intermediate, project, $"{project}.csproj");
            Assert.True(File.Exists(csprojPath), $"missing intermediate project: {csprojPath}");
            var csprojText = await File.ReadAllTextAsync(csprojPath);

            // 实测 Include/Link 文本：生成文件位于中间树根，项目在子目录，故 Include 为 ../文件名。
            var expectedInclude = Path.Combine("..", fileName);
            Assert.Contains($"Include=\"{expectedInclude}\"", csprojText);
            Assert.Contains($"Link=\"{fileName}\"", csprojText);

            if (project is "SampleAnonLib" or "SampleAnonApp")
            {
                var shapeName = ExtractNameLengthShapeName(content);
                Assert.NotNull(shapeName);
                if (crossProjectShapeName == null)
                {
                    crossProjectShapeName = shapeName;
                }
                else
                {
                    Assert.Equal(crossProjectShapeName, shapeName);
                }
            }
        }

        Assert.NotNull(crossProjectShapeName);
        return crossProjectShapeName!;
    }

    /// <summary>从生成文件中提取 Name+Length 形状的类名（属性顺序 Name→Length）。</summary>
    private static string? ExtractNameLengthShapeName(string generatedContent)
    {

        var matches = Regex.Matches(generatedContent, @"internal sealed class (Anon_[A-Za-z0-9_]+)");
        return matches.Select(m => m.Groups[1].Value)
            .FirstOrDefault(name => name.Contains("_Name_") && name.Contains("_Length_"));
    }

    private static void AssertZeroResidualAnonymousCreations(string intermediate)
    {
        foreach (var file in Directory.EnumerateFiles(intermediate, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !IsBuildArtifactPath(intermediate, path))
                     .Where(path => !IsGeneratedHelperFile(path)))
        {
            var text = File.ReadAllText(file);
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            var residual = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousObjectCreationExpressionSyntax>()
                .Count();
            Assert.True(residual == 0,
                $"{Path.GetRelativePath(intermediate, file)} still contains " +
                $"{residual} AnonymousObjectCreationExpressionSyntax node(s)");
        }
    }

    /// <summary>
    /// 判断文件是否位于中间树根内部的 bin/obj 构建产物目录。必须按相对中间树根的
    /// 路径分段判断：fixture 经 Content 复制到测试工程的 bin 输出目录，绝对路径恒含
    /// bin 段，按绝对路径过滤会把全部业务文件误判为产物。
    /// </summary>
    private static bool IsBuildArtifactPath(string root, string path)
        => Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsGeneratedHelperFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith(GeneratedPrefix, StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("_Cs2jTupleTypes", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
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
        var output = await process!.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            Console.WriteLine(output);
            Console.Error.WriteLine(error);
        }

        return process.ExitCode;
    }
}
