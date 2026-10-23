using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// java-keywords 阶段跨项目同名辅助类场景：合并整树编译里 xml-tiny 的
/// <c>System.AssertExtensions</c> 与 regex tests 的 <c>System.Tests.AssertExtensions</c>
/// 在 <c>using System; using System.Tests;</c> 下对调用点形成 CS0179 歧义绑定，
/// 声明侧 Throws→ThrowsValueN 改名后调用点全部残留（真实 SDK 上 147 处 CS0117）。
/// 修复后：绑定歧义时应按「同项目子树」亲和挑出唯一候选并同步改名。
/// </summary>
public class JavaKeywordAmbiguousCallSiteRenameTests
{
    private readonly ITestOutputHelper _output;
    public JavaKeywordAmbiguousCallSiteRenameTests(ITestOutputHelper output) => _output = output;

    private static IEnumerable<MetadataReference> GetReferences()
    {
        // 仅引用 CoreLib 同目录的运行时程序集：trusted 集合里的转换器自身程序集
        // （如 Cs2jPrelude 的 dotnet.System 命名空间）会劫持 using 解析，掩盖真实管线行为。
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var assembly in Directory.EnumerateFiles(runtimeDir, "*.dll"))
        {
            yield return MetadataReference.CreateFromFile(assembly);
        }
    }

    // 三个文件模拟真实树：regex 项目内声明 + regex 项目内调用 + 其他项目同名类。
    private static readonly (string Path, string Code)[] Sources =
    {
        ("xml-tiny/Tests/XmlDocument/Common/AssertExtensions.cs", """
namespace System
{
    internal static class AssertExtensions
    {
        public static void Throws<T>(System.Action action, string message) where T : System.Exception => throw null!;
        public static T Throws<T>(string paramName, System.Action action) where T : System.ArgumentException => throw null!;
    }
}
"""),
        ("regular-expressions/tests/TestUtilities/System/Tests/AssertExtensions.cs", """
namespace System.Tests
{
    public static class AssertExtensions
    {
        public static T Throws<T>(string paramName, System.Action testCode) where T : System.ArgumentException => throw null!;
        public static T Throws<T>(string paramName, string beginsWith, System.Action testCode) where T : System.ArgumentException => throw null!;
    }
}
"""),
        ("regular-expressions/tests/Regex.Cache.Tests.cs", """
namespace dotnet.text.regularexpressions.Tests
{
    using System;
    using System.Tests;
    using Xunit;
    public class RegexCacheTests
    {
        public void M()
        {
            AssertExtensions.Throws<ArgumentException>("p", null);
            AssertExtensions.Throws<ArgumentException>("p", "b", null);
        }
    }
}
"""),
    };

    private const string GlobalUsings = """
global using System;
global using System.Tests;
""";

    [Fact]
    public void AmbiguousBindingCallSites_RenamedByProjectAffinity()
    {
        // 在真实文件系统的临时目录里还原 SDK 布局：作用域化回退按「最近的 csproj
        // 祖先目录」分区，因此需要 dummy csproj 把两个 regular-expressions 文件
        // 划进同一分区、xml-tiny 文件划到另一分区。
        var tempRoot = Directory.CreateTempSubdirectory("cs2j-jk-ambiguous").FullName;
        try
        {
            var paths = new Dictionary<string, string>();
            foreach (var (path, code) in Sources)
            {
                var full = Path.Combine(tempRoot, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, code);
                paths[path] = full;
            }
            Directory.CreateDirectory(Path.Combine(tempRoot, "regular-expressions", "tests"));
            File.WriteAllText(
                Path.Combine(tempRoot, "regular-expressions", "tests", "dotnet.text.regularexpressions.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Directory.CreateDirectory(Path.Combine(tempRoot, "xml-tiny", "Tests", "XmlDocument", "Common"));
            File.WriteAllText(
                Path.Combine(tempRoot, "xml-tiny", "Tests", "XmlDocument", "Common", "dotnet-xml.document-tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var options = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = Sources.Select(s => CSharpSyntaxTree.ParseText(s.Code, options, path: paths[s.Path])).ToList();
            trees.Add(CSharpSyntaxTree.ParseText(GlobalUsings, options, path: Path.Combine(tempRoot, "__globalusings.cs")));

            var compilation = CSharpCompilation.Create("jk-ambiguous-repro",
                trees,
                GetReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            // 绑定现状自证：调用点在合并编译里确实歧义（否则本测试前提不成立）。
            var callTree = trees.First(t => t.FilePath.EndsWith("Regex.Cache.Tests.cs"));
            var model = compilation.GetSemanticModel(callTree);
            var generic = callTree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax>().First();
            var info = model.GetSymbolInfo(generic);
            _output.WriteLine($"GetSymbolInfo: Symbol={info.Symbol?.ToDisplayString() ?? "<null>"}, " +
                $"Reason={info.CandidateReason}, Candidates={string.Join(" | ", info.CandidateSymbols.Select(c => c.ToDisplayString()))}");

            // 诊断：作用域模型在同一节点上的绑定
            var fallback = TopLevelPartitionScopedBindingFallback.Build(compilation);
            _output.WriteLine($"callPartition='{TopLevelPartitionScopedBindingFallback.PartitionKey(callTree.FilePath)}'");
            _output.WriteLine($"declPartition='{TopLevelPartitionScopedBindingFallback.PartitionKey(paths["regular-expressions/tests/TestUtilities/System/Tests/AssertExtensions.cs"])}'");
            var scopedModel = fallback.GetScopedModel(callTree);
            _output.WriteLine($"scopedModel null? {scopedModel == null}");
            if (scopedModel != null)
            {
                var sInfo = scopedModel.GetSymbolInfo(generic);
                _output.WriteLine($"scopedInfo: Symbol={sInfo.Symbol?.ToDisplayString() ?? "<null>"} Reason={sInfo.CandidateReason} " +
                    $"Cands=[{string.Join("|", sInfo.CandidateSymbols.Select(c => c.ToDisplayString()))}]");
            }

            var pp = new CSharpToJava.Core.JavaKeywords.JavaReservedWordPreprocessor();
            pp.BindCompilation(compilation);
            var stats = new JavaKeywordStatistics();
            var diags = new List<JavaKeywordDiagnostic>();

            var outputs = new Dictionary<string, string>();
            foreach (var (path, code) in Sources)
            {
                var tree = trees.First(t => t.FilePath == paths[path]);
                var result = pp.RewriteFile(code, compilation.GetSemanticModel(tree), stats, diags);
                outputs[path] = result.OutputCode;
                _output.WriteLine($"RewriteFile {path}: Changed={result.Changed}");
            }
            foreach (var d in diags)
                _output.WriteLine($"DIAG: {d.Code} {d.Message}");

            _output.WriteLine($"--- regex call file ---{System.Environment.NewLine}{outputs["regular-expressions/tests/Regex.Cache.Tests.cs"]}");

            // 声明改名照旧发生
            Assert.Contains("ThrowsValue", outputs["regular-expressions/tests/TestUtilities/System/Tests/AssertExtensions.cs"]);

            // 修复点：同项目子树歧义消解后调用点必须同步改名，不得残留
            var callOut = outputs["regular-expressions/tests/Regex.Cache.Tests.cs"];
            Assert.DoesNotContain("Throws<", callOut);
            Assert.Contains("ThrowsValue", callOut);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
