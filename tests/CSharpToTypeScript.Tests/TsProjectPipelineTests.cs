using CSharpToJava.Core;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 38 ProjectConversionPipeline TS 分叉测试（spec §2.4）：多文件项目转换 →
/// 每类型一个 .ts 结果；语法树层 rewriter 跨文件生效（重载改名 + 调用点同步）；
/// NativeInterop 检查降级为警告不阻断；NpmDependencyPass 收集 compat 依赖；
/// Java 路径输出不变（零回归守卫）。
/// </summary>
public class TsProjectPipelineTests
{
    private static ConversionOptions CreateOptions(TargetLanguage target = TargetLanguage.TypeScript)
    {
        var basePath = AppContext.BaseDirectory;
        return new ConversionOptions
        {
            TargetLanguage = target,
            TypeMappingConfigPath = Path.Combine(basePath, "config", "TypeMappings.json"),
            TypeScriptMappingConfigPath = Path.Combine(basePath, "config", "typescript", "TypeMappings.json"),
            TypeScriptMemberMappingPath = Path.Combine(basePath, "config", "typescript", "MemberMappings.json"),
        };
    }

    private static async Task<List<ConversionResult>> ConvertProjectAsync(params (string FilePath, string Content)[] files)
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        return await pipeline.ConvertProjectAsync(files
            .Select(file => new SourceFile { FilePath = file.FilePath, Content = file.Content }));
    }

    [Fact]
    public async Task Project_Conversion_Emits_Ts_File_Per_Type()
    {
        var results = await ConvertProjectAsync(
            ("Point.cs", "namespace App.Models { public class Point { public int X { get; set; } } }"),
            ("IShape.cs", "namespace App.Models { public interface IShape { double Area(); } }"));

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message))));
        Assert.All(results, result => Assert.EndsWith(".ts", result.FileName, StringComparison.Ordinal));
        Assert.Contains(results, result => result.GeneratedCode.Contains("export class Point", StringComparison.Ordinal));
        Assert.Contains(results, result => result.GeneratedCode.Contains("export interface IShape", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overload_Rename_Applies_Across_Files()
    {
        var results = await ConvertProjectAsync(
            ("Calc.cs", """
namespace App
{
    public class Calc
    {
        public string Fmt(int x) { return "int"; }
        public string Fmt(string s) { return s; }
    }
}
"""),
            ("Use.cs", """
namespace App
{
    public class Use
    {
        public string Go()
        {
            var c = new Calc();
            return c.Fmt("hi");
        }
    }
}
"""));

        var calcResult = Assert.Single(results, result => result.FileName == "Calc.ts");
        Assert.True(calcResult.Success, string.Join("; ", calcResult.Diagnostics.Select(d => d.Message)));
        // 重载组内稳定序首个保留原名，string 重载 → fmt_string
        Assert.Contains("fmt_string(s: string)", calcResult.GeneratedCode, StringComparison.Ordinal);

        // 跨文件调用点同步改写（预收集 + 逐文件 Visit）
        var useResult = Assert.Single(results, result => result.FileName == "Use.ts");
        Assert.True(useResult.Success, string.Join("; ", useResult.Diagnostics.Select(d => d.Message)));
        Assert.Contains("c.fmt_string(\"hi\")", useResult.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("c.Fmt(", useResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeInterop_Is_Downgraded_To_Warning_For_Ts()
    {
        var results = await ConvertProjectAsync(
            ("Native.cs", """
using System.Runtime.InteropServices;
namespace App
{
    public class Native
    {
        [DllImport("user32.dll")]
        public static extern int MessageBox(int hWnd, string text, string caption, int type);
    }
}
"""));

        var result = Assert.Single(results);
        // TS 目标：NativeInterop 降级为警告，不阻断发射（Task 38，spec §2.4）
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning
            && (d.Message.Contains("DllImport", StringComparison.OrdinalIgnoreCase)
                || d.Message.Contains("Extern method", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task NpmDependencyPass_Collects_Compat_Import()
    {
        var results = await ConvertProjectAsync(
            ("Text.cs", """
namespace App
{
    public class Text
    {
        public int Count(string s)
        {
            string[] parts = s.Split(',');
            return parts.Length;
        }
    }
}
"""));

        var result = Assert.Single(results);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        // Task 36 成员映射 → CsString.split + compat 导入；NpmDependencyPass 收集包名
        Assert.Contains("CsString.split(s, ',')", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("@csharptotypescript/compat", result.NpmPackageDependencies ?? new System.Collections.Generic.HashSet<string>());
    }

    [Fact]
    public async Task Test_Project_Emits_Test_Ts_Files_With_Vitest_Structure()
    {
        var results = await ConvertProjectAsync(
            ("CalcTests.cs", """
using Xunit;
namespace App.Tests
{
    public class CalcTests
    {
        private readonly Calc _calc = new Calc();

        [Fact]
        public void Add_AddsTwoNumbers()
        {
            Assert.Equal(3, _calc.Add(1, 2));
        }

        [Theory]
        [InlineData(2, 3, 5)]
        public void Add_Theory(int a, int b, int expected)
        {
            Assert.Equal(expected, _calc.Add(a, b));
        }
    }
}
"""),
            ("Calc.cs", """
namespace App.Tests
{
    public class Calc
    {
        public int Add(int a, int b) => a + b;
    }
}
"""));

        // 测试类单元 → .test.ts（vitest include 模式 src/**/*.test.ts）+ describe/it 结构
        var testResult = Assert.Single(results, result => result.FileName == "CalcTests.test.ts");
        Assert.True(testResult.Success, string.Join("; ", testResult.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import { describe, expect, it } from \"vitest\";", testResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("describe(\"CalcTests\", () => {", testResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("let _calc: Calc = new Calc();", testResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("expect(_calc.add(1, 2)).toEqual(3);", testResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("it.each([", testResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("[2, 3, 5],", testResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("])(\"Add_Theory\", (a, b, expected) => {", testResult.GeneratedCode, StringComparison.Ordinal);
        // NpmDependencyPass 收集 vitest 依赖
        Assert.Contains("vitest", testResult.NpmPackageDependencies ?? new System.Collections.Generic.HashSet<string>());

        // 普通类维持 .ts + class 发射
        var calcResult = Assert.Single(results, result => result.FileName == "Calc.ts");
        Assert.True(calcResult.Success, string.Join("; ", calcResult.Diagnostics.Select(d => d.Message)));
        Assert.Contains("export class Calc {", calcResult.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("vitest", calcResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IsTestProject_Flag_Flows_To_Library()
    {
        var sourceFiles = new List<SourceFile>
        {
            new() { FilePath = "T.cs", Content = "namespace App { public class T { } }" },
        };
        var context = new ConversionContext(CreateOptions(), null);
        var compilation = ProjectCompilationBuilder.BuildCompilation(sourceFiles, context);
        Assert.NotNull(compilation);

        var library = Cs2jLibraryFactory.CreateFromSourceFiles(sourceFiles, compilation, isTestProject: true);
        Assert.Single(library.Projects);
        Assert.True(library.Projects[0].IsTestProject);

        var regular = Cs2jLibraryFactory.CreateFromSourceFiles(sourceFiles, compilation, isTestProject: false);
        Assert.False(regular.Projects[0].IsTestProject);
    }

    [Fact]
    public async Task Java_Target_Still_Emits_Java_Files()
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions(TargetLanguage.Java));
        var results = await pipeline.ConvertProjectAsync(
        [
            new SourceFile { FilePath = "A.cs", Content = "namespace App { public class A { public int X { get; set; } } }" },
        ]);

        var result = Assert.Single(results);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.EndsWith(".java", result.FileName, StringComparison.Ordinal);
        Assert.Contains("public class A", result.GeneratedCode, StringComparison.Ordinal);
    }
}
