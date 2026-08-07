using CSharpToJava.CLI;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 40 CLI 工程级接线（spec §6.1/6.2）：convert-project -t typescript 输出
/// npm workspace（modules/&lt;模块&gt;/src/&lt;命名空间&gt;/…，测试类 .test.ts），
/// 兼容库嵌入（&lt;dest&gt;/compat，依赖改相对 file: 路径），转换后自动验证链
/// （npm install → npx tsc --noEmit -p 每个模块 → 测试包 vitest run；
/// --no-verify-build/--no-verify-tests 关闭，失败即中止）。
/// </summary>
public class TsProjectCliTests
{
    [Fact]
    public async Task ConvertProject_TypeScriptTarget_Writes_Npm_Workspace_Structure()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "src");
        var destinationRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Calc.cs"), """
            namespace App
            {
                public class Calc
                {
                    public int Add(int a, int b) => a + b;
                }
            }
            """);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "CalcTests.cs"), """
            using Xunit;
            namespace App
            {
                public class CalcTests
                {
                    [Fact]
                    public void Add_AddsTwoNumbers()
                    {
                        var calc = new Calc();
                        Assert.Equal(3, calc.Add(1, 2));
                    }
                }
            }
            """);

            var exit = await Program.MainImpl([
                "convert-project",
                "-t", "typescript",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-verify-build",
                "--no-verify-tests",
            ]);

            Assert.Equal(0, exit);

            // 模块布局：命名空间 → 目录；测试类 → .test.ts（Task 39）
            var moduleRoot = Path.Combine(destinationRoot, "modules", "out");
            Assert.True(File.Exists(Path.Combine(moduleRoot, "src", "App", "Calc.ts")),
                $"Missing {Path.Combine(moduleRoot, "src", "App", "Calc.ts")}");
            Assert.True(File.Exists(Path.Combine(moduleRoot, "src", "App", "CalcTests.test.ts")),
                $"Missing {Path.Combine(moduleRoot, "src", "App", "CalcTests.test.ts")}");

            // 工作区根文件（spec §6.1）
            Assert.True(File.Exists(Path.Combine(destinationRoot, "package.json")));
            Assert.True(File.Exists(Path.Combine(destinationRoot, "tsconfig.base.json")));
            var rootPackage = await File.ReadAllTextAsync(Path.Combine(destinationRoot, "package.json"));
            Assert.Contains("modules/*", rootPackage, StringComparison.Ordinal);
            // 兼容库嵌入 → 工作区成员
            Assert.Contains("compat", rootPackage, StringComparison.Ordinal);

            // 模块文件：package.json（compat workspace:* + vitest）/ tsconfig / vitest 配置
            var modulePackage = await File.ReadAllTextAsync(Path.Combine(moduleRoot, "package.json"));
            Assert.Contains("@csharptotypescript/compat", modulePackage, StringComparison.Ordinal);
            Assert.Contains("\"@csharptotypescript/compat\": \"file:../../compat\"", modulePackage, StringComparison.Ordinal);
            Assert.Contains("vitest", modulePackage, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(moduleRoot, "tsconfig.json")));
            Assert.True(File.Exists(Path.Combine(moduleRoot, "vitest.config.ts")));

            // 兼容库嵌入产物（dist 保留供 tsc 类型解析）
            Assert.True(File.Exists(Path.Combine(destinationRoot, "compat", "package.json")));
            Assert.True(File.Exists(Path.Combine(destinationRoot, "compat", "dist", "index.d.ts")));

            // 生成代码内容抽查
            var emitted = await File.ReadAllTextAsync(Path.Combine(moduleRoot, "src", "App", "CalcTests.test.ts"));
            Assert.Contains("describe(\"CalcTests\", () => {", emitted, StringComparison.Ordinal);
            Assert.Contains("expect(calc.add(1, 2)).toEqual(3);", emitted, StringComparison.Ordinal);
            Assert.Contains("from \"vitest\"", emitted, StringComparison.Ordinal);
        }
        finally
        {
            ForceDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// M5 验收（spec §6.2）：验证链端到端 —— npm install → npx tsc --noEmit -p 模块
    /// → 测试包 vitest run 全通过。无 Node/npm 环境时空转通过（对齐 L3 测试约定）。
    /// </summary>
    [Fact]
    public async Task ConvertProject_TypeScriptTarget_Verification_Chain_Runs_Green()
    {
        if (!IsNpmAvailable())
        {
            // xunit v2 普通 [Fact] 无法动态跳过：无 Node 时空转通过，避免误红（对齐 L3 约定）
            return;
        }

        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "src");
        var destinationRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Calc.cs"), """
            namespace App
            {
                public class Calc
                {
                    public int Add(int a, int b) => a + b;
                }
            }
            """);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "CalcTests.cs"), """
            using Xunit;
            namespace App
            {
                public class CalcTests
                {
                    [Fact]
                    public void Add_AddsTwoNumbers()
                    {
                        var calc = new Calc();
                        Assert.Equal(3, calc.Add(1, 2));
                    }

                    [Theory]
                    [InlineData(1, 2, 3)]
                    [InlineData(2, 3, 5)]
                    public void Add_Theory(int a, int b, int expected)
                    {
                        var calc = new Calc();
                        Assert.Equal(expected, calc.Add(a, b));
                    }
                }
            }
            """);

            // 不传 --no-verify-*：完整验证链（npm install + tsc + vitest）真实运行
            var exit = await Program.MainImpl([
                "convert-project",
                "-t", "typescript",
                "-s", sourceRoot,
                "-d", destinationRoot,
            ]);

            Assert.True(exit == 0,
                $"Verification chain failed (exit {exit}). Check destination: {destinationRoot}");
            // 验证链产物：node_modules 由 npm install 生成
            Assert.True(Directory.Exists(Path.Combine(destinationRoot, "node_modules")),
                "npm install did not produce node_modules at workspace root");
        }
        finally
        {
            ForceDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// Task 40 跨模块接线（spec §3.3）：被引用模块生成 src/index.ts barrel +
    /// package.json main/types（tsc/vitest 按 TS 源码解析），引用方发射
    /// workspace 包名导入并写 workspace 依赖（file:../&lt;模块&gt;）。
    /// </summary>
    [Fact]
    public async Task ConvertProject_TypeScriptTarget_Cross_Module_Reference_Writes_Entry_Point()
    {
        var tempRoot = CreateTempDirectory();
        var coreDir = Path.Combine(tempRoot, "Sample.Core");
        var appDir = Path.Combine(tempRoot, "Sample.App");
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(appDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(coreDir, "Sample.Core.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
            await File.WriteAllTextAsync(Path.Combine(coreDir, "Calc.cs"), """
            namespace Sample.Core
            {
                public class Calc
                {
                    public int Add(int a, int b) => a + b;
                }
            }
            """);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Sample.App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Sample.Core\Sample.Core.csproj" />
              </ItemGroup>
            </Project>
            """);
            await File.WriteAllTextAsync(Path.Combine(appDir, "Use.cs"), """
            using Sample.Core;
            namespace Sample.App
            {
                public class Use
                {
                    public int Go() => new Calc().Add(1, 2);
                }
            }
            """);

            var destinationRoot = Path.Combine(tempRoot, "out");
            var exit = await Program.MainImpl([
                "convert-project",
                "-t", "typescript",
                "-s", Path.Combine(appDir, "Sample.App.csproj"),
                "-d", destinationRoot,
                "--no-verify-build",
                "--no-verify-tests",
            ]);

            Assert.True(exit == 0, $"Conversion failed (exit {exit}). Check destination: {destinationRoot}");

            // 引用方：workspace 包名导入 + workspace 依赖（file:../sample-core）
            var useTs = await File.ReadAllTextAsync(
                Path.Combine(destinationRoot, "modules", "sample-app", "src", "Sample", "App", "Use.ts"));
            Assert.Contains("import { Calc } from \"sample-core\";", useTs, StringComparison.Ordinal);
            var appPackage = await File.ReadAllTextAsync(
                Path.Combine(destinationRoot, "modules", "sample-app", "package.json"));
            Assert.Contains("\"sample-core\": \"file:../sample-core\"", appPackage, StringComparison.Ordinal);

            // 被引用模块：barrel 入口 + package.json main/types
            var indexTs = await File.ReadAllTextAsync(
                Path.Combine(destinationRoot, "modules", "sample-core", "src", "index.ts"));
            Assert.Contains("export * from \"./Sample/Core/Calc.js\";", indexTs, StringComparison.Ordinal);
            var corePackage = await File.ReadAllTextAsync(
                Path.Combine(destinationRoot, "modules", "sample-core", "package.json"));
            Assert.Contains("\"main\": \"./src/index.ts\"", corePackage, StringComparison.Ordinal);
            Assert.Contains("\"types\": \"./src/index.ts\"", corePackage, StringComparison.Ordinal);
        }
        finally
        {
            ForceDeleteDirectory(tempRoot);
        }
    }
    /// <summary>递归删除目录：npm 产物含只读文件与 workspace 符号链接（node_modules
    /// 内 @csharptotypescript/compat → ../../compat 联接），Directory.Delete 递归会
    /// 跟随链接循环或对只读项抛 UnauthorizedAccessException——先清只读属性、链接
    /// 只删链接本身不跟随目标。</summary>
    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 符号链接/联接：删除链接本身，不跟随目标
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                ForceDeleteDirectory(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: false);
    }
    private static bool IsNpmAvailable()
    {
        var name = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv == null)
        {
            return false;
        }

        return pathEnv.Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, name)));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-ts-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
