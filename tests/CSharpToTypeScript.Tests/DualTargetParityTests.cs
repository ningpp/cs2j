using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 41 DualTargetParityTests（spec §5.7）：同一批 M5 语料（tests/DualTargetCorpus）
/// 分别以 Java/TS 目标转换，断言两者均成功且关键结构特征对齐——类型数、类型名集合、
/// 每类型公共方法面、错误诊断数（均为 0）——防止 TS 分支改动意外污染共享前端；
/// 同时是 Java 路径的回归安全网。TS 侧另断言产物结构（.ts / export 声明 / compat 导入）。
/// </summary>
public class DualTargetParityTests
{
    private static ConversionOptions CreateOptions(TargetLanguage target)
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

    private static List<SourceFile> LoadCorpusFiles()
    {
        var root = FindCorpusRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new SourceFile
            {
                FilePath = Path.GetRelativePath(root, path),
                Content = File.ReadAllText(path),
            })
            .ToList();
    }

    private static string FindCorpusRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "DualTargetCorpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "tests/DualTargetCorpus not found from test output directory " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task Corpus_Converts_With_Both_Targets_And_Parity()
    {
        var files = LoadCorpusFiles();
        Assert.True(files.Count >= 10, $"Corpus unexpectedly small: {files.Count} files");

        var javaResults = await new ProjectConversionPipeline(CreateOptions(TargetLanguage.Java))
            .ConvertProjectAsync(files);
        var tsResults = await new ProjectConversionPipeline(CreateOptions(TargetLanguage.TypeScript))
            .ConvertProjectAsync(files);

        // 类型数对齐（同语料两目标产出单元数一致，且不小于语料文件数——多类型文件展开）
        Assert.Equal(javaResults.Count, tsResults.Count);
        Assert.True(javaResults.Count >= files.Count, $"Expected >= {files.Count} type units, got {javaResults.Count}");

        // 类型名集合对齐（.java ↔ .ts 基名一致，忽略测试类后缀差异）
        var javaNames = javaResults
            .Select(r => Path.GetFileNameWithoutExtension(r.FileName!.Replace(".test", "")))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var tsNames = tsResults
            .Select(r => Path.GetFileNameWithoutExtension(r.FileName!.Replace(".test", "")))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(javaNames, tsNames);

        // 全部成功 + 0 错误诊断（parity：两侧错误诊断数均为 0）
        foreach (var result in javaResults.Concat(tsResults))
        {
            Assert.True(result.Success, $"Conversion failed for {result.FileName}: "
                + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }

        // 每类型公共方法面在两侧产物中都存在（Java 保留原名；TS 保留组内首个重载名，
        // 属性访问器/枚举/常量等非方法成员不在此列）
        var methodsByType = ExtractPublicMethodsByType(files);
        foreach (var (typeName, methods) in methodsByType)
        {
            var javaCode = CodeFor(javaResults, typeName);
            var tsCode = CodeFor(tsResults, typeName);
            // 测试类方法在 TS 侧只出现在 it("...") 标签（不发射方法签名），用子串断言；
            // 普通类型两侧均断言方法签名 name( 存在（重载组首个保留原名）。
            var isTestUnit = tsCode.Contains("describe(", StringComparison.Ordinal);
            foreach (var method in methods)
            {
                Assert.True(javaCode.Contains(method + "(", StringComparison.OrdinalIgnoreCase), $"Java output missing method {typeName}.{method}");
                if (isTestUnit)
                {
                    Assert.True(tsCode.Contains(method, StringComparison.OrdinalIgnoreCase), $"TS test output missing method {typeName}.{method}");
                }
                else
                {
                    Assert.True(
                            tsCode.Contains(method + "(", StringComparison.OrdinalIgnoreCase)
                            || tsCode.Contains(method + "<", StringComparison.OrdinalIgnoreCase),
                        $"TS output missing method {typeName}.{method}");
                }
            }
        }

        // TS 产物结构：.ts 扩展名 + export 声明 + compat 导入
        foreach (var result in tsResults)
        {
            Assert.EndsWith(".ts", result.FileName!, StringComparison.Ordinal);
            Assert.Contains("export ", result.GeneratedCode, StringComparison.Ordinal);
            Assert.Contains("@csharptotypescript/compat", result.GeneratedCode, StringComparison.Ordinal);
        }

        // Java 产物结构：.java + public 声明
        foreach (var result in javaResults)
        {
            Assert.EndsWith(".java", result.FileName!, StringComparison.Ordinal);
            Assert.Contains("public ", result.GeneratedCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Corpus_Test_Files_Map_To_Vitest_Structure()
    {
        var files = LoadCorpusFiles();
        var tsResults = await new ProjectConversionPipeline(CreateOptions(TargetLanguage.TypeScript))
            .ConvertProjectAsync(files);

        foreach (var result in tsResults.Where(r => r.FileName!.EndsWith(".test.ts", StringComparison.Ordinal)))
        {
            Assert.Contains("describe(", result.GeneratedCode, StringComparison.Ordinal);
            Assert.Contains("from \"vitest\"", result.GeneratedCode, StringComparison.Ordinal);
            Assert.True(result.NpmPackageDependencies?.Contains("vitest") == true, "vitest missing in npm deps");
        }

        // M5 语料：两个测试类（CalculatorTests/FeatureTests）均映射为 vitest 单测
        Assert.Equal(2, tsResults.Count(r => r.FileName!.EndsWith(".test.ts", StringComparison.Ordinal)));
    }

    private static string CodeFor(List<ConversionResult> results, string typeName)
    {
        var match = results.FirstOrDefault(r =>
            Path.GetFileNameWithoutExtension(r.FileName!.Replace(".test", "")) == typeName);
        Assert.NotNull(match);
        return match!.GeneratedCode;
    }

    /// <summary>解析语料 C# 源码：类型名 → 该类型声明的公共方法名（属性/字段/构造器不计）。</summary>
    private static Dictionary<string, string[]> ExtractPublicMethodsByType(List<SourceFile> files)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var tree = CSharpSyntaxTree.ParseText(file.Content);
            var root = tree.GetCompilationUnitRoot();
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var methods = type.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Modifiers.Any(t => t.Kind() == SyntaxKind.PublicKeyword))
                    .Select(m => m.Identifier.Text)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (methods.Length > 0)
                {
                    result[type.Identifier.Text] = methods;
                }
            }
        }
        return result;
    }
}