using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// java-keywords 阶段作用域化回退（JK-4d）必须按「项目 + ProjectReference 闭包」重建
/// 作用域编译。真实 SDK 里 regex tests 的调用文件所在项目只直接包含 tests 目录，
/// 被调用实参的类型（RegexCompilationInfo）来自被引用的 src 项目；若回退编译漏掉
/// 引用闭包，实参 lambda 的方法体无法绑定，Throws 的重载解析退化为候选集合，
/// 回退只好取第一个候选，把 (string, Action) 调用点改成 (string, Func&lt;object&gt;)
/// 重载的新名 → 目标树 CS1662/CS0029。
/// </summary>
public class JavaKeywordScopedReferenceClosureTests
{
    private readonly ITestOutputHelper _output;
    public JavaKeywordScopedReferenceClosureTests(ITestOutputHelper output) => _output = output;

    private static IEnumerable<MetadataReference> GetReferences()
    {
        // 仅引用 CoreLib 同目录的托管运行时程序集，避免 native DLL（CS0009）与
        // 测试宿主程序集（其 using 解析会掩盖真实管线行为）。
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var assembly in Directory.EnumerateFiles(runtimeDir, "*.dll"))
        {
            var name = Path.GetFileName(assembly);
            if (name.Contains("Native", StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith("System", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return MetadataReference.CreateFromFile(assembly);
        }
    }

    private const string WidgetSource = """
namespace dotnet.text.regularexpressions
{
    public class Widget
    {
        public void set_Timeout(int value) { }
    }
}
""";

    private const string AssertExtensionsSource = """
namespace System.Tests
{
    public static class AssertExtensions
    {
        public static T Throws<T>(string paramName, System.Action testCode) where T : System.ArgumentException => throw null!;
        public static T Throws<T>(string paramName, string beginsWith, System.Action testCode) where T : System.ArgumentException => throw null!;
        public static T Throws<T>(string paramName, System.Func<object> testCode) where T : System.ArgumentException => throw null!;
        public static T Throws<T>(string paramName, string beginsWith, System.Func<object> testCode) where T : System.ArgumentException => throw null!;
        public static T Throws<T>(System.Action testCode) where T : System.Exception => throw null!;
        public static System.Exception Throws(System.Type exceptionType, System.Func<object> testCode) => throw null!;
    }
}
""";

    private const string CallSource = """
namespace dotnet.text.regularexpressions.Tests
{
    public class CallTests
    {
        public void M()
        {
            Widget widget = new Widget();
            AssertExtensions.Throws<System.ArgumentException>("p", () => widget.set_Timeout(1));
            AssertExtensions.Throws<System.ArgumentNullException>("p", "b", () => widget.set_Timeout(1));
        }
    }
}
""";

    // 其他项目里的同名类型：让合并整树编译的 AssertExtensions 绑定歧义（Symbol=null）。
    private const string DecoySource = """
namespace System
{
    public static class AssertExtensions
    {
        public static T Throws<T>(string paramName, System.Action testCode) where T : System.Exception => throw null!;
    }
}
""";

    private const string MergedGlobalUsings = """
global using System;
global using System.Tests;
""";

    [Fact]
    public void ScopedBindingIncludesProjectReferenceClosure_OverloadCallSiteGetsMatchingRename()
    {
        var tempRoot = Directory.CreateTempSubdirectory("cs2j-jk-closure").FullName;
        try
        {
            const string libProject = "lib/lib.csproj";
            const string testsProject = "tests/tests.csproj";
            const string otherProject = "other/other.csproj";

            var widgetPath = WriteFile(tempRoot, "lib/Widget.cs", WidgetSource);
            var assertsPath = WriteFile(tempRoot, "tests/AssertExtensions.cs", AssertExtensionsSource);
            var callPath = WriteFile(tempRoot, "tests/CallTests.cs", CallSource);
            WriteFile(tempRoot, "other/Decoy.cs", DecoySource);

            WriteFile(tempRoot, libProject, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
            WriteFile(tempRoot, testsProject, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="System.Tests" />
    <ProjectReference Include="..\lib\lib.csproj" />
  </ItemGroup>
</Project>
""");
            WriteFile(tempRoot, otherProject, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");

            var options = new CSharpParseOptions(LanguageVersion.Latest);
            var sourceList = new (SyntaxTree Tree, string Text)[]
            {
                (CSharpSyntaxTree.ParseText(WidgetSource, options, path: widgetPath), WidgetSource),
                (CSharpSyntaxTree.ParseText(AssertExtensionsSource, options, path: assertsPath), AssertExtensionsSource),
                (CSharpSyntaxTree.ParseText(CallSource, options, path: callPath), CallSource),
                (CSharpSyntaxTree.ParseText(DecoySource, options, path: Path.Combine(tempRoot, "other", "Decoy.cs")), DecoySource),
                (CSharpSyntaxTree.ParseText(MergedGlobalUsings, options,
                    path: Path.Combine(tempRoot, "__cs2j_global_usings.g.cs")), MergedGlobalUsings),
            };
            var trees = sourceList.Select(s => s.Tree).ToArray();

            var compilation = CSharpCompilation.Create("jk-scoped-closure-repro",
                trees,
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            // 前提自证：合并整树编译里调用点的 AssertExtensions 绑定歧义（Symbol=null）。
            var callTree = trees[2];
            var model = compilation.GetSemanticModel(callTree);
            var firstName = callTree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>().First();
            var info = model.GetSymbolInfo(firstName);
            _output.WriteLine($"merged GetSymbolInfo: Symbol={info.Symbol?.ToDisplayString() ?? "<null>"}, " +
                $"Reason={info.CandidateReason}");
            Assert.Null(info.Symbol);

            var layout = new ProjectPreprocessSourceLayout(
                tempRoot,
                tempRoot,
                new[]
                {
                    Path.Combine(tempRoot, "lib", "lib.csproj"),
                    Path.Combine(tempRoot, "tests", "tests.csproj"),
                    Path.Combine(tempRoot, "other", "other.csproj"),
                });

            var fallback = JavaKeywordsPassAdapter.BuildProjectScopedBindingFallback(
                compilation, layout, tempRoot);
            var scopedModel = fallback.GetScopedModel(callTree);
            Assert.NotNull(scopedModel);
            var scopedInfo = scopedModel!.GetSymbolInfo(firstName);
            _output.WriteLine($"scoped GetSymbolInfo: Symbol={scopedInfo.Symbol?.ToDisplayString() ?? "<null>"}, " +
                $"Reason={scopedInfo.CandidateReason}, " +
                $"Candidates=[{string.Join(" | ", scopedInfo.CandidateSymbols.Select(c => c.ToDisplayString()))}]");

            var preprocessor = new JavaReservedWordPreprocessor();
            preprocessor.BindCompilation(compilation);
            preprocessor.SetScopedBindingFallback(fallback);

            var stats = new JavaKeywordStatistics();
            var diagnostics = new List<JavaKeywordDiagnostic>();
            var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tree, text) in sourceList)
            {
                outputs[tree.FilePath] = preprocessor
                    .RewriteFile(text, compilation.GetSemanticModel(tree), stats, diagnostics)
                    .OutputCode;
            }
            foreach (var diagnostic in diagnostics)
                _output.WriteLine($"DIAG: {diagnostic.Code} {diagnostic.Message}");
            _output.WriteLine($"--- call ---{Environment.NewLine}{outputs[callPath]}");

            // 声明侧：找出 (string, Action) 与 (string, string, Action) 重载改名后的新名。
            var actionName = FindNewName(outputs[assertsPath], parameterCount: 2, delegateName: "Action");
            var actionWithMessageName = FindNewName(outputs[assertsPath], parameterCount: 3, delegateName: "Action");
            _output.WriteLine($"expected (string, Action) -> {actionName}; (string, string, Action) -> {actionWithMessageName}");

            var callOut = outputs[callPath];
            Assert.DoesNotContain("Throws<", callOut);
            Assert.Contains($"{actionName}<", callOut);
            Assert.Contains($"{actionWithMessageName}<", callOut);

            // 调用点改写后与声明侧必须能编译（修复前会命中 Func<object> 重载 → CS1662/CS0029）。
            var verifyTrees = new[]
            {
                CSharpSyntaxTree.ParseText(outputs[widgetPath], options, path: widgetPath),
                CSharpSyntaxTree.ParseText(outputs[assertsPath], options, path: assertsPath),
                CSharpSyntaxTree.ParseText(outputs[callPath], options, path: callPath),
                CSharpSyntaxTree.ParseText(MergedGlobalUsings, options,
                    path: Path.Combine(tempRoot, "__cs2j_global_usings.g.cs")),
            };
            var verify = CSharpCompilation.Create("jk-scoped-closure-verify",
                verifyTrees,
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            var errors = verify.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Descriptor.Id + ": " + d.GetMessage())
                .ToList();
            _output.WriteLine($"verify errors: {errors.Count}");
            Assert.True(errors.Count == 0,
                "rewritten compilation has errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    /// <summary>从改名后的声明里按参数个数与委托类型找出方法的新名。</summary>
    private static string FindNewName(string code, int parameterCount, string delegateName)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.ParameterList.Parameters.Count != parameterCount) continue;
            var lastType = method.ParameterList.Parameters.Last().Type?.ToString() ?? string.Empty;
            if (lastType.Contains(delegateName, StringComparison.Ordinal))
                return method.Identifier.Text;
        }
        throw new InvalidOperationException($"no renamed overload with {parameterCount} params / {delegateName}");
    }

    private static string WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }
}
