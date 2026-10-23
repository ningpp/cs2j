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
/// java-keywords 阶段：带 <c>where T : ...</c> 约束的泛型方法（如 xUnit 断言助手中的
/// <c>Throws&lt;T&gt;</c>，decap 后命中 Java 关键字 throws）声明与调用点必须一致改名。
/// 声明被改名（Throws → ThrowsValue…）而调用点残留会直接产生 CS0117。
/// 复现 JavaKeywordsPassAdapter 的单编译多文件驱动方式。
/// </summary>
public class JavaKeywordGenericCallSiteRenameTests
{
    private readonly ITestOutputHelper _output;
    public JavaKeywordGenericCallSiteRenameTests(ITestOutputHelper output) => _output = output;

    private static IEnumerable<MetadataReference> GetReferences()
    {
        // 仅引用 CoreLib 同目录的托管运行时程序集：trusted 集合里的转换器自身程序集
        // （如 Cs2jPrelude 的 dotnet.System 命名空间）会劫持 using 解析，掩盖真实管线行为。
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

    private static (string DeclCode, string CallCode) Sources => (
        DeclCode: """
namespace X
{
    public class AssertExtensions
    {
        public static T Throws<T>(string paramName, System.Action testCode) where T : System.ArgumentException
            => throw null;
        public static T Throws<T>(string paramName, string beginsWith, System.Action testCode) where T : System.ArgumentException
            => throw null;
        public static void ThrowsAny<T1, T2>(System.Action testCode) where T1 : System.Exception where T2 : System.Exception
            => throw null;
    }
}
""",
        CallCode: """
namespace Y
{
    using X;
    public class Tests
    {
        public void M()
        {
            AssertExtensions.Throws<System.ArgumentException>("p", null);
            AssertExtensions.Throws<System.ArgumentException>("p", "b", null);
            AssertExtensions.ThrowsAny<System.ArgumentException, System.ArgumentException>(null);
        }
    }
}
""");

    [Fact]
    public void GenericMethodCallSites_RenamedTogetherWithDeclaration()
    {
        var (declCode, callCode) = Sources;
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var declTree = CSharpSyntaxTree.ParseText(declCode, options, path: "Decl.cs");
        var callTree = CSharpSyntaxTree.ParseText(callCode, options, path: "Call.cs");

        var compilation = CSharpCompilation.Create("jk-generic-repro",
            new[] { declTree, callTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var pp = new JavaReservedWordPreprocessor();
        pp.BindCompilation(compilation);
        var stats = new JavaKeywordStatistics();
        var diags = new List<JavaKeywordDiagnostic>();

        var declResult = pp.RewriteFile(declCode, compilation.GetSemanticModel(declTree), stats, diags);
        var callResult = pp.RewriteFile(callCode, compilation.GetSemanticModel(callTree), stats, diags);

        _output.WriteLine($"--- DECL ---{System.Environment.NewLine}{declResult.OutputCode}");
        _output.WriteLine($"--- CALL ---{System.Environment.NewLine}{callResult.OutputCode}");

        // 声明侧命中：Throws (decap → throws) 必须改名
        Assert.Contains("ThrowsValue", declResult.OutputCode);

        // 调用点必须同步改名，不得残留 Throws<；ThrowsAny（decap 非 Java 关键字）不改名
        Assert.DoesNotContain("Throws<", callResult.OutputCode);
        Assert.Contains("ThrowsAny<", callResult.OutputCode);
        Assert.Contains("ThrowsValue<", callResult.OutputCode);
        Assert.Contains("ThrowsValue2<", callResult.OutputCode);

        // 改名后的双文件编译不得引入 CS0117/CS0103/CS7036 类调用点错误
        var rewrittenDecl = CSharpSyntaxTree.ParseText(declResult.OutputCode, options, path: "Decl.cs");
        var rewrittenCall = CSharpSyntaxTree.ParseText(callResult.OutputCode, options, path: "Call.cs");
        var candidate = CSharpCompilation.Create("jk-generic-repro-verify",
            new[] { rewrittenDecl, rewrittenCall },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Descriptor.Id + ": " + d.GetMessage())
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten compilation has errors:" + System.Environment.NewLine + string.Join(System.Environment.NewLine, errors));
    }
}
